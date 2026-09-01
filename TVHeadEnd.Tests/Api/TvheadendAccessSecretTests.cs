using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TVHeadEnd.Api;
using TVHeadEnd.Configuration;
using Xunit;

namespace TVHeadEnd.Tests.Api;

/// <summary>
/// The secret every address this plugin publishes is signed with.
/// </summary>
/// <remarks>
/// Jellyfin stores those addresses -- on a recording's media source, on a channel's image path --
/// so a secret that changed would leave every stored item pointing at something that no longer
/// verifies. It is therefore created once and never rotated, and the one moment that could go
/// wrong is two callers arriving together on a fresh install.
/// </remarks>
public class TvheadendAccessSecretTests
{
    [Fact]
    public void AStoredSecretIsUsedAndNothingIsWritten()
    {
        var configuration = new RecordingConfiguration
        {
            Stored = { RecordingAccessSecret = "9f1a3b5c7d9e1f3a5b7c9d1e3f5a7b9c1d3e5f7a9b1c3d5e7f9a1b3c5d7e9f1a" },
        };

        var secret = new TvheadendAccessSecret(configuration, NullLogger<TvheadendAccessSecret>.Instance);

        Assert.Equal(configuration.Stored.RecordingAccessSecret, secret.Ensure());
        Assert.Equal(0, configuration.Saves);
    }

    [Fact]
    public void AFreshInstallCreatesOneAndKeepsIt()
    {
        var configuration = new RecordingConfiguration();
        var secret = new TvheadendAccessSecret(configuration, NullLogger<TvheadendAccessSecret>.Instance);

        var created = secret.Ensure();

        Assert.False(string.IsNullOrEmpty(created));
        Assert.Equal(created, configuration.Stored.RecordingAccessSecret);
        Assert.Equal(1, configuration.Saves);

        // Never rotated. Asking again is asking about the same addresses.
        Assert.Equal(created, secret.Ensure());
        Assert.Equal(1, configuration.Saves);
    }

    [Fact]
    public async Task TwoCallersArrivingTogetherGetTheSameSecret()
    {
        // The second must return what the first stored rather than replace it: a secret replaced
        // half a second after the first address was minted invalidates that address.
        var configuration = new RecordingConfiguration();
        var secret = new TvheadendAccessSecret(configuration, NullLogger<TvheadendAccessSecret>.Instance);

        var answers = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(secret.Ensure)));

        Assert.Single(answers.Distinct(StringComparer.Ordinal));
        Assert.Equal(1, configuration.Saves);
    }

    [Fact]
    public void TheSettingKeepsTheNameEveryExistingServerStoredItUnder()
    {
        // Renaming it would orphan the secret on every server that already has one, and with it
        // every address Jellyfin has stored.
        Assert.NotNull(typeof(PluginConfiguration).GetProperty("RecordingAccessSecret"));
    }

    /// <summary>
    /// A configuration that counts how often it was written.
    /// </summary>
    private sealed class RecordingConfiguration : IPluginConfigurationSource
    {
        public event EventHandler? Changed;

        public PluginConfiguration Stored { get; } = new();

        public int Saves { get; private set; }

        public PluginConfiguration Current => Stored;

        public void Save()
        {
            Saves++;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
