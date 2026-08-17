using TVHeadEnd.Configuration;
using TVHeadEnd.Tvheadend;
using Xunit;

namespace TVHeadEnd.Tests.Configuration;

public class PluginConfigurationMigrationTests
{
    [Fact]
    public void TheOldProfileSettingBecomesTheDvrProfile()
    {
        // It always named a TVHeadend DVR configuration. The rename is what makes the new stream
        // profile settings unambiguous, so the value has to travel rather than be reinterpreted.
        var configuration = new PluginConfiguration { DvrProfile = string.Empty };
#pragma warning disable CS0618
        configuration.Profile = "my-dvr-config";
#pragma warning restore CS0618

        Assert.True(configuration.Migrate());

        Assert.Equal("my-dvr-config", configuration.DvrProfile);
#pragma warning disable CS0618
        Assert.Null(configuration.Profile);
#pragma warning restore CS0618
    }

    [Fact]
    public void AnAlreadyMigratedConfigurationIsLeftAlone()
    {
        var configuration = new PluginConfiguration { DvrProfile = "already-set" };

        Assert.False(configuration.Migrate());
        Assert.Equal("already-set", configuration.DvrProfile);
    }

    [Fact]
    public void MigrationNeverOverwritesAnExistingDvrProfile()
    {
        var configuration = new PluginConfiguration { DvrProfile = "chosen" };
#pragma warning disable CS0618
        configuration.Profile = "stale";
#pragma warning restore CS0618

        configuration.Migrate();

        Assert.Equal("chosen", configuration.DvrProfile);
    }

    [Fact]
    public void TheNativeStreamProfileDefaultsToPass()
    {
        var configuration = new PluginConfiguration { NativeStreamProfile = "  " };

        Assert.True(configuration.Migrate());
        Assert.Equal(TvheadendStreamProfiles.DefaultNativeProfile, configuration.NativeStreamProfile);
    }

    [Fact]
    public void LearnedChannelStateIsHandedOverOnceAndThenCleared()
    {
        // Learned state does not belong in the configuration; it moves to the descriptor store.
        var configuration = new PluginConfiguration();
#pragma warning disable CS0618
        configuration.ChannelsWithoutIdr = ["1460599120", "1460599121"];
#pragma warning restore CS0618

        Assert.Equal(2, configuration.TakeChannelsWithoutIdr().Length);
        Assert.Empty(configuration.TakeChannelsWithoutIdr());
    }

    [Fact]
    public void ANewConfigurationCarriesTheDocumentedDefaults()
    {
        var configuration = new PluginConfiguration();

        // The names the documentation tells an administrator to create. They are only defaults:
        // a role whose profile TVHeadend does not report is reported as not found and not used,
        // so naming them here costs nothing on a server that has none of them.
        Assert.Equal(TvheadendStreamProfiles.DefaultNativeProfile, configuration.NativeStreamProfile);
        Assert.Equal("jellyfin-h264", configuration.Mpeg2H264CompatibilityProfile);
        Assert.Equal("jellyfin-idr", configuration.H264IdrNormalizationProfile);
        Assert.False(configuration.AnalyzeChannelFormatsOnRefresh);
    }
}
