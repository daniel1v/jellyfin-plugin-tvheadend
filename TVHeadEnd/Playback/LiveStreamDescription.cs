using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Entities;
using Tvheadend.Htsp.Model;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Playback;

/// <summary>
/// What a live stream contains, in Jellyfin's vocabulary, built from what TVHeadend said and
/// where the delivered transport stream actually put it.
/// </summary>
/// <remarks>
/// <para>
/// Two sources, each answering the half it is authoritative for. The running HTSP subscription
/// says what each elementary stream <em>is</em> -- codec, geometry, language, channel count --
/// because TVHeadend has already parsed the broadcast to produce that. The program map of the
/// delivered stream says what <em>order</em> those streams are in, because that is the order
/// libavformat will number them in and therefore the order every later <c>-map</c> argument
/// means.
/// </para>
/// <para>
/// The two are joined by PID. An HTSP stream is keyed by <c>es_index</c>, which is a counter and
/// not a position, so it is turned into a PID through the service's stream table and matched
/// against the program map from there. Assigning <c>es_index</c> straight to
/// <see cref="MediaStream.Index"/> is the mistake this whole path exists to avoid.
/// </para>
/// </remarks>
public sealed record LiveStreamDescription
{
    /// <summary>
    /// Gets the streams, at the indices FFmpeg will give them.
    /// </summary>
    public required IReadOnlyList<MediaStream> Streams { get; init; }

    /// <summary>
    /// Gets a value indicating whether the description is complete enough to publish.
    /// </summary>
    /// <remarks>
    /// Jellyfin dereferences the video stream while preparing playback, so a description with no
    /// video is worse than none at all for a television channel: it throws before any fallback
    /// could take effect.
    /// </remarks>
    public bool IsUsable => Streams.Count > 0;

    /// <summary>
    /// Builds the description of a stream.
    /// </summary>
    /// <param name="start">What the HTSP subscription says the stream contains.</param>
    /// <param name="programMap">The program map of the transport stream actually arriving.</param>
    /// <param name="service">
    /// The service's stream table, which carries the PID behind each <c>es_index</c>, or
    /// <see langword="null"/> when TVHeadend would not supply it.
    /// </param>
    /// <returns>
    /// The description, or <see langword="null"/> when the two halves cannot be joined and
    /// nothing honest can be said about the order.
    /// </returns>
    public static LiveStreamDescription? Build(
        HtspSubscriptionStart start,
        ProgramMapTable programMap,
        ServiceDescription? service)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(programMap);

        if (service is null || programMap.Entries.Count == 0)
        {
            return null;
        }

        // Every entry of the program map becomes a stream, in this order, because that is what
        // libavformat does as it walks the table. An entry nothing is known about still occupies
        // its index -- leaving a gap would shift everything after it.
        var streams = new List<MediaStream>(programMap.Entries.Count);
        for (var index = 0; index < programMap.Entries.Count; index++)
        {
            var entry = programMap.Entries[index];
            var described = FindByPid(start, service, entry.Pid);

            streams.Add(described is null
                ? DescribeFromProgramMap(entry, index)
                : Describe(described, index));
        }

        return new LiveStreamDescription { Streams = streams };
    }

    /// <summary>
    /// Reports whether the service TVHeadend describes over HTSP is the one arriving over HTTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one check that keeps the two halves of a live stream honest. Both are opened on a
    /// channel, and a channel may map to more than one service; combining the description of one
    /// with the bytes of another would produce a media source that is wrong in a way nothing
    /// downstream could detect.
    /// </para>
    /// <para>
    /// Proven rather than assumed, and proven directly: every PID the delivered program map
    /// announces has to be one the service is known to carry. That is a property of the actual
    /// bytes against the actual service table, with no reliance on which service TVHeadend
    /// happened to pick or on the order it picked them in.
    /// </para>
    /// </remarks>
    /// <param name="programMap">The program map of the transport stream arriving.</param>
    /// <param name="service">The service the HTSP subscription reported.</param>
    /// <returns>Whether the two describe the same service.</returns>
    public static bool AgreesWith(ProgramMapTable programMap, ServiceDescription service)
    {
        ArgumentNullException.ThrowIfNull(programMap);
        ArgumentNullException.ThrowIfNull(service);

        var servicePids = service.GetPids();
        if (servicePids.Count == 0 || programMap.Entries.Count == 0)
        {
            return false;
        }

        return programMap.Entries.All(entry => servicePids.Contains(entry.Pid));
    }

    private static HtspStreamInfo? FindByPid(
        HtspSubscriptionStart start,
        ServiceDescription service,
        int pid)
    {
        foreach (var stream in start.Streams)
        {
            if (service.GetPid(stream.Index) == pid)
            {
                return stream;
            }
        }

        return null;
    }

    private static MediaStream Describe(HtspStreamInfo stream, int index)
    {
        if (stream.IsVideo)
        {
            return new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = index,
                Codec = HtspCodecNames.ToJellyfinCodec(stream.Type),
                Width = stream.Width,
                Height = stream.Height,
                RealFrameRate = HtspTimeBase.ToFrameRate(stream.FrameDuration),
                AverageFrameRate = HtspTimeBase.ToFrameRate(stream.FrameDuration),
                AspectRatio = DescribeAspectRatio(stream),
                Language = stream.Language,

                // Left alone deliberately. Jellyfin overwrites it for every external live TV
                // service anyway, and a claim here would be a guess about the broadcast that
                // TVHeadend does not make.
                IsInterlaced = false,
            };
        }

        if (stream.IsAudio)
        {
            return new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = index,
                Codec = HtspCodecNames.ToJellyfinCodec(stream.Type),
                Language = stream.Language,
                Channels = stream.Channels,
                SampleRate = stream.SampleRateHz,

                // The DVB audio type, which is the only thing that distinguishes an audio
                // description from an ordinary track.
                IsHearingImpaired = stream.AudioType == 2,
                Title = DescribeAudioTitle(stream),
            };
        }

        if (stream.IsSubtitle)
        {
            return new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = index,
                Codec = HtspCodecNames.ToJellyfinCodec(stream.Type),
                Language = stream.Language,
                SupportsExternalStream = false,
            };
        }

        return new MediaStream
        {
            Type = MediaStreamType.Data,
            Index = index,
        };
    }

    private static MediaStream DescribeFromProgramMap(ProgramMapEntry entry, int index)
    {
        // In the delivered stream but not in anything TVHeadend described: a table or a private
        // stream the server does not carry as a component. It keeps its slot so the indices after
        // it stay right, and says nothing it cannot support.
        return new MediaStream
        {
            Type = entry.IsVideo ? MediaStreamType.Video : MediaStreamType.Data,
            Index = index,
            Codec = entry.IsVideo ? TransportStreamPacket.DescribeVideoStreamType(entry.StreamType) : null,
        };
    }

    private static string? DescribeAspectRatio(HtspStreamInfo stream)
    {
        if (stream.AspectNumerator is not > 0 || stream.AspectDenominator is not > 0)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{stream.AspectNumerator}:{stream.AspectDenominator}");
    }

    private static string? DescribeAudioTitle(HtspStreamInfo stream) => stream.AudioType switch
    {
        1 => "Clean effects",
        2 => "Hearing impaired",
        3 => "Audio description",
        _ => null,
    };
}
