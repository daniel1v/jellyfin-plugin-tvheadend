using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace TVHeadEnd.Playback;

/// <summary>
/// Builds the one media source Jellyfin negotiates and plays a live channel with.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one, because TVHeadend delivers exactly one thing: the broadcast, forwarded
/// untouched by the <c>pass</c> profile. What a given client can do with it -- play it as it is,
/// remux it, or transcode it -- is Jellyfin's decision, made against the device profile the
/// client sent, and Jellyfin makes it again after the stream is opened and fully described. There
/// is nothing here for this plugin to pre-empt.
/// </para>
/// <para>
/// The source states facts and stops. Where TVHeadend gives no answer the field is left unset:
/// Jellyfin handles an absent value, and a wrong one produces a playback decision that fails in a
/// way nobody can trace back to here.
/// </para>
/// </remarks>
public static class LiveMediaSource
{
    /// <summary>
    /// What a device profile calls a transport stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One name, and Jellyfin's own: <c>ts</c> is what its probe normaliser produces and what
    /// client device profiles list for MPEG-TS direct play. Jellyfin for Android TV publishes its
    /// direct play profile as <c>ts</c>, and a container that does not match it is a container
    /// that cannot direct play.
    /// </para>
    /// <para>
    /// It needs no FFmpeg spelling of its own, because Jellyfin translates: with hardware
    /// acceleration configured the container is passed to FFmpeg as <c>-f</c>, and
    /// <c>EncodingHelper.GetInputFormat</c> maps <c>ts</c> to <c>mpegts</c> on the way. Both
    /// levels are therefore served by the one name.
    /// </para>
    /// <para>
    /// Not <c>mpegts,ts</c>. A container is one container, not a set of aliases, and that value
    /// reached FFmpeg unmapped as <c>-f mpegts,ts</c> -- not a demuxer it has, and nothing played
    /// at all on a hardware-accelerated server.
    /// </para>
    /// </remarks>
    public const string Container = "ts";

    /// <summary>
    /// How long FFmpeg may analyse the stream before it has to start producing output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a tuning knob -- without it live TV does not play at all. Jellyfin turns this into
    /// <c>-analyzeduration</c>, and when it is unset it falls back to the server-wide setting,
    /// which defaults to 200 seconds. On a file that is read as fast as the disk allows, that
    /// costs nothing. On a live stream it means FFmpeg reads two hundred seconds of broadcast
    /// before it writes its first HLS segment, so the client waits, Jellyfin gives up waiting for
    /// the playlist, kills FFmpeg and starts again -- on every channel, for ever.
    /// </para>
    /// <para>
    /// Two seconds, and not less. It is tempting to read this as two seconds added to every start,
    /// and it is not: FFmpeg returns as soon as it has described the streams, so the value is a
    /// ceiling rather than a wait. Measured on the test server on 2026-08-26 -- first HLS segment
    /// written after 1,772 ms at 100 ms, 1,807 ms at 250 ms, 1,998 ms at 500 ms and 1,481 ms at
    /// 1,000 ms. There is no trend in that, only noise: lowering it buys nothing.
    /// </para>
    /// <para>
    /// What it does buy is a failure. Below roughly a quarter of a second FFmpeg has not seen an
    /// AC-3 frame yet, so it cannot state the sample rate, and a stream it is asked to copy rather
    /// than re-encode has no parameters to write a header from: <c>sample rate not set</c>, then
    /// <c>Could not write header (incorrect codec parameters ?)</c>, and the client gets a 500.
    /// The threshold moves by channel -- ZDF failed at 50 ms and worked at 100 ms, Das Erste
    /// failed at 100 ms and worked at 250 ms -- so it is not a number to sail close to.
    /// </para>
    /// <para>
    /// Without any value at all one request took 200,008 ms: the server-wide default of 200M, to
    /// the millisecond. That is the failure this exists to prevent, and it is why the value is
    /// stated rather than left to the server.
    /// </para>
    /// </remarks>
    private const int AnalyzeDurationMs = 2000;

    /// <summary>
    /// Builds the source offered during playback negotiation, before anything is opened.
    /// </summary>
    /// <remarks>
    /// Nothing is known about the broadcast at this point and nothing is claimed. Jellyfin only
    /// has to be able to identify and open it; what it contains is established while opening and
    /// evaluated against the device profile afterwards.
    /// </remarks>
    /// <param name="mediaSourceId">
    /// The source identity, which is the channel's own Jellyfin item identifier.
    /// </param>
    /// <param name="name">What to call the source.</param>
    /// <returns>An unopened media source.</returns>
    public static MediaSourceInfo CreatePending(string mediaSourceId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);

