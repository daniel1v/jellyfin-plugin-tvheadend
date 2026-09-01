using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TVHeadEnd.Compatibility.Jellyfin12;
using TVHeadEnd.Core.Media;
using Xunit;

namespace TVHeadEnd.Tests.Architecture;

/// <summary>
/// The lines the refactoring drew, asserted rather than agreed.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here was a real defect before it was a rule: a core that quietly grew a Jellyfin
/// reference, a TVHeadend adapter that decided what Jellyfin should be told, a class that reached
/// past its own dependencies for a static singleton, a host's codec spelling written into the
/// parser that produces it. None of them announced itself; each was found by reading.
/// </para>
/// <para>
/// These forbid dependencies, not arrangements. Nothing here pins a file name, a folder or a list
/// of classes, so moving a type is not a failing test -- only making it depend on something it is
/// not allowed to know about is.
/// </para>
/// </remarks>
public class ArchitectureBoundaryTests
{
    /// <summary>
    /// What the core is not allowed to be built on, by assembly name.
    /// </summary>
    private static readonly string[] OutsideTheCore =
    [
        "TVHeadEnd",
        "Tvheadend",
        "Jellyfin",
        "MediaBrowser",
        "Microsoft.AspNetCore",
        "SkiaSharp",
    ];

    /// <summary>
    /// What the TVHeadend adapter is not allowed to know about, by namespace.
    /// </summary>
    /// <remarks>
    /// It speaks HTSP, TVHeadend's HTTP and this plugin's own vocabulary. Which of that a host
    /// wants, and under what names, is a question asked on the other side of the plugin.
    /// </remarks>
    private static readonly string[] OutsideTheTvheadendAdapter =
    [
        "MediaBrowser.",
        "Jellyfin.",
        "TVHeadEnd.LiveTv",
        "TVHeadEnd.Recordings",
        "TVHeadEnd.Playback",
        "TVHeadEnd.Compatibility",
    ];

    /// <summary>
    /// Names that belong to a host's codec vocabulary rather than to a broadcast.
    /// </summary>
    /// <remarks>
    /// These are FFmpeg's spellings, which Jellyfin inherited. A transport stream does not carry
    /// them: it carries a stream type byte, and turning that byte into one of these is a decision
    /// about the host, made once, in <see cref="JellyfinCodecNames"/>.
    /// </remarks>
    private static readonly string[] HostCodecNames =
    [
        "mpeg2video",
        "mpeg4",
        "h264",
        "hevc",
        "mp2",
        "aac_latm",
        "dvb_subtitle",
        "dvb_teletext",
        "mpegts",
    ];

