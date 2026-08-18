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
    public void ANewConfigurationCarriesTheDocumentedDefaults()
    {
        var configuration = new PluginConfiguration();

        // Only the native role has a default, because only it is required. Naming the optional
        // roles here would have every fresh installation claim two profiles that almost certainly
        // do not exist, and the settings page would report both as missing before the
        // administrator had decided whether to want them at all.
        Assert.Equal(TvheadendStreamProfiles.DefaultNativeProfile, configuration.NativeStreamProfile);
        Assert.Equal(string.Empty, configuration.Mpeg2H264CompatibilityProfile);
        Assert.False(configuration.AnalyzeChannelFormatsOnRefresh);
    }
}
