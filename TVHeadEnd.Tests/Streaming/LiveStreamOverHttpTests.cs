using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// Opening a live channel end to end, over nothing but the HTTP stream.
/// </summary>
/// <remarks>
/// The point of the test is as much what it does not need as what it asserts. There is no HTSP
/// server here and no administrative API, and the stream still opens and describes itself
/// completely -- because everything the description needs is in the transport stream. An earlier
/// design took out a second HTSP subscription and made two administrator-only API calls to reach
/// the same answer.
/// </remarks>
public sealed class LiveStreamOverHttpTests : IDisposable
{
    private const int PacketLength = 188;
    private const int PmtPid = 0x13ec;
    private const int VideoPid = 0x13ed;
    private const int GermanAudioPid = 0x13ee;
    private const int EnglishAudioPid = 0x13ef;
    private const int SubtitlePid = 0x13f0;

    private readonly string _bufferDirectory = Path.Combine(Path.GetTempPath(), $"tvh-live-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_bufferDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Left behind in the temporary directory; harmless.
        }
    }

    [Fact]
    public async Task AChannelIsOpenedAndDescribedFromTheTransportStreamAlone()
    {
        await using var server = new TransportStreamServer(BuildBroadcast());

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        var programMap = stream.ProgramMap;
        Assert.NotNull(programMap);

        var description = LiveStreamDescription.FromProgramMap(programMap!, ChannelType.TV);
        Assert.NotNull(description);

        // The whole of a typical DVB channel, in the order the table lists it.
        Assert.Equal(
            [MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Audio, MediaStreamType.Subtitle],
            description!.Streams.Select(media => media.Type));
        Assert.Equal(["h264", null, null, "dvb_subtitle"], description.Streams.Select(media => media.Codec));
        Assert.Equal([null, "deu", "eng", "deu"], description.Streams.Select(media => media.Language));
        Assert.Equal([0, 1, 2, 3], description.Streams.Select(media => media.Index));

        // The one request made. No HTSP subscription, no service lookup, no PID table fetched
        // from an administrator-only API.
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task TheStreamHandedToJellyfinBeginsWithTheProgramTablesAndAnAccessPoint()
    {
        await using var server = new TransportStreamServer(BuildBroadcast());

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        using var reader = stream.GetStream();
        var opening = await ReadUpTo(reader, 4 * PacketLength);

        // A decoder that starts here has the tables it needs and a picture it may begin at.
        Assert.Equal(0x00, ReadPid(opening, 0));
        Assert.Equal(PmtPid, ReadPid(opening, PacketLength));
        Assert.Equal(VideoPid, ReadPid(opening, 2 * PacketLength));
        Assert.True(stream.StartedOnConfirmedRandomAccessPoint);
    }

    [Theory]
    [InlineData(0x1B, "h264")]
    [InlineData(0x02, "mpeg2video")]
    public async Task AChannelIsOpenedAndKeepsDeliveringBytesToItsConsumer(byte videoStreamType, string codec)
    {
        // The whole path a viewer takes: open, then read from what IDirectStreamProvider hands
        // back, the way Jellyfin's LiveStreamFiles endpoint does. A test that only proves an
        // object was constructed would have passed while every channel loaded for ever.
        await using var server = new TransportStreamServer(BuildBroadcast(videoStreamType: videoStreamType));

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        Assert.Equal(codec, LiveStreamDescription.FromProgramMap(stream.ProgramMap!, ChannelType.TV)!.Streams[0].Codec);

        using var reader = stream.GetStream();

        // Read well past the first chunk, so this covers the buffer being consumed as it is
        // written rather than a single hand-off.
        var delivered = await ReadUpTo(reader, 300 * PacketLength);

        Assert.True(
            delivered.Length >= 300 * PacketLength,
            FormattableString.Invariant($"Only {delivered.Length} bytes reached the consumer."));

        // Every packet of it is a transport stream packet, on a packet boundary.
        for (var offset = 0; offset + PacketLength <= delivered.Length; offset += PacketLength)
        {
            Assert.Equal(0x47, delivered[offset]);
        }
    }

    [Fact]
    public async Task ARadioChannelIsOpenedAndDeliversBytes()
    {
        // No video to wait for. This is the case that used to be withheld until the startup
        // limit expired, because the conditioner would only start on a video packet.
        await using var server = new TransportStreamServer(BuildRadioBroadcast());

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        using var reader = stream.GetStream();
        var delivered = await ReadUpTo(reader, 60 * PacketLength);

        Assert.True(delivered.Length >= 60 * PacketLength);
        Assert.Equal(0x00, ReadPid(delivered, 0));
        Assert.Equal(PmtPid, ReadPid(delivered, PacketLength));

        // Audio only, and complete: a radio channel is described from its audio, and nothing is
        // probed to make up for the video that was never going to be there.
        var description = LiveStreamDescription.FromProgramMap(stream.ProgramMap!, ChannelType.Radio);
        Assert.NotNull(description);
        Assert.All(description!.Streams, media => Assert.Equal(MediaStreamType.Audio, media.Type));
    }

    [Fact]
    public async Task TheOpenedSourceTellsJellyfinHowLongItMayAnalyse()
    {
        // Without it Jellyfin falls back to its server-wide default, which is two hundred
        // seconds. On a live stream that is two hundred seconds before FFmpeg writes anything,
        // and the client waits the whole time.
        await using var server = new TransportStreamServer(BuildBroadcast());

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        var source = LiveMediaSource.CreateOpened(
            "8f14e45fceea167a5a36dedd4bea2543",
            "Das Erste HD",
            stream.MediaPath,
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            LiveStreamDescription.FromProgramMap(stream.ProgramMap!, ChannelType.TV)!,
            requiresVideoReencode: false);

        Assert.True(source.AnalyzeDurationMs is > 0 and <= 5000);
    }

    [Fact]
    public async Task TheEventInformationTableNeverReachesTheClient()
    {
        // libavformat creates an "epg" stream the moment it sees one, which shifts every stream
        // index after it and makes the description disagree with what FFmpeg will map.
        await using var server = new TransportStreamServer(BuildBroadcast(includeEventInformation: true));

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        using var reader = stream.GetStream();
        var delivered = await ReadUpTo(reader, 60 * PacketLength);

        for (var offset = 0; offset + PacketLength <= delivered.Length; offset += PacketLength)
        {
            Assert.NotEqual(0x12, ReadPid(delivered, offset));
        }

        // And the description is unaffected by its presence in the source.
        var description = LiveStreamDescription.FromProgramMap(stream.ProgramMap!, ChannelType.TV)!;
        Assert.Equal(4, description.Streams.Count);
    }

    [Fact]
    public async Task AnOpenGopBroadcastAsksJellyfinToReEncodeItForADecoderThatNeedsIdrPictures()
    {
        // The ARD case end to end. The access points are signalled and hold no IDR, so the source
        // handed back withdraws direct play -- and the buffer still holds the broadcast exactly as
        // it arrived, because the re-encoding is Jellyfin's to do.
        await using var server = new TransportStreamServer(BuildBroadcast());

        await using var stream = CreateStream(server.Url, clientNeedsIdr: true);
        await stream.Open(CancellationToken.None);

        Assert.True(stream.RequiresVideoReencode);

        // Which is what the opener then builds the source from.
        var source = OpenedSource(stream);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);

        // And the ring still holds the broadcast, tables and all, exactly as it arrived.
        using var reader = stream.GetStream();
        var delivered = await ReadUpTo(reader, 2 * PacketLength);
        Assert.Equal(0x00, ReadPid(delivered, 0));
        Assert.Equal(PmtPid, ReadPid(delivered, PacketLength));
    }

    [Fact]
    public async Task TheSameBroadcastIsHandedToEveryOtherClientAsItIs()
    {
        await using var server = new TransportStreamServer(BuildBroadcast());

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        Assert.False(stream.RequiresVideoReencode);

        var source = OpenedSource(stream);
        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
    }

    [Fact]
    public async Task ABroadcastWithRealIdrPicturesIsNotReEncodedForAnyone()
    {
        await using var server = new TransportStreamServer(BuildBroadcast(carriesIdr: true));

        await using var stream = CreateStream(server.Url, clientNeedsIdr: true);
        await stream.Open(CancellationToken.None);

        Assert.False(stream.RequiresVideoReencode);
        Assert.True(stream.SuitsDecodersNeedingIdr);
        Assert.True(OpenedSource(stream).SupportsDirectPlay);
    }

    [Fact]
    public async Task AnMpeg2BroadcastIsNotReEncodedForAnyone()
    {
        // The IDR question is about H.264 and nothing else, and asking it of MPEG-2 would find
        // slice start codes that read the same and mean something else entirely.
        await using var server = new TransportStreamServer(BuildBroadcast(videoStreamType: 0x02));

        await using var stream = CreateStream(server.Url, clientNeedsIdr: true);
        await stream.Open(CancellationToken.None);

        Assert.False(stream.RequiresVideoReencode);
        Assert.True(stream.SuitsDecodersNeedingIdr);
        Assert.True(OpenedSource(stream).SupportsDirectPlay);
    }

    [Fact]
    public async Task AForcedReEncodeStreamIsSharedWithItsOwnKindAndNoOther()
    {
        // Its media source has direct play withdrawn, which is right for the viewer it was opened
        // for and wrong for everyone else: handing it on would transcode a channel that plays
        // perfectly well as it is.
        await using var server = new TransportStreamServer(BuildBroadcast());

        await using var stream = CreateStream(server.Url, clientNeedsIdr: true);
        await stream.Open(CancellationToken.None);

        Assert.True(LiveTvService.CanBeReusedFor(stream, "42", needsIdrToStart: true));
        Assert.False(LiveTvService.CanBeReusedFor(stream, "42", needsIdrToStart: false));
    }

    [Fact]
    public async Task AnOpenGopBroadcastIsSharedWithEveryoneExceptDecodersThatNeedIdrPictures()
    {
        await using var server = new TransportStreamServer(BuildBroadcast());

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        Assert.True(LiveTvService.CanBeReusedFor(stream, "42", needsIdrToStart: false));
        Assert.False(LiveTvService.CanBeReusedFor(stream, "42", needsIdrToStart: true));
    }

    [Theory]
    [InlineData((byte)0x1B, true)]
    [InlineData((byte)0x02, false)]
    public async Task ABroadcastEveryDecoderStartsOnIsSharedWithAllOfThem(byte videoStreamType, bool carriesIdr)
    {
        await using var server = new TransportStreamServer(
            BuildBroadcast(videoStreamType: videoStreamType, carriesIdr: carriesIdr));

        await using var stream = CreateStream(server.Url);
        await stream.Open(CancellationToken.None);

        Assert.True(LiveTvService.CanBeReusedFor(stream, "42", needsIdrToStart: false));
        Assert.True(LiveTvService.CanBeReusedFor(stream, "42", needsIdrToStart: true));
    }

    /// <summary>
    /// The source the opener would publish for this stream, built the same way it builds it.
    /// </summary>
    private static MediaSourceInfo OpenedSource(TvheadendLiveStream stream)
        => LiveMediaSource.CreateOpened(
            "8f14e45fceea167a5a36dedd4bea2543",
            "Das Erste HD",
            stream.MediaPath,
            "http://localhost:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            LiveStreamDescription.FromProgramMap(stream.ProgramMap!, ChannelType.TV)!,
            stream.RequiresVideoReencode);

    private TvheadendLiveStream CreateStream(string url, bool clientNeedsIdr = false)

        => new(
            "42",
            "Das Erste HD",
            url,
            new Dictionary<string, string>(),
            LiveMediaSource.CreatePending("8f14e45fceea167a5a36dedd4bea2543", "Das Erste HD"),
            Path.Combine(_bufferDirectory, Guid.NewGuid().ToString("N")),
            LiveStreamBuffer.MinimumSizeMegabytes,
            new SingleClientFactory(),
            NullLogger.Instance,
            clientNeedsIdr,
            TimeSpan.FromSeconds(15));

    /// <summary>
    /// Builds a broadcast the way a DVB multiplex delivers one: tables first, then a run of
    /// packets with periodic access points.
    /// </summary>
    private static byte[] BuildBroadcast(
        bool includeEventInformation = false,
        byte videoStreamType = 0x1B,
        bool carriesIdr = false)
    {
        var packets = new List<byte[]>();

        for (var round = 0; round < 40; round++)
        {
            if (round % 8 == 0)
            {
                packets.Add(SectionPacket(0x00, ProgramAssociationSection()));
                packets.Add(SectionPacket(PmtPid, ProgramMapSection(videoStreamType)));
            }

            if (includeEventInformation)
            {
                packets.Add(Packet(0x12));
            }

            var accessPoint = round % 4 == 0;
            packets.Add(accessPoint && carriesIdr
                ? IdrPacket()
                : Packet(VideoPid, startsUnit: true, randomAccess: accessPoint));
            packets.Add(Packet(VideoPid));
            packets.Add(Packet(GermanAudioPid, startsUnit: true));
            packets.Add(Packet(EnglishAudioPid, startsUnit: true));
            packets.Add(Packet(SubtitlePid, startsUnit: true));
        }

        return [.. packets.SelectMany(packet => packet)];
    }

    /// <summary>
    /// Builds a radio multiplex: program tables and one audio track, and no video at all.
    /// </summary>
    private static byte[] BuildRadioBroadcast()
    {
        var packets = new List<byte[]>();

        for (var round = 0; round < 40; round++)
        {
            if (round % 8 == 0)
            {
                packets.Add(SectionPacket(0x00, ProgramAssociationSection()));
                packets.Add(SectionPacket(PmtPid, RadioProgramMapSection()));
            }

            packets.Add(Packet(GermanAudioPid, startsUnit: true));
            packets.Add(Packet(GermanAudioPid));
        }

        return [.. packets.SelectMany(packet => packet)];
    }

    private static byte[] RadioProgramMapSection()
    {
        var body = new List<byte>();
        AppendEntry(body, 0x03, GermanAudioPid, [0x0A, 0x04, (byte)'d', (byte)'e', (byte)'u', 0x00]);

        var section = new List<byte>
        {
            0x02,
            0, 0,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            (byte)(0xE0 | ((GermanAudioPid >> 8) & 0x1F)), GermanAudioPid & 0xFF,
            0xF0, 0x00,
        };

        section.AddRange(body);

        var sectionLength = section.Count - 3 + 4;
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)(sectionLength & 0xFF);

        return PsiSection.WithCrc(section);
    }

    private static byte[] ProgramAssociationSection()
    {
        var section = new List<byte>
        {
            0x00,
            0xB0, 0x0D,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            0x00, 0x01,
            (byte)(0xE0 | ((PmtPid >> 8) & 0x1F)), PmtPid & 0xFF,
        };

        return PsiSection.WithCrc(section);
    }

    private static byte[] ProgramMapSection(byte videoStreamType = 0x1B)
    {
        var body = new List<byte>();
        AppendEntry(body, videoStreamType, VideoPid, []);
        AppendEntry(body, 0x03, GermanAudioPid, [0x0A, 0x04, (byte)'d', (byte)'e', (byte)'u', 0x00]);
        AppendEntry(body, 0x03, EnglishAudioPid, [0x0A, 0x04, (byte)'e', (byte)'n', (byte)'g', 0x00]);
        AppendEntry(
            body,
            0x06,
            SubtitlePid,
            [0x59, 0x08, (byte)'d', (byte)'e', (byte)'u', 0x10, 0x00, 0x00, 0x00, 0x00]);

        var section = new List<byte>
        {
            0x02,
            0, 0,
            0x00, 0x01,
            0xC1, 0x00, 0x00,
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), VideoPid & 0xFF,
            0xF0, 0x00,
        };

        section.AddRange(body);

        var sectionLength = section.Count - 3 + 4;
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)(sectionLength & 0xFF);

        return PsiSection.WithCrc(section);
    }

    private static void AppendEntry(List<byte> body, byte streamType, int pid, byte[] descriptors)
    {
        body.Add(streamType);
        body.Add((byte)(0xE0 | ((pid >> 8) & 0x1F)));
        body.Add((byte)(pid & 0xFF));
        body.Add((byte)(0xF0 | ((descriptors.Length >> 8) & 0x0F)));
        body.Add((byte)(descriptors.Length & 0xFF));
        body.AddRange(descriptors);
    }

    private static byte[] SectionPacket(int pid, byte[] section)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((pid >> 8) & 0x1F) | 0x40);
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10;
        packet[4] = 0x00; // pointer_field
        section.CopyTo(packet, 5);
        return packet;
    }

    private static byte[] Packet(int pid, bool startsUnit = false, bool randomAccess = false)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0x47;
        packet[1] = (byte)(((pid >> 8) & 0x1F) | (startsUnit ? 0x40 : 0x00));
        packet[2] = (byte)(pid & 0xFF);

        if (randomAccess)
        {
            packet[3] = 0x30;
            packet[4] = 1;
            packet[5] = 0x40;
        }
        else
        {
            packet[3] = 0x10;
        }

        return packet;
    }

    /// <summary>
    /// A signalled access point whose picture actually begins with an IDR, which is what an ARD
    /// broadcast does not send and a ZDF one does.
    /// </summary>
    private static byte[] IdrPacket()
    {
        var packet = Packet(VideoPid, startsUnit: true, randomAccess: true);
        byte[] nalUnits = [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x65, 0x88];
        nalUnits.CopyTo(packet.AsSpan(6));
        return packet;
    }

    private static int ReadPid(byte[] data, int offset)

        => ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];

    private static async Task<byte[]> ReadUpTo(Stream stream, int count)
    {
        var buffer = new byte[count];
        var total = 0;
        var attempts = 0;

        while (total < count && attempts < 600)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (read == 0)
            {
                attempts++;
                await Task.Delay(5);
                continue;
            }

            total += read;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>
    /// A TVHeadend that answers a stream request with a transport stream, and counts how often it
    /// is asked for anything at all.
    /// </summary>
    private sealed class TransportStreamServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _payload;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _loop;

        private int _requestCount;

        internal TransportStreamServer(byte[] payload)
        {
            _payload = payload;

            var port = FreePort();
            Url = FormattableString.Invariant($"http://127.0.0.1:{port}/stream/channelid/42?profile=pass");
            _listener.Prefixes.Add(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
            _listener.Start();
            _loop = Task.Run(() => ServeAsync(_lifetime.Token));
        }

        internal string Url { get; }

        internal int RequestCount => Volatile.Read(ref _requestCount);

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Close();

            try
            {
                await _loop;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Teardown.
            }

            _lifetime.Dispose();
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _requestCount);

                context.Response.ContentType = "video/mp2t";
                context.Response.SendChunked = true;

                try
                {
                    // Keeps producing until the test is done with it. A finite payload would make
                    // the reader's behaviour depend on whether it happened to join before or
                    // after the last byte -- and a live viewer joins at the newest entry point,
                    // so it would usually be after.
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await context.Response.OutputStream.WriteAsync(_payload, cancellationToken);
                        await context.Response.OutputStream.FlushAsync(cancellationToken);
                        await Task.Delay(15, cancellationToken);
                    }
                }
                catch (Exception exception) when (exception is HttpListenerException or IOException or OperationCanceledException)
                {
                    // The reader went away.
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                    {
                        // Already gone.
                    }
                }
            }
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
