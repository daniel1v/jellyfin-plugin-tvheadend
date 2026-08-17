using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TVHeadEnd.Media
{
    /// <summary>
    /// Establishes what unknown channels are, while the channel list is being refreshed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional, and off by default. Without it a channel is analysed the first time somebody
    /// watches it, which costs that viewer a couple of seconds once; with it the cost is paid in
    /// the background and playback negotiation can offer the right variants from the very first
    /// tune. Which is worth more depends on how many channels there are and how many tuners.
    /// </para>
    /// <para>
    /// Only the native profile is ever opened. A compatibility profile would start a transcoder
    /// on the TVHeadend server for a channel nobody is watching, and what it produces is not
    /// what the analysis is asking about anyway.
    /// </para>
    /// </remarks>
    public sealed class ChannelFormatPreAnalyzer
    {
        private readonly ChannelMediaDescriptorStore _descriptors;
        private readonly ILogger _logger;

        private int _running;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelFormatPreAnalyzer"/> class.
        /// </summary>
        /// <param name="descriptors">Where results are stored.</param>
        /// <param name="logger">The logger.</param>
        public ChannelFormatPreAnalyzer(ChannelMediaDescriptorStore descriptors, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(descriptors);
            ArgumentNullException.ThrowIfNull(logger);

            _descriptors = descriptors;
            _logger = logger;
        }

        /// <summary>
        /// Analyses every channel nothing current is known about.
        /// </summary>
        /// <remarks>
        /// One channel at a time. Each one occupies a tuner for the seconds it takes, and a
        /// refresh that seized every tuner at once would take live TV away from whoever is
        /// watching. A failure is logged and skipped: a channel that cannot be analysed stays
        /// unknown, which is a state the playback policy handles, and a channel refresh must
        /// never fail because of it.
        /// </remarks>
        /// <param name="channelIds">The channels TVHeadend currently offers.</param>
        /// <param name="nativeProfile">The stream profile the analysis reads through.</param>
        /// <param name="analyzeOne">Opens one channel and returns what it contains.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>How many channels were analysed.</returns>
        public async Task<int> Run(
            IReadOnlyCollection<string> channelIds,
            string? nativeProfile,
            Func<string, CancellationToken, Task<ChannelMediaDescriptor?>> analyzeOne,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(channelIds);
            ArgumentNullException.ThrowIfNull(analyzeOne);

            // A refresh can overlap with the previous one. Letting both run would double the
            // tuner pressure for no gain, so the second simply steps aside.
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                _logger.LogDebug("TVHeadend channel analysis: a previous run is still going, skipping this one");
                return 0;
            }

            try
            {
                var pending = channelIds
                    .Where(channelId => !string.IsNullOrEmpty(channelId))
                    .Where(channelId => _descriptors.NeedsAnalysis(channelId, nativeProfile))
                    .ToList();
                if (pending.Count == 0)
                {
                    return 0;
                }

                _logger.LogInformation(
                    "TVHeadend channel analysis: {Count} channel(s) have no current description, analysing them one at a time",
                    pending.Count);

                var stopwatch = Stopwatch.StartNew();
                var analysed = 0;
                foreach (var channelId in pending)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation(
                            "TVHeadend channel analysis: stopped after {Analysed} of {Count} channel(s)",
                            analysed,
                            pending.Count);
                        break;
                    }

                    try
                    {
                        var descriptor = await analyzeOne(channelId, cancellationToken).ConfigureAwait(false);
                        if (descriptor is not null)
                        {
                            _descriptors.Record(descriptor);
                            analysed++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        // Never fatal. The channel stays unknown and is analysed on first
                        // playback instead, exactly as it would be with this feature switched off.
                        _logger.LogWarning(
                            exception,
                            "TVHeadend channel analysis: channel {ChannelId} could not be analysed",
                            channelId);
                    }
                }

                _logger.LogInformation(
                    "TVHeadend channel analysis: described {Analysed} of {Count} channel(s) in {ElapsedSeconds:N1} s",
                    analysed,
                    pending.Count,
                    stopwatch.Elapsed.TotalSeconds);

                return analysed;
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }
}
