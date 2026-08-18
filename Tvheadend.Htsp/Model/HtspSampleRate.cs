using System;

namespace Tvheadend.Htsp.Model;

/// <summary>
/// Turns TVHeadend's <c>es_sri</c> into a sampling frequency.
/// </summary>
/// <remarks>
/// The table is the MPEG-4 sampling frequency index, and is the same one TVHeadend resolves
/// through <c>sri_to_rate</c>. It matters that this is done in one place: the value reaches a
/// client under the wire name <c>rate</c>, and reporting an index where a frequency is expected
/// yields an audio track claiming to be sampled at four hertz.
/// </remarks>
public static class HtspSampleRate
{
    private static readonly int[] Frequencies =
    [
        96000, 88200, 64000, 48000,
        44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000,
        7350, 0, 0, 0,
    ];

    /// <summary>
    /// Resolves a sampling frequency index.
    /// </summary>
    /// <param name="index">The index, as TVHeadend sends it.</param>
    /// <returns>
    /// The frequency in hertz, or <see langword="null"/> for one of the reserved entries, which
    /// name no frequency. Unknown is reported rather than guessed: a wrong sample rate is worse
    /// than an absent one.
    /// </returns>
    public static int? FromIndex(int index)
    {
        if (index < 0 || index >= Frequencies.Length)
        {
            return null;
        }

        var frequency = Frequencies[index];
        return frequency == 0 ? null : frequency;
    }
}
