using System;
using System.Collections.Generic;
using TVHeadEnd.Media;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Playback
{
    /// <summary>
    /// Turns what is known about a channel into what should be offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every judgement is made from facts about the source, plus -- for the one known client
    /// defect -- whether <see cref="PlaybackQuirkPolicy"/> says the caller is affected. There is
    /// no other client-specific behaviour, and no channel is ever named.
    /// </para>
    /// <para>
    /// Where two variants are offered, the native one comes first. If a client can direct play
    /// both, Jellyfin takes the first and the broadcast reaches it untouched. If it can direct
    /// play neither, Jellyfin transcodes the first, and it transcodes the original rather than a
    /// stream that has already been encoded once.
    /// </para>
    /// </remarks>
    public static class PlaybackVariantPolicy
    {
        /// <summary>
        /// Decides which variants to offer for a channel.
        /// </summary>
        /// <param name="descriptor">What the channel was observed to be, or <see langword="null"/>.</param>
        /// <param name="availability">Which forms can be produced.</param>
        /// <param name="client">The caller, or <see langword="null"/> when there is no request.</param>
        /// <returns>The variants, native first where both are offered.</returns>
        public static IReadOnlyList<VariantOffer> SelectVariants(
            ChannelMediaDescriptor? descriptor,
            PlaybackVariantAvailability availability,
            PlaybackClientContext? client)
        {
            // Nothing usable is known. Offer the broadcast and learn from opening it; guessing
            // here would mean opening a tuner during playback negotiation, which must stay free.
            if (descriptor is not { IsUsable: true })
            {
                return [new VariantOffer(PlaybackVariant.Native, true)];
            }

            if (descriptor.RandomAccess == H264RandomAccessKind.RecoveryOpenGop)
            {
                var affected = PlaybackQuirkPolicy.Applies(client, PlaybackQuirk.H264DvbRecoveryOpenGopColdStart);

                // The broadcast is conformant and starts correctly on every decoder that
                // discards the leading pictures of an open GOP. Only a client known not to gets
                // something else -- and then it gets only that, so the stream that will not
                // start on it cannot win direct play by being listed first.
                if (affected && availability.H264IdrNormalization)
                {
                    return [new VariantOffer(PlaybackVariant.H264IdrNormalization, true)];
                }

                return [new VariantOffer(PlaybackVariant.Native, true)];
            }

            // The broadcast is safe to start on but is coded in a way many clients cannot
            // decode. Both are direct play candidates and the device profile decides; a client
            // that can decode MPEG-2 keeps the broadcast because native comes first.
            if (descriptor.IsMpeg2Video && availability.Mpeg2H264Compatibility)
            {
                return
                [
                    new VariantOffer(PlaybackVariant.Native, true),
                    new VariantOffer(PlaybackVariant.Mpeg2H264Compatibility, true),
                ];
            }

            return [new VariantOffer(PlaybackVariant.Native, true)];
        }

        /// <summary>
        /// Decides what to serve once a stream has been opened and the source turned out to be
        /// something other than the stored description said.
        /// </summary>
        /// <remarks>
        /// Only one case forces a change of variant mid-open, and only in the direction of
        /// safety: a broadcast that turns out to signal random access without IDR frames, opened
        /// natively, by a client known not to be able to start on it. Anything else is left as
        /// negotiated, and the corrected description is stored for the next tune.
        /// </remarks>
        /// <param name="opened">The variant Jellyfin selected.</param>
        /// <param name="observed">What the open actually found.</param>
        /// <param name="availability">Which forms can be produced.</param>
        /// <param name="client">The caller, or <see langword="null"/> when there is no request.</param>
        /// <returns>The variant to serve.</returns>
        public static PlaybackVariant ReconcileAfterOpen(
            PlaybackVariant opened,
            H264RandomAccessKind observed,
            PlaybackVariantAvailability availability,
            PlaybackClientContext? client)
        {
            if (opened != PlaybackVariant.Native
                || observed != H264RandomAccessKind.RecoveryOpenGop
                || !availability.H264IdrNormalization)
            {
                return opened;
            }

            return PlaybackQuirkPolicy.Applies(client, PlaybackQuirk.H264DvbRecoveryOpenGopColdStart)
                ? PlaybackVariant.H264IdrNormalization
                : opened;
        }
    }
}
