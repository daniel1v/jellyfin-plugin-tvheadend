using System;
using TVHeadEnd.Configuration;
using TVHeadEnd.Tvheadend;
using Xunit;

namespace TVHeadEnd.Tests.Configuration;

/// <summary>
/// The one place where what somebody typed becomes settings the TVHeadend side can work from.
/// </summary>
/// <remarks>
/// Everything below this line deals with a host name known not to be empty and a priority known to
/// be in range. That is only true because this validates them, and only useful if it validates
/// them the way it always has -- a setting quietly reinterpreted is a working installation that
/// stops working after an upgrade, with nothing in any log to say why.
/// </remarks>
public class TvheadendSettingsBridgeTests
{
    [Fact]
    public void WhatWasTypedIsWhatArrives()
    {
        var settings = PluginTvheadendSettingsSource.Validate(new PluginConfiguration
        {
            TVH_ServerName = "tvh.local",
            HTTP_Port = 9981,
            HTSP_Port = 9982,
            Username = "frigo",
            Password = "secret",
            Priority = 3,
            DvrProfile = "recordings",
            ChannelType = "Radio",
            LiveBufferSizeMegabytes = 512,
        });

        Assert.Equal("tvh.local", settings.Host);
        Assert.Equal(9981, settings.HttpPort);
        Assert.Equal(9982, settings.HtspPort);
        Assert.Equal("frigo", settings.UserName);
        Assert.Equal("secret", settings.Password);
        Assert.Equal(3, settings.Priority);
        Assert.Equal("recordings", settings.DvrProfile);
        Assert.Equal("Radio", settings.ChannelTypeForOther);
        Assert.Equal(512, settings.LiveBufferSizeMegabytes);
    }

    [Fact]
    public void AHostNameWithAStraySpaceIsATypoAndAPasswordWithOneIsAPassword()
    {
        // The asymmetry is the point. Trimming a password silently turns a working credential into
        // a failing login nobody can explain.
        var settings = PluginTvheadendSettingsSource.Validate(Configured(configuration =>
        {
            configuration.TVH_ServerName = "  tvh.local  ";
            configuration.Username = "  frigo  ";
            configuration.Password = "  secret  ";
        }));

        Assert.Equal("tvh.local", settings.Host);
        Assert.Equal("frigo", settings.UserName);
        Assert.Equal("  secret  ", settings.Password);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public void APriorityTvheadendWouldRefuseFallsBackToNormal(int priority)
    {
        // DVR_PRIO_IMPORTANT through DVR_PRIO_NOTSET is the range the server accepts; anything
        // else would be rejected on every recording rather than once here.
        Assert.Equal(2, PluginTvheadendSettingsSource.Validate(Configured(c => c.Priority = priority)).Priority);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void APriorityTvheadendAcceptsIsPassedOnUnchanged(int priority)
    {
        Assert.Equal(priority, PluginTvheadendSettingsSource.Validate(Configured(c => c.Priority = priority)).Priority);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoServerConfiguredThereIsNothingToConnectTo(string serverName)
    {
        // Refused here rather than half way through a connection attempt, where it would look like
        // the server being unreachable.
        Assert.Throws<InvalidOperationException>(
            () => PluginTvheadendSettingsSource.Validate(Configured(c => c.TVH_ServerName = serverName)));
    }

    [Fact]
    public void ChangingWhichServerToTalkToIsWhatCountsAsADifferentServer()
    {
        // What the connection compares to decide whether a settings change is worth reconnecting
        // for. Everything on this list identifies the server or how to get into it.
        var before = PluginTvheadendSettingsSource.Validate(Configured(_ => { }));

        Assert.False(SameServer(before, Change(c => c.TVH_ServerName = "other.local")));
        Assert.False(SameServer(before, Change(c => c.HTSP_Port = 9992)));
        Assert.False(SameServer(before, Change(c => c.HTTP_Port = 9991)));
        Assert.False(SameServer(before, Change(c => c.Username = "somebody")));
        Assert.False(SameServer(before, Change(c => c.Password = "different")));
    }

    [Fact]
    public void ChangingSomethingElseIsNotAReasonToReconnect()
    {
        // A viewer changing the recording priority or the buffer size should not drop the
        // connection, discard every catalogue and re-sync the whole server.
        var before = PluginTvheadendSettingsSource.Validate(Configured(_ => { }));

        Assert.True(SameServer(before, Change(c => c.Priority = 4)));
        Assert.True(SameServer(before, Change(c => c.DvrProfile = "elsewhere")));
        Assert.True(SameServer(before, Change(c => c.ChannelType = "Radio")));
        Assert.True(SameServer(before, Change(c => c.LiveBufferSizeMegabytes = 1024)));
    }

    /// <summary>
    /// The connection's own rule, asked of it rather than restated here.
    /// </summary>
    private static bool SameServer(TvheadendSettings first, TvheadendSettings second)
        => (bool)typeof(TvheadendConnection)
            .GetMethod(
                "DescribesSameServer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [first, second])!;

    private static TvheadendSettings Change(Action<PluginConfiguration> edit)
        => PluginTvheadendSettingsSource.Validate(Configured(edit));

    private static PluginConfiguration Configured(Action<PluginConfiguration> edit)
    {
        var configuration = new PluginConfiguration
        {
            TVH_ServerName = "tvh.local",
            HTTP_Port = 9981,
            HTSP_Port = 9982,
            Username = "frigo",
            Password = "secret",
            Priority = 2,
            DvrProfile = "recordings",
            ChannelType = "Ignore",
            LiveBufferSizeMegabytes = 512,
        };

        edit(configuration);
        return configuration;
    }
}
