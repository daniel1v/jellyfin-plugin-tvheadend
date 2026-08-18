using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using TVHeadEnd.Media;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Playback
{
    /// <summary>
    /// Builds the <see cref="MediaSourceInfo"/> objects Jellyfin negotiates and plays with.
    /// </summary>
    /// <remarks>
    /// The one place that turns this plugin's vocabulary -- a channel, a profile role, what a
    /// source was observed to contain -- into Jellyfin's. It states facts and stops there: which
    /// of the offered sources a client can actually play is Jellyfin's decision, made against the
    /// device profile the client itself sent.
    /// </remarks>
    public static class JellyfinMediaSourceMapper
    {
        private const int AnalyzeDurationMs = 2000;

        /// <summary>
        /// Builds a source offered during playback negotiation, before anything is opened.
        /// </summary>
        /// <remarks>
        /// <see cref="MediaSourceInfo.SupportsDirectPlay"/> is set on every candidate. It does not
        /// claim the client can play it -- it says Jellyfin may evaluate it against the device
        /// profile, which is the only way a compatibility source can ever be chosen. Jellyfin
        /// overwrites the flag with its own verdict before the client sees it.
        /// </remarks>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="role">Which form of the channel this offers.</param>
        /// <param name="descriptor">
        /// What the source is expected to contain, or <see langword="null"/> when nothing is
        /// known and there is nothing honest to say.
        /// </param>
        /// <returns>An unopened media source.</returns>
        public static MediaSourceInfo CreatePending(
            string channelId,
            StreamProfileRole role,
            ChannelMediaDescriptor? descriptor)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            var source = CreateShell(channelId, role);
            descriptor?.ApplyTo(source);
            source.Container = NormalizeContainer(ContainerOf(role, descriptor));
            source.Name = DescribeRole(role);
            return source;
        }

        /// <summary>
        /// Builds the source handed back once a role has been opened.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="role">Which form of the channel was opened.</param>
        /// <param name="descriptor">What the opened stream was observed to contain.</param>
        /// <param name="mediaPath">The file the stream is readable from.</param>
        /// <param name="streamUrl">The URL Jellyfin serves the open stream at.</param>
        /// <param name="container">
        /// What the stream is delivered in, as a file extension. It has to be what actually
        /// arrives: the URL ends in it, and a client that believes the declaration and finds
        /// something else renders nothing.
        /// </param>
        /// <returns>An opened media source.</returns>
        public static MediaSourceInfo CreateOpened(
            string channelId,
            StreamProfileRole role,
            ChannelMediaDescriptor? descriptor,
            string mediaPath,
            string streamUrl,
            string container)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);
            ArgumentException.ThrowIfNullOrEmpty(mediaPath);
            ArgumentException.ThrowIfNullOrEmpty(streamUrl);
            ArgumentException.ThrowIfNullOrEmpty(container);

            var source = CreateShell(channelId, role);
            descriptor?.ApplyTo(source);
            source.Name = DescribeRole(role);
            source.RequiresOpening = false;

            if (role == StreamProfileRole.Native)
            {
                source.Path = mediaPath;
                source.Protocol = MediaProtocol.File;
                source.EncoderPath = streamUrl;
                source.EncoderProtocol = MediaProtocol.Http;
            }
            else
            {
                // A compatibility rendering is served through Jellyfin's live stream file
                // endpoint, which takes its content type from the container in the URL. The
                // direct-play route declares every live stream video/mp2t regardless, which a
                // Matroska body cannot survive.
                source.Path = streamUrl;
                source.Protocol = MediaProtocol.Http;
                source.EncoderPath = streamUrl;
                source.EncoderProtocol = MediaProtocol.Http;
            }

            // The last word on the container, after any descriptor has been applied: what is
            // published has to be what the client will receive.
            source.Container = NormalizeContainer(container);
            source.RequiredHttpHeaders = new Dictionary<string, string>();

            // A channel has no runtime, whatever has been received so far.
            source.RunTimeTicks = null;
            source.Size = null;
            source.DefaultSubtitleStreamIndex = null;

            return source;
        }

        /// <summary>
        /// Reports whether an observed compatibility output keeps the promise of its role.
        /// </summary>
        /// <remarks>
        /// Rather than trust the configuration, the output is checked once. A profile that copies
        /// the video, or produces a different container, silently defeats the purpose of the role
        /// and would hand a client something no better than the broadcast.
        /// </remarks>
        /// <param name="role">The role the output was produced for.</param>
        /// <param name="observed">What the output turned out to be.</param>
        /// <returns>Whether the output may be served as that role.</returns>
        public static bool SatisfiesContract(StreamProfileRole role, ChannelMediaDescriptor? observed)
        {
            if (role == StreamProfileRole.Native)
            {
                return true;
            }

            if (observed is not { IsUsable: true })
            {
                return false;
            }

            // Matroska, because that is what TVHeadend's transcoder produces -- its libav muxer
            // cannot currently emit MPEG-TS -- and because the role is published as Matroska.
            if (observed.IsTransportStream
                || observed.Container?.Contains("matroska", StringComparison.OrdinalIgnoreCase) != true)
            {
                return false;
            }

            return string.Equals(observed.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Names a container the way a Jellyfin device profile names it.
        /// </summary>
        /// <remarks>
        /// The one place Jellyfin's container vocabulary is allowed to exist. A descriptor records
        /// what FFmpeg reported -- mpegts,ts or matroska,webm -- because that is the fact; a
        /// device profile advertises mpegts and mkv. Jellyfin splits both lists on commas and
        /// compares the parts literally, with no alias resolution, so a source that names only
        /// what FFmpeg said is refused with ContainerNotSupported however well the client could
        /// have played it. Matroska is published as mkv alone: webm is a separate profile entry
        /// and does not stand for an H.264 Matroska stream.
        /// </remarks>
        /// <param name="observed">What the container was observed or declared to be.</param>
        /// <returns>The container to publish, or <see langword="null"/> if it is unrecognised.</returns>
        public static string? NormalizeContainer(string? observed)
        {
            if (string.IsNullOrWhiteSpace(observed))
            {
                return null;
            }

            foreach (var part in observed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // "MPEG-TS" turns up in hand written configuration and in some server responses.
                var name = part.Replace("-", string.Empty, StringComparison.Ordinal);

                if (name.Equals("mpegts", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ts", StringComparison.OrdinalIgnoreCase))
                {
                    return "mpegts,ts";
                }

                if (name.Equals("matroska", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("mkv", StringComparison.OrdinalIgnoreCase))
                {
                    return "mkv";
                }
            }

            // Something neither role produces. Passed through rather than guessed at, so the
            // mismatch shows up as itself instead of as a container that was quietly invented.
            return observed;
        }

        /// <summary>
        /// Describes what a compatibility role guarantees, for an output never yet produced.
        /// </summary>
        /// <remarks>
        /// Only what the role contract promises: Matroska, H.264 video, and the source geometry.
        /// Profile, level, bitrate, frame rate and every audio fact are deliberately left unset
        /// rather than guessed -- a client decides on those, and a wrong claim is worse than an
        /// absent one. The real values replace these once the role has been opened and observed.
        /// </remarks>
        /// <param name="native">What the broadcast was observed to be.</param>
        /// <returns>The projected description, or <see langword="null"/> when there is no basis.</returns>
        public static ChannelMediaDescriptor? ProjectCompatibility(ChannelMediaDescriptor? native)
        {
            if (native is not { IsUsable: true } || native.Video is not { } video)
            {
                return null;
            }

            var streams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",

                    // Guaranteed by the role: geometry is preserved.
                    Width = video.Width,
                    Height = video.Height,
                    AspectRatio = video.AspectRatio,
                },
            };

            // Audio is not part of the contract: a profile may copy the broadcast tracks or
            // re-encode them, and which it does is not knowable from here. Only what survives
            // either choice is stated -- that a track exists, and what it is called.
            foreach (var audio in native.Streams.Where(stream => stream.Type == MediaStreamType.Audio))
            {
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = streams.Count,
                    Language = audio.Language,
                    Title = audio.Title,
                });
            }

            return new ChannelMediaDescriptor
            {
                ChannelId = native.ChannelId,
                NativeProfile = native.NativeProfile,
                ProgramSignature = native.ProgramSignature,
                Container = SourceContainer.Matroska,
                Streams = streams,
                IsTransportStream = false,
            };
        }

        private static string? ContainerOf(StreamProfileRole role, ChannelMediaDescriptor? descriptor)
            => role == StreamProfileRole.Native
                ? descriptor?.Container
                : SourceContainer.Matroska;

        private static string DescribeRole(StreamProfileRole role)
            => role == StreamProfileRole.Native ? "Original" : "H.264 (TVHeadend)";

        private static MediaSourceInfo CreateShell(string channelId, StreamProfileRole role)
            => new()
            {
                Id = ChannelSourceId.Create(channelId, role),
                Path = null,
                Protocol = MediaProtocol.Http,
                AnalyzeDurationMs = AnalyzeDurationMs,
                IsInfiniteStream = true,
                RequiresOpening = true,
                RequiresClosing = true,

                // Set on every candidate, opened or not. This is not a claim that the client can
                // play it; it is what lets Jellyfin evaluate it against the device profile at
                // all, and Jellyfin replaces the flag with its own verdict afterwards. Withheld,
                // a compatibility source could never be chosen and the broadcast would be
                // transcoded instead.
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true,
                MediaStreams = [],
            };
    }
}
