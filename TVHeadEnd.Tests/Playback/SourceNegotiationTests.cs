using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd;
using TVHeadEnd.Media;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// What the plugin offers Jellyfin, and what it deliberately leaves to Jellyfin to decide.
/// </summary>
public class SourceNegotiationTests
{
    // What Jellyfin for Android 2.7.1 advertises, taken from a real PlaybackInfo request.
    private const string TransportStreamProfile = "mpegts";
    private const string MatroskaProfile = "mkv";

    [Fact]
    public void EveryFormOfAChannelHasItsOwnStableIdentifier()
    {
        var native = ChannelSourceId.Create("42", StreamProfileRole.Native);
        var compatibility = ChannelSourceId.Create("42", StreamProfileRole.Mpeg2H264Compatibility);

        Assert.NotEqual(native, compatibility);
        Assert.Equal(native, ChannelSourceId.Create("42", StreamProfileRole.Native));
        Assert.NotEqual(native, ChannelSourceId.Create("43", StreamProfileRole.Native));
    }

    [Fact]
    public void AnIdentifierNamesBackTheRoleItWasMadeFor()
    {
        Assert.Equal(
            StreamProfileRole.Mpeg2H264Compatibility,
            ChannelSourceId.Resolve("42", ChannelSourceId.Create("42", StreamProfileRole.Mpeg2H264Compatibility)));

        Assert.Equal(
            StreamProfileRole.Native,
            ChannelSourceId.Resolve("42", ChannelSourceId.Create("42", StreamProfileRole.Native)));

        Assert.Null(ChannelSourceId.Resolve("42", "something-else"));
    }

    [Fact]
    public void NoSourceIdentifierIsTheChannelItemIdentifier()
    {
        // Jellyfin builds the live stream open token out of the item identifier and the source
        // identifier, and sorts a source carrying the item identifier ahead of every other --
        // which would settle the choice before the device profile was ever consulted.
        const string ItemId = "2524023830cee68580a286d99349fc9d";

        Assert.NotEqual(ItemId, ChannelSourceId.Create("159026356", StreamProfileRole.Native));
        Assert.NotEqual(ItemId, ChannelSourceId.Create("159026356", StreamProfileRole.Mpeg2H264Compatibility));
    }

    [Fact]
    public void APendingSourceCanBeEvaluatedAgainstADeviceProfile()
    {
        // Withheld, a compatibility source could never be chosen: Jellyfin skips what does not
        // claim direct play, and would transcode the broadcast instead. The flag says "evaluate
        // this", not "the client can play it" -- Jellyfin overwrites it with its own verdict.
        var pending = JellyfinMediaSourceMapper.CreatePending(
            "42",
            StreamProfileRole.Mpeg2H264Compatibility,
            null);

        Assert.True(pending.SupportsDirectPlay);
        Assert.True(pending.RequiresOpening);
        Assert.Equal(MediaProtocol.Http, pending.Protocol);
    }

    [Fact]
    public void ABroadcastIsOfferedAsTransportStreamAndARenderingAsMatroska()
    {
        var native = JellyfinMediaSourceMapper.CreatePending(
            "42",
            StreamProfileRole.Native,
            Native("mpegts,ts", "h264"));

        var compatibility = JellyfinMediaSourceMapper.CreatePending(
            "42",
            StreamProfileRole.Mpeg2H264Compatibility,
            JellyfinMediaSourceMapper.ProjectCompatibility(Native("mpegts,ts", "mpeg2video")));

        Assert.True(ContainerHelper.ContainsContainer(TransportStreamProfile, native.Container));
        Assert.Equal("mkv", compatibility.Container);
        Assert.True(ContainerHelper.ContainsContainer(MatroskaProfile, compatibility.Container));
    }

    [Fact]
    public void AnUnprovenRenderingClaimsOnlyWhatItsRoleGuarantees()
    {
        var projected = JellyfinMediaSourceMapper.ProjectCompatibility(Native("mpegts,ts", "mpeg2video"));

        var video = Assert.Single(projected!.Streams, stream => stream.Type == MediaStreamType.Video);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(720, video.Width);
        Assert.Null(video.Profile);
        Assert.Null(video.Level);
        Assert.Null(video.BitRate);
        Assert.Null(video.RealFrameRate);

        var audio = Assert.Single(projected.Streams, stream => stream.Type == MediaStreamType.Audio);
        Assert.Equal("deu", audio.Language);
        Assert.Null(audio.Codec);
        Assert.Null(audio.Channels);
    }

    [Fact]
    public void NothingIsProjectedWithoutABroadcastToProjectFrom()
    {
        Assert.Null(JellyfinMediaSourceMapper.ProjectCompatibility(null));
    }

