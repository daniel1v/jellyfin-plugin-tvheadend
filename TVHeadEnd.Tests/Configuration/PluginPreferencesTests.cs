using System;
using TVHeadEnd.Configuration;
using Xunit;

namespace TVHeadEnd.Tests.Configuration;

/// <summary>
/// The plugin's own behaviour settings, read through the one door onto the stored configuration.
/// </summary>
/// <remarks>
/// Kept apart from the TVHeadend settings on purpose: a viewer adjusting their padding by a minute
/// must not look, to everything downstream, like the server having moved.
/// </remarks>
public class PluginPreferencesTests
{
    [Fact]
    public void WhatWasStoredIsWhatIsRead()
    {
        var source = new PluginPreferencesSource(new StoredConfiguration(new PluginConfiguration
        {
            Pre_Padding = 120,
            Post_Padding = 300,
            HideRecordingsChannel = true,
            UseChannelLogoWhereArtworkIsMissing = false,
        }));

        var preferences = source.Current;

        Assert.Equal(120, preferences.PrePaddingSeconds);
        Assert.Equal(300, preferences.PostPaddingSeconds);
        Assert.True(preferences.HideRecordingsChannel);
        Assert.False(preferences.UseChannelLogoWhereArtworkIsMissing);
    }

    [Fact]
    public void EachReadSeesTheConfigurationAsItStandsNow()
    {
        // Read at the moment it matters -- when a timer is created, when a listing is built -- so
        // a viewer who changes one expects the next thing they do to use it.
        var stored = new PluginConfiguration { Pre_Padding = 60 };
        var source = new PluginPreferencesSource(new StoredConfiguration(stored));

        Assert.Equal(60, source.Current.PrePaddingSeconds);

        stored.Pre_Padding = 90;

        Assert.Equal(90, source.Current.PrePaddingSeconds);
    }

    private sealed class StoredConfiguration(PluginConfiguration configuration) : IPluginConfigurationSource
    {
        public event EventHandler? Changed;

        public PluginConfiguration Current => configuration;

        public void Save() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
