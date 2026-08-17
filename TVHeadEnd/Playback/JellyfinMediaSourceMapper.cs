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
        /// <returns>An unopened media source.</returns>
        public static MediaSourceInfo CreatePending(
            string channelId,
            VariantOffer offer,
            ChannelMediaDescriptor? native,
            ChannelMediaDescriptor? observedVariant = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            var source = CreateShell(channelId, offer);

            // Without streams Jellyfin has nothing to evaluate the device profile against. What
            // may be claimed here differs sharply between the two cases: for the broadcast it is
            // an observation, for a compatibility output that has never run it is only what the
            // role contract guarantees.
            var descriptor = offer.Variant == PlaybackVariant.Native
                ? native
                : observedVariant ?? ProjectFromContract(offer.Variant, native);

            descriptor?.ApplyTo(source);
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

            // What the roles are actually for: video a client can decode. The container is the
            // means, not the end -- measured on a real installation, the Matroska output carries
            // more of the original audio tracks than the transport stream one did, and the
            // buffer indexes entry points for either.
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
                SupportsDirectPlay = offer.SupportsDirectPlay,
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
