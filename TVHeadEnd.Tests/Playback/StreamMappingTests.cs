using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using Tvheadend.Htsp.Model;
using Tvheadend.Htsp.Protocol;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// Placing what TVHeadend describes at the index FFmpeg will give it.
/// </summary>
/// <remarks>
/// The mapping every wrong <c>-map</c> argument in this plugin's history came from. HTSP keys a
/// stream by <c>es_index</c>, which is a counter; Jellyfin addresses one by its position in the
/// media source's stream list, which is the position libavformat gave it while walking the
/// program map. The two agree only by going through the PID.
/// </remarks>
public class StreamMappingTests
{
    private const int VideoPid = 511;
    private const int GermanAudioPid = 512;
    private const int EnglishAudioPid = 513;
    private const int SubtitlePid = 514;

    [Fact]
    public void StreamsAreNumberedByProgramMapOrderRatherThanByEsIndex()
    {
        // The case that matters: TVHeadend's es_index counters are 4, 1, 7 while the program map
        // lists video, German audio, English audio. Using es_index as the index would number the
        // tracks 4, 1, 7 -- three wrong answers, one of which is out of range.
        var start = Subscription(
            Stream(esIndex: 4, "H264", width: 1920, height: 1080, frameDuration: 3600),
            Stream(esIndex: 1, "MPEG2AUDIO", language: "deu", channels: 2, sampleRateIndex: 3),
            Stream(esIndex: 7, "AC3", language: "eng", channels: 6, sampleRateIndex: 3));

        var service = Service(
            (4, VideoPid),
            (1, GermanAudioPid),
            (7, EnglishAudioPid));

        var programMap = Map((0x1b, VideoPid), (0x03, GermanAudioPid), (0x81, EnglishAudioPid));

        var description = LiveStreamDescription.Build(start, programMap, service);

        Assert.NotNull(description);
        Assert.Equal([0, 1, 2], description!.Streams.Select(stream => stream.Index));
        Assert.Equal(
            [MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Audio],
            description.Streams.Select(stream => stream.Type));
        Assert.Equal(["h264", "mp2", "ac3"], description.Streams.Select(stream => stream.Codec));
        Assert.Equal([null, "deu", "eng"], description.Streams.Select(stream => stream.Language));
    }

    [Fact]
    public void TheProgramMapDecidesTheOrderEvenWhenHtspListsStreamsDifferently()
    {
        // TVHeadend lists its components in its own order, which need not be the broadcaster's.
        // What FFmpeg will do is walk the program map, so that is the order that counts.
        var start = Subscription(
            Stream(esIndex: 9, "MPEG2AUDIO", language: "deu", channels: 2, sampleRateIndex: 4),
            Stream(esIndex: 2, "H264", width: 1280, height: 720, frameDuration: 1800));

        var service = Service((9, GermanAudioPid), (2, VideoPid));
        var programMap = Map((0x1b, VideoPid), (0x03, GermanAudioPid));

        var description = LiveStreamDescription.Build(start, programMap, service)!;

        Assert.Equal(MediaStreamType.Video, description.Streams[0].Type);
        Assert.Equal(1280, description.Streams[0].Width);
        Assert.Equal(MediaStreamType.Audio, description.Streams[1].Type);
        Assert.Equal(44100, description.Streams[1].SampleRate);
    }

    [Fact]
    public void AStreamInTheTransportButNotInTheDescriptionStillOccupiesItsIndex()
    {
        // Leaving a gap would shift every index after it, which is the same failure as counting
        // the EIT. What is not known is left unsaid, not left out.
        var start = Subscription(Stream(esIndex: 1, "H264", width: 720, height: 576, frameDuration: 3600));
        var service = Service((1, VideoPid));
        var programMap = Map((0x1b, VideoPid), (0x06, 600), (0x03, GermanAudioPid));

        var description = LiveStreamDescription.Build(start, programMap, service)!;

        Assert.Equal(3, description.Streams.Count);
        Assert.Equal([0, 1, 2], description.Streams.Select(stream => stream.Index));
        Assert.Equal(MediaStreamType.Video, description.Streams[0].Type);
        Assert.Equal(MediaStreamType.Data, description.Streams[1].Type);
        Assert.Null(description.Streams[1].Codec);
    }

    [Fact]
    public void SubtitlesAreDescribedWhereTheyCanBeNamed()
    {
        var start = Subscription(
            Stream(esIndex: 1, "H264", width: 1920, height: 1080, frameDuration: 1800),
            Stream(esIndex: 2, "DVBSUB", language: "deu"));

        var service = Service((1, VideoPid), (2, SubtitlePid));
        var programMap = Map((0x1b, VideoPid), (0x06, SubtitlePid));

        var description = LiveStreamDescription.Build(start, programMap, service)!;

        Assert.Equal(MediaStreamType.Subtitle, description.Streams[1].Type);
        Assert.Equal("dvb_subtitle", description.Streams[1].Codec);
        Assert.Equal("deu", description.Streams[1].Language);
    }

    [Fact]
    public void WithoutTheServiceTableNothingIsClaimedAboutTheOrder()
    {
        // The PID behind each es_index comes from an administrator-only API. Without it the
        // streams could still be described, but not placed -- and a description at the wrong
        // index is worse than none, because Jellyfin acts on it.
        var start = Subscription(Stream(esIndex: 1, "H264", width: 1920, height: 1080, frameDuration: 3600));

        Assert.Null(LiveStreamDescription.Build(start, Map((0x1b, VideoPid)), service: null));
    }

