using Xunit;

namespace TVHeadEnd.Tests.Architecture;

/// <summary>
/// That the reader the source rules stand on tells code, comment and text apart.
/// </summary>
/// <remarks>
/// A rule that scans nothing passes. These are here so that the two rules which cannot be checked
/// against compiled metadata -- reaching the plugin singleton, and writing a host's codec name --
/// fail when they should rather than when the reader happens to be looking.
/// </remarks>
public class SourceTreeTests
{
    [Fact]
    public void ADependencyNamedOnlyInACommentIsNotADependency()
    {
        var file = SourceTree.Read("Example.cs", "// Not Plugin.Instance, which is the point.\nvar x = 1;");

        Assert.DoesNotContain("Plugin.Instance", file.Code, System.StringComparison.Ordinal);
        Assert.Contains("var x = 1;", file.Code, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ADependencyInTheCodeIsFound()
    {
        var file = SourceTree.Read("Example.cs", "var c = Plugin.Instance!.Configuration;");

        Assert.Contains("Plugin.Instance", file.Code, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockCommentIsNotCode()
    {
        var file = SourceTree.Read("Example.cs", "/* Plugin.Instance */ var x = 1;");

        Assert.DoesNotContain("Plugin.Instance", file.Code, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TextIsReportedAsTextRatherThanAsCode()
    {
        var file = SourceTree.Read("Example.cs", "var name = \"mpeg2video\";");

        Assert.Contains("mpeg2video", file.Literals);
        Assert.DoesNotContain("mpeg2video", file.Code, System.StringComparison.Ordinal);
    }

    [Fact]
    public void WhatAnInterpolatedStringHolesIsStillCode()
    {
        var file = SourceTree.Read("Example.cs", "var s = $\"{Plugin.Instance}\";");

        Assert.Contains("Plugin.Instance", file.Code, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AnEscapedQuoteDoesNotEndTheText()
    {
        var file = SourceTree.Read("Example.cs", "var s = \"a\\\"b\"; var y = 2;");

        Assert.Contains("var y = 2;", file.Code, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeclaredNamespaceIsRead()
    {
        var file = SourceTree.Read("Example.cs", "using System;\n\nnamespace TVHeadEnd.Tvheadend.Catalogs;\n");

        Assert.Equal("TVHeadEnd.Tvheadend.Catalogs", file.Namespace);
    }

    [Fact]
    public void EveryProjectTheRulesScanActuallyHasSources()
    {
        // The failure mode a wrong path would cause: a rule that reads an empty list and reports
        // no offences for as long as nobody notices.
        Assert.NotEmpty(SourceTree.SourcesOf("TVHeadEnd"));
        Assert.NotEmpty(SourceTree.SourcesOf("TVHeadEnd.Core"));
        Assert.Contains(
            SourceTree.SourcesOf("TVHeadEnd.Tests"),
            file => string.Equals(file.Namespace, "TVHeadEnd.Tests.Core", System.StringComparison.Ordinal));
        Assert.Contains(
            SourceTree.SourcesOf("TVHeadEnd"),
            file => file.Namespace.StartsWith("TVHeadEnd.Tvheadend", System.StringComparison.Ordinal));
    }
}