    [Fact]
    public void TheCoreIsBuiltOnNothingButTheBaseClassLibrary()
    {
        // The strongest form of the rule, because it reads what the compiler produced rather than
        // what the source appears to say. A core with no way to name a Jellyfin type cannot start
        // deciding things on Jellyfin's behalf by accident.
        var reached = typeof(ElementaryStreamCodec).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => OutsideTheCore.Any(forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(reached);
    }

    [Fact]
    public void TheCoreProjectDeclaresNothingItCouldReference()
    {
        // The rule one step earlier: not only does the core reference nothing, it has nothing to
        // reference. Analyzers are the exception and they are build-time only, so nothing they
        // bring can reach the output.
        var project = XDocument.Parse(SourceTree.ReadFile("TVHeadEnd.Core/TVHeadEnd.Core.csproj"));

        Assert.Empty(project.Descendants("ProjectReference"));

        foreach (var package in project.Descendants("PackageReference"))
        {
            var name = package.Attribute("Include")?.Value ?? "an unnamed package";
            var privateAssets = package.Attribute("PrivateAssets")?.Value
                ?? package.Element("PrivateAssets")?.Value;

            Assert.True(
                string.Equals(privateAssets, "All", StringComparison.OrdinalIgnoreCase),
                $"TVHeadEnd.Core references {name} at runtime; only build-time analyzers belong here.");
        }
    }

    [Fact]
    public void TheTvheadendAdapterDoesNotKnowWhatTheOtherSideWants()
    {
        var offences = new List<string>();

        foreach (var file in SourceTree.SourcesOf("TVHeadEnd")
            .Where(file => file.Namespace.StartsWith("TVHeadEnd.Tvheadend", StringComparison.Ordinal)))
        {
            offences.AddRange(OutsideTheTvheadendAdapter
                .Where(forbidden => file.Code.Contains(forbidden, StringComparison.Ordinal))
                .Select(forbidden => file.Path + " reaches " + forbidden));
        }

        Assert.Empty(offences);
    }

    [Fact]
    public void TheTvheadendAdapterDoesNotReadThePluginsConfiguration()
    {
        // It is handed what it needs. Reading the configuration object itself would make every
        // class under it a client of the host's settings shape as well as of TVHeadend's protocol.
        var offences = SourceTree.SourcesOf("TVHeadEnd")
            .Where(file => file.Namespace.StartsWith("TVHeadEnd.Tvheadend", StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(file.Code, @"\bPluginConfiguration\b"))
            .Select(file => file.Path)
            .ToList();

        Assert.Empty(offences);
    }

    [Fact]
    public void OnlyTheConfigurationBridgeReachesThePluginSingleton()
    {
        // A static singleton is reachable from everywhere, which is exactly the problem: a class
        // that takes one has real dependencies nothing can see and no test can stand it up on its
        // own. One bridge holds it; everything else is given what it needs.
        var offences = SourceTree.SourcesOf("TVHeadEnd")
            .Where(file => !file.Namespace.StartsWith("TVHeadEnd.Configuration", StringComparison.Ordinal))
            .Where(file => !Regex.IsMatch(file.Code, @"\bclass Plugin\b"))
            .Where(file => Regex.IsMatch(file.Code, @"\bPlugin\s*\.\s*Instance\b"))
            .Select(file => file.Path)
            .ToList();

        Assert.Empty(offences);
    }

    [Fact]
    public void TheCoreDoesNotSpeakTheHostsCodecVocabulary()
    {
        // Comments may name them -- explaining why a stream type maps the way it does is exactly
        // what a comment is for. Only a literal counts, because only a literal can escape.
        //
        // Case-sensitively, and that is the point rather than an oversight. The core does carry
        // "HEVC", "AC-3" and "DTS1": those are the four-character format identifiers a registration
        // descriptor puts in the stream itself, so they are broadcast facts read off the wire. A
        // host's name for the same thing is the lower-case one, and it is a different string.
        var offences = new List<string>();

        foreach (var file in SourceTree.SourcesOf("TVHeadEnd.Core"))
        {
            offences.AddRange(
                from literal in file.Literals
                from name in HostCodecNames
                where Regex.IsMatch(literal, @"\b" + Regex.Escape(name) + @"\b")
                select file.Path + " writes \"" + literal + "\"");
        }

        Assert.Empty(offences);
    }

    [Fact]
    public void TheHostsCodecVocabularyHasOneOwner()
    {
        // Where they are allowed, they are allowed once. A second copy is a second answer waiting
        // to disagree with the first, and a client comparing strings cannot tell which was meant.
        var owners = SourceTree.SourcesOf("TVHeadEnd")
            .Where(file => file.Literals.Any(literal => HostCodecNames.Any(
                name => string.Equals(literal, name, StringComparison.Ordinal))))
            .Select(file => file.Namespace)
            .Distinct()
            .ToList();

        Assert.All(owners, owner => Assert.Equal("TVHeadEnd.Compatibility.Jellyfin12", owner));
    }

    [Fact]
    public void TheTwoJellyfinEntryPointsDoNotHoldEachOther()
    {
        // Recordings once had to go through the live TV service to be listed, which made one
        // adapter a dependency of the other for no reason either of them expressed. They answer
        // different Jellyfin interfaces and share only what sits behind them.
        AssertDoesNotHold(typeof(RecordingsChannel), typeof(LiveTvService));
        AssertDoesNotHold(typeof(LiveTvService), typeof(RecordingsChannel));
    }

    [Fact]
    public void TheCoreTestsTestTheCore()
    {
        // The folder said core and the contents said otherwise, which is how wire mapping and
        // host mapping came to be verified as though they were this plugin's own reasoning.
        var forbidden = OutsideTheTvheadendAdapter.Append("Tvheadend.Htsp").ToList();
        var offences = new List<string>();

        foreach (var file in SourceTree.SourcesOf("TVHeadEnd.Tests")
            .Where(file => string.Equals(file.Namespace, "TVHeadEnd.Tests.Core", StringComparison.Ordinal)))
        {
            offences.AddRange(forbidden
                .Where(name => file.Code.Contains(name, StringComparison.Ordinal))
                .Select(name => file.Path + " reaches " + name));
        }

        Assert.Empty(offences);
    }

    private static void AssertDoesNotHold(Type adapter, Type other)
    {
        var taken = adapter.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(adapter
                .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(field => field.FieldType))
            .ToList();

        Assert.DoesNotContain(other, taken);
    }
}
