namespace Tvheadend.Htsp.Model;

/// <summary>
/// The time base a subscription's durations and timestamps are expressed in.
/// </summary>
public static class HtspTimeBase
{
    /// <summary>
    /// The tick rate of a subscription opened with <c>90khz</c>, which is how this client always
    /// opens one.
    /// </summary>
    public const int Ticks90Khz = 90000;

    /// <summary>
    /// The tick rate TVHeadend rescales to when <c>90khz</c> was not requested.
    /// </summary>
    public const int TicksMicroseconds = 1000000;

    /// <summary>
    /// Turns a frame duration into a frame rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole of the calculation, and deliberately no more than the calculation. TVHeadend
    /// sends the duration of one frame in the subscription's time base, so the frame rate is the
    /// tick rate divided by it -- 3600 ticks of a 90 kHz clock is 25 fps, 1800 is 50 fps.
    /// </para>
    /// <para>
    /// There is no halving rule and no correction for interlaced video. An earlier attempt to
    /// second-guess this value is how a 50 fps broadcast came to be published as 100 fps.
    /// </para>
    /// </remarks>
    /// <param name="frameDuration">The frame duration TVHeadend reported.</param>
    /// <param name="ticksPerSecond">The subscription's tick rate.</param>
    /// <returns>The frame rate, or <see langword="null"/> when no usable duration was reported.</returns>
    public static float? ToFrameRate(int? frameDuration, int ticksPerSecond = Ticks90Khz)
    {
        if (frameDuration is not > 0 || ticksPerSecond <= 0)
        {
            return null;
        }

        return (float)ticksPerSecond / frameDuration.Value;
    }
}