        return new MediaSourceInfo
        {
            Id = mediaSourceId,
            Name = name,
            Path = null,
            Protocol = MediaProtocol.Http,
            Container = Container,
            IsInfiniteStream = true,
            AnalyzeDurationMs = AnalyzeDurationMs,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,

            // Never probed, opened or not. The program map of the stream being delivered is the
            // description, and asking Jellyfin to inspect a live channel means reading a stream
            // that is already being read to answer a question already answered.
            SupportsProbing = false,
            MediaStreams = [],
        };
    }

    /// <summary>
    /// Builds the source handed back once the stream is open and described.
    /// </summary>
    /// <param name="mediaSourceId">The source identity.</param>
    /// <param name="name">What to call the source.</param>
    /// <param name="mediaPath">The buffer file the stream is readable from.</param>
    /// <param name="streamUrl">The address Jellyfin serves the open stream at.</param>
    /// <param name="description">What the stream contains, read from its program map.</param>
    /// <param name="requiresVideoReencode">
    /// Whether the viewer this stream was opened for needs the video re-encoded. True only for the
    /// one measured case: a decoder that will not start without an IDR picture, and an H.264
    /// broadcast whose access point was found to carry none.
    /// </param>
    /// <returns>An opened media source.</returns>
    public static MediaSourceInfo CreateOpened(
        string mediaSourceId,
        string name,
        string mediaPath,
        string streamUrl,
        LiveStreamDescription description,
        bool requiresVideoReencode)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);
        ArgumentException.ThrowIfNullOrEmpty(mediaPath);
        ArgumentException.ThrowIfNullOrEmpty(streamUrl);
        ArgumentNullException.ThrowIfNull(description);

        var source = new MediaSourceInfo
        {
            Id = mediaSourceId,
            Name = name,
            Container = Container,
            IsInfiniteStream = true,
            AnalyzeDurationMs = AnalyzeDurationMs,
            RequiresOpening = false,
            RequiresClosing = true,

            // Direct play and remux hand the client the broadcast as it is, which for this one
            // viewer is a stream its decoder will not start on. Withdrawing both leaves Jellyfin
            // its ordinary transcoding path, which is the thing that can produce a picture with
            // IDR frames in it. Nothing here misstates what the stream contains to get there:
            // the streams below are still the program map, and the container is still what it is.
            SupportsDirectPlay = !requiresVideoReencode,
            SupportsDirectStream = !requiresVideoReencode,
            SupportsTranscoding = true,

            // The streams below are the program map of the stream being delivered, at the
            // indices FFmpeg will give them. Nothing here is ever probed.
            SupportsProbing = false,

            // Two addresses for two readers, which is the separation Jellyfin already draws.
            //
            // The client is given the buffer as a file, because that is what puts Jellyfin's
            // static video endpoint on its direct stream provider path: it reads the live stream
            // through this plugin's own reader, which waits for what has not been written yet.
            // Published as an HTTP address instead, the client is sent to fetch the transport
            // stream itself, and the clients that matter do not manage it.
            //
            // A file was published once before and failed, because the request arrived without a
            // live stream identifier: with none, Jellyfin has no provider to ask and serves the
            // ring file itself, which ends at whatever had been written -- measured at 5,434 bytes
            // in 47 ms. That is fixed where it is caused, in LivePlaybackRequestMiddleware, which
            // supplies the identifier of the running stream. The ring file is untouched.
            //
            // Jellyfin's own reader takes the encoder address, because AttachMediaSourceInfo
            // prefers EncoderPath and EncoderProtocol whenever both are set. So FFmpeg goes over
            // HTTP as it always did, and only the door the client is sent to has changed.
            Path = mediaPath,
            Protocol = MediaProtocol.File,
            EncoderPath = streamUrl,
            EncoderProtocol = MediaProtocol.Http,
            RequiredHttpHeaders = new Dictionary<string, string>(),

            // A channel has no runtime and no size, however much of it has been received.
            RunTimeTicks = null,
            Size = null,
            DefaultSubtitleStreamIndex = null,
        };

        source.MediaStreams = [.. description.Streams];

        // No default audio track is nominated, and that is deliberate. The program map says which
        // tracks exist, not which one a viewer wants, so naming one here would be an invention --
        // and it was: the first track of the map was nominated, which on the German broadcasts is
        // MPEG audio. Jellyfin then reports that choice back, the client pins it in its next
        // question, and a pinned track collapses the candidate list to exactly that one
        // (StreamBuilder narrows to all audio streams only while none is pinned and no default has
        // a source). A device that cannot decode MPEG audio is then made to transcode a stream it
        // could have taken as delivered, because of a preference nobody expressed.
        //
        // Left unset, the choice falls to Jellyfin, which knows the viewer, their language and
        // their client -- none of which this plugin knows.
        return source;
    }
}
