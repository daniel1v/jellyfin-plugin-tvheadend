using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using TVHeadEnd.Media;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Playback
{
    /// <summary>
    /// Builds the <see cref="MediaSourceInfo"/> objects Jellyfin negotiates and plays with.
    /// </summary>
    /// <remarks>
    /// The one place that turns this plugin's vocabulary -- a channel, a variant, what a source
    /// was observed to contain -- into Jellyfin's, which is what makes it possible to state and
    /// check that a description matches what is actually delivered.
    /// </remarks>
    public static class JellyfinMediaSourceMapper
    {
        private const int AnalyzeDurationMs = 2000;

        /// <summary>
        /// Builds the source offered during playback negotiation, before anything is opened.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="offer">Which variant, and whether a client may play it directly.</param>
        /// <param name="native">What the broadcast was observed to be, or <see langword="null"/>.</param>
        /// <param name="observedVariant">
        /// What this variant's output was observed to be on an earlier open, or
        /// <see langword="null"/> if it has never been produced.
        /// </param>
        /// <param name="itemId">
        /// The channel's own item identifier, to be used for the variant offered first. Clients
        /// that do not choose a source send this back as the media source identifier, and a
        /// source has to answer to it or nothing opens at all.
        /// </param>
        /// <param name="describeStreams">
        /// Whether to attach what the source contains. Only worth doing when more than one variant
        /// is offered and Jellyfin has to choose between them.
        /// </param>
        /// <returns>An unopened media source.</returns>
        public static MediaSourceInfo CreatePending(
            string channelId,
            VariantOffer offer,
            ChannelMediaDescriptor? native,
            ChannelMediaDescriptor? observedVariant = null,
            string? itemId = null,
            bool describeStreams = false)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            var source = CreateShell(channelId, offer);
            if (!string.IsNullOrEmpty(itemId))
            {
                source.Id = itemId;
            }

            // Streams are attached only when there is a choice to make. Jellyfin evaluates the
            // device profile as soon as it has something to evaluate, and an unopened source has
            // no path yet -- it reports the bare server address -- so that evaluation can only
            // end in a direct play error and a decision to transcode. With nothing to go on,
            // Jellyfin opens the source first and judges the real thing.
            if (describeStreams)
            {
                var descriptor = offer.Variant == PlaybackVariant.Native
                    ? native
                    : observedVariant ?? ProjectFromContract(offer.Variant, native);

                descriptor?.ApplyTo(source);
                PreferWidelyDecodableAudio(source);
            }

            source.Name = DescribeVariant(offer.Variant);
            return source;
        }

        /// <summary>
        /// Builds the source handed back once a variant has been opened.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="offer">Which variant, and whether a client may play it directly.</param>
        /// <param name="descriptor">What the opened stream was observed to contain.</param>
        /// <param name="bufferPath">The buffer file the stream is readable from.</param>
        /// <param name="encoderUrl">The URL the buffer is also readable at.</param>
        /// <returns>An opened media source.</returns>
        public static MediaSourceInfo CreateOpened(
            string channelId,
            VariantOffer offer,
            ChannelMediaDescriptor? descriptor,
            string bufferPath,
            string encoderUrl)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);
            ArgumentException.ThrowIfNullOrEmpty(bufferPath);
            ArgumentException.ThrowIfNullOrEmpty(encoderUrl);

            var source = CreateShell(channelId, offer);
            descriptor?.ApplyTo(source);
            PreferWidelyDecodableAudio(source);
            source.Name = DescribeVariant(offer.Variant);
            source.RequiresOpening = false;

            // The buffer is exposed as a local file rather than an HTTP source because Jellyfin's
            // Android client treats every HTTP direct-play source as an HLS playlist. Its static
            // request then receives the MPEG-TS stream directly. The HTTP form is still supplied
            // as the encoder path, which is what a server-side transcode reads.
            source.Path = bufferPath;
            source.Protocol = MediaProtocol.File;
            source.EncoderPath = encoderUrl;
            source.EncoderProtocol = MediaProtocol.Http;
            source.RequiredHttpHeaders = new Dictionary<string, string>();

            // Now there is something to play, and the policy decides whether this variant may be
            // handed to a client unmodified.
            source.SupportsDirectPlay = offer.SupportsDirectPlay;

            // A channel has no runtime, whatever the buffer happens to hold.
            source.RunTimeTicks = null;
            source.Size = null;
            source.DefaultSubtitleStreamIndex = null;

            return source;
        }

        /// <summary>
        /// Reports whether an observed compatibility output satisfies the contract of its role.
        /// </summary>
        /// <remarks>
        /// Both roles promise MPEG-TS with H.264 video. A profile that copies the video, or that
        /// produces a different container, silently defeats the purpose of the variant, and a
        /// client would be handed something no better than the broadcast. Rather than trust the
        /// configuration, the output is checked once and the role is dropped for that channel if
        /// it does not hold.
        /// </remarks>
        /// <param name="variant">The role the output was produced for.</param>
        /// <param name="observed">What the output turned out to be.</param>
        /// <returns>Whether the output may be offered as that role.</returns>
        public static bool SatisfiesContract(PlaybackVariant variant, ChannelMediaDescriptor? observed)
        {
            if (variant == PlaybackVariant.Native)
            {
                return true;
            }

            if (observed is not { IsUsable: true })
            {
                return false;
            }

            // MPEG-TS, and nothing else. Jellyfin serves a direct-played live stream with a
            // hardcoded content type of video/mp2t -- its own source says so, TODO and all:
            //
            //     return File(liveStream, MimeTypes.GetMimeType("file.ts"));
            //
            // A client that believes the declared type, as players reasonably do, finds no sync
            // byte in a Matroska body and never renders a frame. The plugin cannot correct the
            // declaration, so the content has to match it.
            if (!observed.IsTransportStream
                || observed.Container?.Contains("mpegts", StringComparison.OrdinalIgnoreCase) != true)
            {
                return false;
            }

            if (!string.Equals(observed.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // The whole point of the normalizing role is that a decoder can cold-start on it, and
            // an output that merely re-wraps the same recovery-point video does not qualify. Only
            // a transport stream can be checked for that here; for any other container the claim
            // rests on the role contract, because the NAL scanner needs PMT-declared stream types
            // and there are none.
            return variant != PlaybackVariant.H264IdrNormalization
                || !observed.IsTransportStream
                || observed.RandomAccess == H264RandomAccessKind.Idr;
        }

        /// <summary>
        /// Marks the audio track the widest range of clients can decode.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A preference, which is why it lives in the mapping and not in the analysis: the
        /// descriptor keeps every track exactly as the broadcast carries them, and this only
        /// says which one a client should reach for first.
        /// </para>
        /// <para>
        /// It matters more than it looks. A German broadcast typically carries MPEG audio first
        /// and a Dolby track after it, and devices without an MP2 decoder are common -- so
        /// without this, Jellyfin selects the first track, finds the client cannot decode it, and
        /// transcodes a stream whose video it was perfectly happy to pass through.
        /// </para>
        /// <para>
        /// The order of the list is deliberately left alone. Jellyfin's
        /// <c>EncodingHelper.GetMapArgs</c> addresses the track it wants FFmpeg to copy by its
        /// position, so reordering makes <c>-map</c> point at something other than the track the
        /// manifest describes.
        /// </para>
        /// </remarks>
        /// <param name="source">The media source to mark up.</param>
        public static void PreferWidelyDecodableAudio(MediaSourceInfo source)
        {
            ArgumentNullException.ThrowIfNull(source);

            var audio = source.MediaStreams.Where(stream => stream.Type == MediaStreamType.Audio).ToList();
            if (audio.Count == 0)
            {
                return;
            }

            string[] preferred = ["aac", "ac3", "eac3", "mp3"];
            var chosen = preferred
                .Select(codec => audio.FirstOrDefault(stream => string.Equals(stream.Codec, codec, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(stream => stream is not null)
                ?? audio.FirstOrDefault(stream => stream.IsDefault)
                ?? audio[0];

            foreach (var stream in audio)
            {
                stream.IsDefault = ReferenceEquals(stream, chosen);
            }

            source.DefaultAudioStreamIndex = chosen.Index;
        }

        /// <summary>
        /// Names a variant for the source list a client shows.
        /// </summary>
        /// <param name="variant">The variant.</param>
        /// <returns>The display name, or <see langword="null"/> for the broadcast.</returns>
        public static string? DescribeVariant(PlaybackVariant variant)
            => variant switch
            {
                PlaybackVariant.Mpeg2H264Compatibility => "H.264 compatibility",
                PlaybackVariant.H264IdrNormalization => "H.264 (IDR normalized)",
                _ => null,
            };

        private static MediaSourceInfo CreateShell(string channelId, VariantOffer offer)
            => new()
            {
                Id = PlaybackVariantId.Create(channelId, offer.Variant),
                Path = null,
                Protocol = MediaProtocol.Http,
                AnalyzeDurationMs = AnalyzeDurationMs,
                Container = SourceContainer.TransportStream,
                IsInfiniteStream = true,
                RequiresOpening = true,
                RequiresClosing = true,

                // Never on an unopened source. It has no path yet, only the promise of one, and
                // Jellyfin's Android client answers direct play on an HTTP source by parsing the
                // URL as an HLS playlist -- which is why the opened source is published as a
                // local file. Whether this variant may be played directly at all is decided when
                // it is opened, where there is something real to play.
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true,
                MediaStreams = [],
            };

        /// <summary>
        /// Derives what a compatibility role guarantees, for a variant that has never been
        /// produced for this channel.
        /// </summary>
        /// <remarks>
        /// Only what the contract in the documentation actually promises: MPEG-TS, H.264 video,
        /// and the source geometry. Profile, level, bitrate and interlace flags are deliberately
        /// left unset rather than guessed -- a client makes decisions on those, and a wrong claim
        /// here is worse than an absent one. The real values replace these once the variant has
        /// been opened and observed.
        /// </remarks>
        private static ChannelMediaDescriptor? ProjectFromContract(
            PlaybackVariant variant,
            ChannelMediaDescriptor? native)
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

                    // Guaranteed for the normalizing role, which must preserve the frame rate.
                    // The MPEG-2 role only promises geometry, so nothing is claimed there.
                    RealFrameRate = variant == PlaybackVariant.H264IdrNormalization ? video.RealFrameRate : null,
                    AverageFrameRate = variant == PlaybackVariant.H264IdrNormalization ? video.AverageFrameRate : null,
                },
            };

            // Audio follows the configured TVHeadend profile and is not part of the contract, so
            // the tracks are carried over as the best available statement and corrected the first
            // time the variant is actually produced.
            foreach (var audio in native.Streams.Where(stream => stream.Type == MediaStreamType.Audio))
            {
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = streams.Count,
                    Codec = audio.Codec,
                    Language = audio.Language,
                    Channels = audio.Channels,
                    ChannelLayout = audio.ChannelLayout,
                    SampleRate = audio.SampleRate,
                    Title = audio.Title,
                });
            }

            return new ChannelMediaDescriptor
            {
                ChannelId = native.ChannelId,
                VariantRole = variant.ToString(),
                NativeProfile = native.NativeProfile,
                ProgramSignature = native.ProgramSignature,
                Container = SourceContainer.TransportStream,
                Streams = streams,
                IsTransportStream = true,
                RandomAccess = variant == PlaybackVariant.H264IdrNormalization
                    ? H264RandomAccessKind.Idr
                    : H264RandomAccessKind.Unknown,
            };
        }
    }
}
