using System;
using Tvheadend.Htsp.Protocol;

namespace Tvheadend.Htsp.Model;

/// <summary>
/// One elementary stream, exactly as TVHeadend describes it.
/// </summary>
/// <remarks>
/// <para>
/// Every property here is a field the server actually sends, under a name that says what the
/// server means by it rather than what a consumer might want it to mean. Nothing is derived,
/// nothing is invented, and nothing is renamed into another vocabulary; that translation belongs
/// to whoever consumes this.
/// </para>
/// <para>
/// Two of these fields are routinely misread, so they are named for what they are.
/// <see cref="SampleRateIndex"/> is <c>es_sri</c>, an index into the MPEG-4 sampling frequency
/// table, and not a frequency -- <c>htsp_subscription_start</c> puts it on the wire under the
/// name <c>rate</c>, which invites exactly that mistake. <see cref="FrameDuration"/> is a
/// duration in the subscription's own time base, which is 90 kHz whenever the subscription
/// asked for <c>90khz</c>, and not a frame rate.
/// </para>
/// </remarks>
public sealed record HtspStreamInfo
{
    /// <summary>
    /// Gets TVHeadend's <c>es_index</c> for this stream.
    /// </summary>
    /// <remarks>
    /// A per-service counter, assigned in the order streams were discovered. It is neither a PID
    /// nor a position in any list, so it must never be used as one; it is only meaningful for
    /// addressing the stream back to TVHeadend, as the stream filter does.
    /// </remarks>
    public required int Index { get; init; }

    /// <summary>
    /// Gets the stream type as TVHeadend names it, such as <c>H264</c>, <c>MPEG2AUDIO</c>,
    /// <c>AC3</c>, <c>DVBSUB</c> or <c>TELETEXT</c>.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the ISO 639 language, or <see langword="null"/> when the broadcast declares none.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets the frame width in pixels, for video.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    /// Gets the frame height in pixels, for video.
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    /// Gets the duration of one frame, for video, in the subscription's time base.
    /// </summary>
    /// <remarks>
    /// The server sends this in 90 kHz ticks when the subscription asked for <c>90khz</c> and in
    /// microseconds otherwise. Use <see cref="HtspTimeBase"/> to turn it into a frame rate rather
    /// than assuming either.
    /// </remarks>
    public int? FrameDuration { get; init; }

    /// <summary>
    /// Gets the numerator of the display aspect ratio, for video.
    /// </summary>
    public int? AspectNumerator { get; init; }

    /// <summary>
    /// Gets the denominator of the display aspect ratio, for video.
    /// </summary>
    public int? AspectDenominator { get; init; }

    /// <summary>
    /// Gets the DVB audio type, for audio: 0 is ordinary audio, 1 clean effects, 2 for the
    /// hearing impaired, 3 an audio description.
    /// </summary>
    public int? AudioType { get; init; }

    /// <summary>
    /// Gets the audio version, for audio.
    /// </summary>
    public int? AudioVersion { get; init; }

    /// <summary>
    /// Gets the channel count, for audio.
    /// </summary>
    public int? Channels { get; init; }

    /// <summary>
    /// Gets <c>es_sri</c>, the index into the MPEG-4 sampling frequency table, for audio.
    /// </summary>
    /// <remarks>
    /// Sent under the wire name <c>rate</c>. It is an index, not a frequency; see
    /// <see cref="SampleRateHz"/>.
    /// </remarks>
    public int? SampleRateIndex { get; init; }

    /// <summary>
    /// Gets a value indicating whether the audio carries UECP-encoded RDS.
    /// </summary>
    public bool CarriesRds { get; init; }

    /// <summary>
    /// Gets the DVB subtitle composition page identifier.
    /// </summary>
    public int? CompositionId { get; init; }

    /// <summary>
    /// Gets the DVB subtitle ancillary page identifier.
    /// </summary>
    public int? AncillaryId { get; init; }

    /// <summary>
    /// Gets the sampling frequency in hertz, resolved from <see cref="SampleRateIndex"/>.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> when no index was sent or when it names one of the reserved table
    /// entries, which carry no frequency at all.
    /// </remarks>
    public int? SampleRateHz => SampleRateIndex is { } index ? HtspSampleRate.FromIndex(index) : null;

    /// <summary>
    /// Gets a value indicating whether this stream is video.
    /// </summary>
    public bool IsVideo => HtspStreamTypes.IsVideo(Type);

    /// <summary>
    /// Gets a value indicating whether this stream is audio.
    /// </summary>
    public bool IsAudio => HtspStreamTypes.IsAudio(Type);

    /// <summary>
    /// Gets a value indicating whether this stream is a subtitle track.
    /// </summary>
    public bool IsSubtitle => HtspStreamTypes.IsSubtitle(Type);

    /// <summary>
    /// Reads one entry of the <c>streams</c> list.
    /// </summary>
    /// <param name="message">The entry.</param>
    /// <returns>The parsed stream.</returns>
    /// <exception cref="HtspProtocolException">The entry has no index or no type.</exception>
    public static HtspStreamInfo From(HtspMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var index = message.GetInt32("index")
            ?? throw new HtspProtocolException("An HTSP stream description carried no index.");
        var type = message.GetString("type")
            ?? throw new HtspProtocolException("An HTSP stream description carried no type.");

        return new HtspStreamInfo
        {
            Index = index,
            Type = type,
            Language = NullIfEmpty(message.GetString("language")),
            Width = message.GetInt32("width"),
            Height = message.GetInt32("height"),
            FrameDuration = message.GetInt32("duration"),
            AspectNumerator = message.GetInt32("aspect_num"),
            AspectDenominator = message.GetInt32("aspect_den"),
            AudioType = message.GetInt32("audio_type"),
            AudioVersion = message.GetInt32("audio_version"),
            Channels = message.GetInt32("channels"),
            SampleRateIndex = message.GetInt32("rate"),
            CarriesRds = message.GetBoolean("rds_uecp"),
            CompositionId = message.GetInt32("composition_id"),
            AncillaryId = message.GetInt32("ancillary_id"),
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
