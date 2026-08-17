using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Media
{
    /// <summary>
    /// Assembles a channel descriptor from what the transport layer observed and what the media
    /// analysis found.
    /// </summary>
    /// <remarks>
    /// The two halves answer different questions and neither can answer the other's. FFprobe
    /// reports the elementary streams and their codecs but says nothing about whether a decoder
    /// joining part-way through can start; the transport observation establishes exactly that,
    /// and the PMT stream type that decides which analysis was even meaningful.
    /// </remarks>
    public sealed class ChannelMediaAnalyzer
    {
        private readonly MediaInspector _inspector;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelMediaAnalyzer"/> class.
        /// </summary>
        /// <param name="inspector">The factual media inspector.</param>
        /// <param name="logger">The logger.</param>
        public ChannelMediaAnalyzer(MediaInspector inspector, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(inspector);
            ArgumentNullException.ThrowIfNull(logger);

            _inspector = inspector;
            _logger = logger;
        }

        /// <summary>
        /// Builds the descriptor of a channel.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="nativeProfile">The stream profile the sample was received through.</param>
        /// <param name="samplePath">A local file holding the opening of the stream.</param>
        /// <param name="observation">What the transport layer saw while receiving it.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The descriptor, or <see langword="null"/> when the sample yielded nothing usable.</returns>
        public async Task<ChannelMediaDescriptor?> Analyze(
            string channelId,
            string? nativeProfile,
            string samplePath,
            TransportObservation observation,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);
            ArgumentException.ThrowIfNullOrEmpty(samplePath);

            var inspected = await _inspector.Inspect(
                samplePath,
                $"channel {channelId}",
                SourceContainer.TransportStream,
                cancellationToken).ConfigureAwait(false);
            if (inspected is null)
            {
                return null;
            }

            var descriptor = new ChannelMediaDescriptor
            {
                ChannelId = channelId,
                NativeProfile = nativeProfile,
                ProgramSignature = observation.ProgramSignature,
                Container = inspected.Container,
                Streams = inspected.Streams,
                VideoStreamType = observation.VideoStreamType,
                RandomAccess = observation.RandomAccess,
                IsTransportStream = observation.IsTransportStream,
                Bitrate = inspected.Bitrate,
                Timestamp = inspected.Timestamp,
                VideoType = inspected.VideoType,
                Video3DFormat = inspected.Video3DFormat,
            };

            _logger.LogInformation(
                "TVHeadend channel analysis: {ChannelId} via profile {Profile} -- {Container}, video {Codec} (stream_type 0x{StreamType:x2}), random access {RandomAccess}",
                channelId,
                nativeProfile ?? "<default>",
                descriptor.Container,
                descriptor.VideoCodec ?? "<none>",
                descriptor.VideoStreamType,
                descriptor.RandomAccess);

            return descriptor.IsUsable ? descriptor : null;
        }
    }
}
