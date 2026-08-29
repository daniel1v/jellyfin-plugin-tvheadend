using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
        }
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
