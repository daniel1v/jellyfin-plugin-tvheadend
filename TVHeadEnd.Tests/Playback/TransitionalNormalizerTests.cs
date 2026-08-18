using System;
using MediaBrowser.Model.Entities;
using TVHeadEnd.Media;
using TVHeadEnd.Playback;
using TVHeadEnd.Streaming;
using TVHeadEnd.Tvheadend;
using Xunit;

namespace TVHeadEnd.Tests.Playback;

/// <summary>
/// The transitional plugin-side encoder stands down for a TVHeadend profile that has been
/// proven, and for nothing less. A profile name is a claim; an opened and inspected stream is
/// evidence.
/// </summary>
public class TransitionalNormalizerTests
{
    [Fact]
    public void AConfiguredButUnprovenIdrProfileDoesNotStandTheEncoderDown()
    {
        // The regression this exists for: a configured name counted as usable, the encoder was
        // switched off, the profile turned out to produce something unplayable, and the affected
        // client was left with a broadcast its decoder cannot start.
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");

        Assert.True(LiveTvService.UsesTransitionalNormalizer(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void ADiscoveredButUnprovenIdrProfileDoesNotStandTheEncoderDown()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");
        profiles.ApplyDiscovery(["pass", "jellyfin-idr"]);

        Assert.Equal(StreamProfileState.NotValidated, StateOf(profiles, StreamProfileRole.H264IdrNormalization));
        Assert.True(LiveTvService.UsesTransitionalNormalizer(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void AProvenIdrProfileReplacesTheEncoder()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");
        profiles.ApplyDiscovery(["pass", "jellyfin-idr"]);
        profiles.RecordValidation(StreamProfileRole.H264IdrNormalization, satisfiesContract: true);

        Assert.False(LiveTvService.UsesTransitionalNormalizer(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void AnIdrProfileProvenBrokenBringsTheEncoderBack()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");
        profiles.RecordValidation(StreamProfileRole.H264IdrNormalization, satisfiesContract: false);

        Assert.Equal(StreamProfileState.Invalid, StateOf(profiles, StreamProfileRole.H264IdrNormalization));
        Assert.True(LiveTvService.UsesTransitionalNormalizer(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void ProofFromAnEarlierRunIsOnlyHonouredForTheProfileStillConfigured()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");
        profiles.RestoreValidation(StreamProfileRole.H264IdrNormalization, "some-other-profile");

        Assert.True(LiveTvService.UsesTransitionalNormalizer(profiles, transitionalEncoderEnabled: true));

        profiles.RestoreValidation(StreamProfileRole.H264IdrNormalization, "jellyfin-idr");

        Assert.False(LiveTvService.UsesTransitionalNormalizer(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void TheEncoderIsNotUsedWhenItHasBeenTurnedOff()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, null);

        Assert.False(LiveTvService.UsesTransitionalNormalizer(profiles, transitionalEncoderEnabled: false));
    }

    [Fact]
    public void AMissingCompatibilityProfileLeavesTheNativeRoleAlone()
    {
        // A broken or absent compatibility profile is not allowed to cost every other client the
        // broadcast it was going to direct play.
        var profiles = new TvheadendStreamProfiles("pass", null, null);
        profiles.ApplyDiscovery(["pass"]);
        profiles.RecordValidation(StreamProfileRole.Mpeg2H264Compatibility, satisfiesContract: false);

        Assert.True(profiles.IsUsable(StreamProfileRole.Native));
        Assert.Equal("pass", profiles.GetProfileName(StreamProfileRole.Native));

        var offers = PlaybackVariantPolicy.SelectVariants(
            Descriptor("h264", H264RandomAccessKind.Idr),
            PlaybackVariantAvailability.NativeOnly,
            new PlaybackClientContext("Some Other Client", "1.0", "Device", "id"));

        Assert.Equal(PlaybackVariant.Native, Assert.Single(offers).Variant);
    }


    [Fact]
    public void AServerProfileThatExistsDoesTheEncodingEvenBeforeItIsProven()
    {
        // Otherwise the profile could never be tried, so it could never be proven, so the
        // transitional encoder would carry every stream forever. Trying it costs one open, and a
        // failed open drops the role back to the encoder.
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");
        profiles.ApplyDiscovery(["pass", "jellyfin-idr"]);

        Assert.False(LiveTvService.NormalizesLocally(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void WithNoServerProfileTheEncoderDoesTheWork()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, null);

        Assert.True(LiveTvService.NormalizesLocally(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void AProfileTheServerDoesNotHaveLeavesTheWorkToTheEncoder()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");
        profiles.ApplyDiscovery(["pass"]);

        Assert.True(LiveTvService.NormalizesLocally(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void AProfileProvenBrokenLeavesTheWorkToTheEncoder()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");
        profiles.ApplyDiscovery(["pass", "jellyfin-idr"]);
        profiles.RecordValidation(StreamProfileRole.H264IdrNormalization, satisfiesContract: false);

        Assert.True(LiveTvService.NormalizesLocally(profiles, transitionalEncoderEnabled: true));
    }

    [Fact]
    public void AProvenProfileDoesTheEncoding()
    {
        var profiles = new TvheadendStreamProfiles("pass", null, "jellyfin-idr");
        profiles.ApplyDiscovery(["pass", "jellyfin-idr"]);
        profiles.RecordValidation(StreamProfileRole.H264IdrNormalization, satisfiesContract: true);

        Assert.False(LiveTvService.NormalizesLocally(profiles, transitionalEncoderEnabled: true));
    }
    private static StreamProfileState StateOf(TvheadendStreamProfiles profiles, StreamProfileRole role)
    {
        foreach (var status in profiles.GetStatus())
        {
            if (status.Role == role)
            {
                return status.State;
            }
        }

        throw new InvalidOperationException("The role was not reported at all.");
    }

    private static ChannelMediaDescriptor Descriptor(string codec, H264RandomAccessKind randomAccess)
        => new()
        {
            ChannelId = "42",
            Container = "mpegts,ts",
            IsTransportStream = true,
            RandomAccess = randomAccess,
            Streams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = codec }],
        };
}