    [Theory]
    [InlineData("mpegts,ts")]
    [InlineData("mpegts")]
    [InlineData("ts")]
    [InlineData("MPEG-TS")]
    public void AnySpellingOfTransportStreamIsPublishedTheSameWay(string observed)
    {
        // Jellyfin compares container strings literally, with no alias resolution, so publishing
        // the URL's file extension answered ContainerNotSupported on every client.
        Assert.Equal("mpegts,ts", JellyfinMediaSourceMapper.NormalizeContainer(observed));
        Assert.True(ContainerHelper.ContainsContainer(
            TransportStreamProfile,
            JellyfinMediaSourceMapper.NormalizeContainer(observed)));
    }

    [Theory]
    [InlineData("matroska,webm")]
    [InlineData("matroska")]
    [InlineData("mkv")]
    public void AnySpellingOfMatroskaIsPublishedAsMkv(string observed)
    {
        // webm is a separate device profile entry and does not stand for an H.264 Matroska
        // stream, so offering it would invite a match on a claim the stream does not keep.
        Assert.Equal("mkv", JellyfinMediaSourceMapper.NormalizeContainer(observed));
    }

    [Fact]
    public void AStoredDescriptorCannotUndoTheContainerTheClientWillReceive()
    {
        var opened = JellyfinMediaSourceMapper.CreateOpened(
            "42",
            StreamProfileRole.Native,
            Native("mpegts,ts", "h264"),
            "/buffers/tvheadend-1.ts",
            "http://server/LiveTv/LiveStreamFiles/1/stream.ts",
            CompatibilityContainer.TransportStream);

        Assert.Equal("mpegts,ts", opened.Container);
        Assert.Equal(MediaProtocol.File, opened.Protocol);
        Assert.True(opened.IsInfiniteStream);
        Assert.False(opened.RequiresOpening);
        Assert.True(opened.SupportsDirectPlay);
    }

    [Fact]
    public void ARenderingIsServedOverTheRouteThatCanAnnounceMatroska()
    {
        var opened = JellyfinMediaSourceMapper.CreateOpened(
            "42",
            StreamProfileRole.Mpeg2H264Compatibility,
            Observed("matroska,webm", "h264"),
            "/spool/tvheadend-1.mkv",
            "http://server/LiveTv/LiveStreamFiles/1/stream.mkv",
            CompatibilityContainer.Matroska);

        Assert.Equal("mkv", opened.Container);
        Assert.Equal(MediaProtocol.Http, opened.Protocol);
        Assert.EndsWith("stream.mkv", opened.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ARenderingThatIsNotWhatItPromisedIsRejected()
    {
        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(
            StreamProfileRole.Mpeg2H264Compatibility,
            Observed("matroska,webm", "mpeg2video")));

        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(
            StreamProfileRole.Mpeg2H264Compatibility,
            Observed("mpegts,ts", "h264") with { IsTransportStream = true }));

        Assert.False(JellyfinMediaSourceMapper.SatisfiesContract(
            StreamProfileRole.Mpeg2H264Compatibility,
            null));
    }

    [Fact]
    public void ARenderingThatKeepsItsPromiseIsAccepted()
    {
        Assert.True(JellyfinMediaSourceMapper.SatisfiesContract(
            StreamProfileRole.Mpeg2H264Compatibility,
            Observed("matroska,webm", "h264")));
    }

    [Fact]
    public void TheBroadcastHasNoContractToBreak()
    {
        Assert.True(JellyfinMediaSourceMapper.SatisfiesContract(StreamProfileRole.Native, null));
    }

    [Fact]
    public void OnlyTheBroadcastIsEverShared()
    {
        using var native = Stream("159026356", StreamProfileRole.Native);

        Assert.True(native.EnableStreamSharing);
        Assert.False(LiveTvService.CanBeReusedFor(native, "159026356", StreamProfileRole.Mpeg2H264Compatibility));

        // Not yet opened, so it has nothing to share either way.
        Assert.False(native.HasBuffer);
        Assert.False(LiveTvService.CanBeReusedFor(native, "159026356", StreamProfileRole.Native));
    }

    private static ChannelMediaDescriptor Native(string container, string codec)
        => new()
        {
            ChannelId = "42",
            Container = container,
            IsTransportStream = true,
            Streams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = codec,
                    Width = 720,
                    Height = 576,
                    Profile = "Main",
                    Level = 8,
                    BitRate = 4_710_991,
                    RealFrameRate = 25,
                    IsInterlaced = true,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "mp2",
                    Language = "deu",
                    Channels = 2,
                },
            ],
        };

    private static ChannelMediaDescriptor Observed(string container, string codec)
        => new()
        {
            ChannelId = "42",
            Container = container,
            IsTransportStream = false,
            Streams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = codec }],
        };

    private static TvheadendLiveStream Stream(string channelId, StreamProfileRole role)
        => new(
            channelId,
            role,
            "http://tvheadend.invalid/stream",
            new Dictionary<string, string>(),
            new MediaSourceInfo(),
            Path.Combine(Path.GetTempPath(), "tvheadend-test-" + Guid.NewGuid().ToString("N")),
            1,
            describedAlready: true,
            new NeverUsedHttpClientFactory(),
            NullLogger.Instance);

    private sealed class NeverUsedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
