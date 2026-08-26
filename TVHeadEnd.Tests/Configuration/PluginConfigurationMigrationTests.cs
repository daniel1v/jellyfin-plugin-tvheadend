using TVHeadEnd.Configuration;
using TVHeadEnd.Tvheadend;
using Xunit;

namespace TVHeadEnd.Tests.Configuration;

public class PluginConfigurationMigrationTests
{
    [Fact]
    public void BorrowingTheChannelLogoIsOnUntilSomebodyTurnsItOff()
    {
        // A server upgrading into this has no such element in its stored configuration, and the
        // serialiser leaves an absent element at whatever the constructor set. On is the useful
        // default: on a broadcast listing the alternative is a wall of blank tiles.
        Assert.True(new PluginConfiguration().UseChannelLogoWhereArtworkIsMissing);
    }

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
}
