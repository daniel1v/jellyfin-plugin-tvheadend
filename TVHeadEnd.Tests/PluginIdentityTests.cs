using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TVHeadEnd.Playback;
using Xunit;

namespace TVHeadEnd.Tests;

/// <summary>
/// The plugin's identity is spelled in five places -- the assembly, the configuration page,
/// build.yaml, the manifest this repository is consumed as a plugin repository through, and
/// whatever a release names its package. Jellyfin matches an installed plugin to its repository
/// entry by GUID alone, so a single one of those disagreeing means the plugin silently stops
/// being offered updates. Nothing in a build catches that, which is why these do.
/// </summary>
public class PluginIdentityTests
{
    /// <summary>
    /// The GUID of the plugin published by the Jellyfin project. TVHeadend EX used to carry it,
    /// which is what made the two the same plugin as far as any server was concerned.
    /// </summary>
    private const string OfficialPluginId = "3fd018e5-5e78-4e58-b280-a0c068febee0";

    private const string PluginName = "TVHeadend EX";

    private static Guid AssemblyId =>
        ((Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin))).Id;

    [Fact]
    public void TheAssemblyDoesNotClaimTheOfficialPluginIdentity()
    {
        Assert.NotEqual(new Guid(OfficialPluginId), AssemblyId);
    }

    [Fact]
    public void TheAssemblyIsNamedTVHeadendEx()
    {
        Assert.Equal(PluginName, ((Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin))).Name);
    }

    [Fact]
    public void TheConfigurationPageAsksForTheSamePluginTheAssemblyIs()
    {
        // The page loads its settings by GUID. Get it wrong and the settings page is simply empty,
        // with nothing anywhere saying why.
        Assert.Contains(AssemblyId.ToString(), ReadRepositoryFile("TVHeadEnd/Web/tvheadend.js"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildYamlDescribesTheSamePluginTheAssemblyIs()
    {
        var yaml = ReadRepositoryFile("build.yaml");

        Assert.Contains($"guid: \"{AssemblyId}\"", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"name: \"{PluginName}\"", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(OfficialPluginId, yaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheManifestDescribesTheSamePluginBuildYamlDoes()
    {
        // Both are written by tools/release.ps1 from build.yaml, so this is a check on the script
        // as much as on the files: they are one contract with two readers.
        using var manifest = JsonDocument.Parse(ReadRepositoryFile("manifest.json"));
        var package = Assert.Single(manifest.RootElement.EnumerateArray());

        Assert.Equal(AssemblyId, package.GetProperty("guid").GetGuid());
        Assert.Equal(PluginName, package.GetProperty("name").GetString());
    }

    [Fact]
    public void TheManifestSurvivedBeingReadAndWrittenAsUtf8()
    {
        // Windows PowerShell 5.1 reads a file in the system ANSI code page unless told otherwise,
        // which turned an em dash into "a-EUR" and then into something longer the next time the
        // entry was carried forward. The characters are the evidence; the fix is in release.ps1.
        var manifest = ReadRepositoryFile("manifest.json");

        Assert.DoesNotContain("â€", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("Ã¢", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPublishedVersionIsDownloadableFromThisRepository()
    {
        using var manifest = JsonDocument.Parse(ReadRepositoryFile("manifest.json"));
        var package = Assert.Single(manifest.RootElement.EnumerateArray());

        foreach (var version in package.GetProperty("versions").EnumerateArray())
        {
            var number = version.GetProperty("version").GetString();
            var source = version.GetProperty("sourceUrl").GetString();

            Assert.NotNull(source);
            Assert.StartsWith("https://github.com/daniel1v/", source, StringComparison.Ordinal);

            // The tag a release is published under, and the version Jellyfin installs, are the
            // same number written twice. A package uploaded to the wrong tag is a 404 nobody sees
            // until somebody tries to install it.
            Assert.Contains($"/v{number}/", source, StringComparison.Ordinal);
            Assert.Contains(number!, source[(source.LastIndexOf('/') + 1)..], StringComparison.Ordinal);

            // The package name follows the plugin's name, not the one it forked from.
            Assert.Contains("tvheadend-ex_", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheManifestListsNothingBuiltUnderTheOldPluginIdentity()
    {
        // 14.0.0.1 to 14.0.0.3 were built and published while this fork still carried the official
        // plugin's GUID. Carrying them into the EX manifest offered them under an identity they
        // were never built with: installing one hands the server an assembly naming the old GUID,
        // which then drops out of this plugin's update path and reports itself as a different
        // plugin. A version history belongs to the plugin that made it.
        using var manifest = JsonDocument.Parse(ReadRepositoryFile("manifest.json"));
        var package = Assert.Single(manifest.RootElement.EnumerateArray());

        var lastPreExVersion = new Version(14, 0, 0, 3);

        foreach (var entry in package.GetProperty("versions").EnumerateArray())
        {
            var version = Version.Parse(entry.GetProperty("version").GetString()!);
            Assert.True(
                version > lastPreExVersion,
                $"The manifest lists {version}, which predates the TVHeadend EX identity.");
        }
    }

    [Fact]
    public void TheVersionBeingBuiltIsPastTheOnesTheOldIdentityUsed()
    {
        // A first EX build reusing 14.0.0.3 would collide with the release already published under
        // that number, and with the tag the release script derives from it.
        var yaml = ReadRepositoryFile("build.yaml");
        var version = Version.Parse(ReadScalar(yaml, "version"));

        Assert.True(version > new Version(14, 0, 0, 3), $"build.yaml is still at {version}.");

        // The assembly is built from Directory.Build.props, the package from build.yaml. Two
        // numbers for one release is two releases as far as anything reading them is concerned.
        var props = ReadRepositoryFile("Directory.Build.props");
        Assert.Contains($"<Version>{version}</Version>", props, StringComparison.Ordinal);
        Assert.Contains($"<AssemblyVersion>{version}</AssemblyVersion>", props, StringComparison.Ordinal);
        Assert.Contains($"<FileVersion>{version}</FileVersion>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseScriptRefusesAVersionHistoryFromADifferentPlugin()
    {
        // The rule that stops the last mistake happening again: the script carries earlier versions
        // forward only where the manifest it is reading describes the same plugin.
        var script = ReadRepositoryFile("tools/release.ps1");

        Assert.Contains("$plugin.guid -ne $guid", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseScriptTagsTheCommitThatDescribesTheRelease()
    {
        // The rule that stops the mistake before that one. Publishing used to happen before the
        // manifest commit was pushed, so GitHub tagged whatever the remote was at -- v14.0.0.4's
        // tag points at the commit before its own manifest. Publishing now names the target.
        var script = ReadRepositoryFile("tools/release.ps1");

        Assert.Contains("--target $head", script, StringComparison.Ordinal);
        Assert.Contains("rev-parse '@{upstream}'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseScriptPublishesOnlyWhatWasPrepared()
    {
        // Publishing must not be able to produce a package. A publish that can build one is a
        // publish that can release something other than the artefact that was tested, and the
        // difference would not be visible afterwards.
        var script = ReadRepositoryFile("tools/release.ps1");
        var publishing = script[script.IndexOf("if ($Publish) {", StringComparison.Ordinal)..];
        var preparing = publishing.IndexOf("# Prepare:", StringComparison.Ordinal);

        Assert.True(preparing > 0, "release.ps1 no longer has a separate prepare phase.");
        publishing = publishing[..preparing];

        Assert.DoesNotContain("Compress-Archive", publishing, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", publishing, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Json", publishing, StringComparison.Ordinal);

        // And it refuses rather than proceeds when what it holds is not what the manifest says.
        Assert.Contains("$entry.checksum -ne $checksum", publishing, StringComparison.Ordinal);
        Assert.Contains("$entry.sourceUrl -ne $sourceUrl", publishing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ServiceName", "TVHclient LiveTvService")]
    [InlineData("RecordingsChannelName", "TVHeadEnd Recordings")]
    public void TheNamesJellyfinDerivesItsOwnIdentifiersFromDoNotMove(string constant, string name)
    {
        // These are not product names and renaming them is not cosmetic. Jellyfin hashes them into
        // the identifiers it stores against every recording and every live TV item, so changing one
        // orphans everything already in the database from the plugin that put it there. The visible
        // product is called TVHeadend EX; these stay as they are.
        var field = typeof(TvheadendItems).GetField(constant, BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Equal(name, field!.GetRawConstantValue());
    }

    private static string ReadScalar(string yaml, string key)
    {
        foreach (var line in yaml.Split('\n'))
        {
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                return line[(key.Length + 1)..].Trim().Trim('"');
            }
        }

        throw new InvalidOperationException($"build.yaml has no '{key}'");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build.yaml")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath), Encoding.UTF8);
    }
}