    [Fact]
    public void TheTwoHalvesOfAStreamMustBeTheSameService()
    {
        // A channel can map to several services. Combining one service's description with
        // another's video would be wrong in a way nothing downstream could detect, so it is
        // proven rather than assumed: every PID being delivered has to be one this service
        // carries.
        var service = Service((1, VideoPid), (2, GermanAudioPid));

        Assert.True(LiveStreamDescription.AgreesWith(
            Map((0x1b, VideoPid), (0x03, GermanAudioPid)),
            service));

        Assert.False(LiveStreamDescription.AgreesWith(
            Map((0x1b, 900), (0x03, 901)),
            service));
    }

    [Fact]
    public void APartialOverlapIsNotAgreement()
    {
        // Two services on one multiplex can share a PID -- a common audio track, say -- without
        // being the same service.
        var service = Service((1, VideoPid), (2, GermanAudioPid));

        Assert.False(LiveStreamDescription.AgreesWith(
            Map((0x1b, VideoPid), (0x03, 999)),
            service));
    }

    [Theory]
    [InlineData(3600, 25f)]
    [InlineData(1800, 50f)]
    [InlineData(3003, 29.97003f)]
    [InlineData(1501, 59.960026f)]
    public void TheFrameRateComesFromTheFrameDurationWithoutCorrection(int frameDuration, float expected)
    {
        // TVHeadend states the duration of one frame in the subscription's 90 kHz time base. A
        // halving rule applied on top of this is how a 50 fps broadcast was once published as
        // 100 fps.
        Assert.Equal(expected, HtspTimeBase.ToFrameRate(frameDuration)!.Value, 3);
    }

    [Fact]
    public void AnAbsentFrameDurationYieldsNoFrameRate()
    {
        Assert.Null(HtspTimeBase.ToFrameRate(null));
        Assert.Null(HtspTimeBase.ToFrameRate(0));
    }

    [Theory]
    [InlineData(0, 96000)]
    [InlineData(3, 48000)]
    [InlineData(4, 44100)]
    [InlineData(11, 8000)]
    public void TheSampleRateIsResolvedFromTheIndexTvheadendSends(int index, int expected)
    {
        // The field is called "rate" on the wire but carries es_sri, an index into the MPEG-4
        // sampling frequency table. Reporting it as a frequency gives a track sampled at 4 Hz.
        Assert.Equal(expected, HtspSampleRate.FromIndex(index));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(99)]
    public void AReservedSampleRateIndexIsReportedAsUnknown(int index)
    {
        Assert.Null(HtspSampleRate.FromIndex(index));
    }

    [Theory]
    [InlineData("H264", "h264")]
    [InlineData("MPEG2VIDEO", "mpeg2video")]
    [InlineData("HEVC", "hevc")]
    [InlineData("MPEG2AUDIO", "mp2")]
    [InlineData("AC3", "ac3")]
    [InlineData("EAC3", "eac3")]
    [InlineData("AAC-LATM", "aac")]
    [InlineData("AAC", "aac")]
    [InlineData("DVBSUB", "dvb_subtitle")]
    [InlineData("TELETEXT", "dvb_teletext")]
    public void TvheadendCodecNamesBecomeTheOnesADeviceProfileIsWrittenAgainst(string type, string expected)
    {
        Assert.Equal(expected, HtspCodecNames.ToJellyfinCodec(type));
    }

    [Theory]
    [InlineData("CA")]
    [InlineData("PCR")]
    [InlineData("SOMETHING-NEW")]
    [InlineData(null)]
    public void ATypeWithNoHonestCounterpartIsLeftUnnamed(string? type)
    {
        // An unknown codec makes Jellyfin transcode, which works. A plausible wrong one makes it
        // direct play something the client cannot decode, which does not.
        Assert.Null(HtspCodecNames.ToJellyfinCodec(type));
    }

    private static HtspSubscriptionStart Subscription(params HtspStreamInfo[] streams)
        => new()
        {
            SubscriptionId = 1,
            Streams = streams,
            SourceInfo = HtspSourceInfo.From(new HtspMessage()
                .Set("mux_uuid", "mux-1")
                .Set("service", "Das Erste HD")),
        };

    private static HtspStreamInfo Stream(
        int esIndex,
        string type,
        string? language = null,
        int? width = null,
        int? height = null,
        int? frameDuration = null,
        int? channels = null,
        int? sampleRateIndex = null)
        => new()
        {
            Index = esIndex,
            Type = type,
            Language = language,
            Width = width,
            Height = height,
            FrameDuration = frameDuration,
            Channels = channels,
            SampleRateIndex = sampleRateIndex,
        };

    private static ServiceDescription Service(params (int Index, int Pid)[] components)
        => new(
            "service-1",
            "Das Erste HD",
            [.. components.Select(component => new ServiceComponent(component.Index, component.Pid, null))]);

    private static ProgramMapTable Map(params (int StreamType, int Pid)[] entries)
        => new(
            1,
            VideoPid,
            [.. entries.Select(entry => new ProgramMapEntry((byte)entry.StreamType, entry.Pid))]);
}
