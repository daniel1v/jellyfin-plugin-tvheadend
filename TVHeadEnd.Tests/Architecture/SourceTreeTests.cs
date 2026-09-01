using System;
using Xunit;

namespace TVHeadEnd.Tests.Architecture;

/// <summary>
/// That the reader the source rules stand on tells code, comment and text apart.
/// </summary>
/// <remarks>
/// A rule that scans nothing passes, and a rule that misreads one way of writing a string is a
/// rule that can be walked around by writing it that way. These are here so that the two rules
/// which cannot be checked against compiled metadata -- reaching the plugin singleton, and writing
/// a host's codec name -- fail when they should rather than when the reader happens to be looking.
/// </remarks>
public class SourceTreeTests
{
    [Fact]
    public void ADependencyNamedOnlyInACommentIsNotADependency()
    {
        var file = SourceTree.Read("Example.cs", "// Not Plugin.Instance, which is the point.\nvar x = 1;");

        Assert.DoesNotContain("Plugin.Instance", file.Code, StringComparison.Ordinal);
        Assert.Contains("var x = 1;", file.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockCommentIsNotCode()
    {
        var file = SourceTree.Read("Example.cs", "/* Plugin.Instance */ var x = 1;");

        Assert.DoesNotContain("Plugin.Instance", file.Code, StringComparison.Ordinal);
        Assert.Contains("var x = 1;", file.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentationCommentIsNotCode()
    {
        var file = SourceTree.Read("Example.cs", "/// <see cref=\"Plugin.Instance\"/>\nvar x = 1;");

        Assert.DoesNotContain("Plugin.Instance", file.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void ADependencyInTheCodeIsFound()
    {
        var file = SourceTree.Read("Example.cs", "var c = Plugin.Instance!.Configuration;");

        Assert.Contains("Plugin.Instance", file.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void NeighbouringTokensStayNeighbours()
    {
        // The reader must not introduce whitespace of its own: a rule looking for a namespace
        // prefix would stop matching the moment it did.
        var file = SourceTree.Read("Example.cs", "using MediaBrowser.Controller.Library;");

        Assert.Contains("MediaBrowser.Controller.Library", file.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every way C# lets a string be written, each saying the same thing.
    /// </summary>
    /// <param name="declaration">The declaration, whose text is always <c>mpeg2video</c>.</param>
    [Theory]
    [InlineData("var s = \"mpeg2video\";")]
    [InlineData("var s = \"mpeg\\u0032video\";")]
    [InlineData("var s = @\"mpeg2video\";")]
    [InlineData("var s = $\"mpeg2video\";")]
    [InlineData("var s = $@\"mpeg2video\";")]
    [InlineData("var s = @$\"mpeg2video\";")]
    [InlineData("var s = \"\"\"mpeg2video\"\"\";")]
    [InlineData("var s = $\"\"\"mpeg2video\"\"\";")]
    [InlineData("var s = \"mpeg2video\"u8;")]
    public void TextIsReportedAsTextHoweverItIsWritten(string declaration)
    {
        var file = SourceTree.Read("Example.cs", declaration);

        Assert.Contains("mpeg2video", file.Literals);
        Assert.DoesNotContain("mpeg2video", file.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEscapedQuoteDoesNotEndTheText()
    {
        var file = SourceTree.Read("Example.cs", "var s = \"a\\\"mpeg2video\"; var y = 2;");

        Assert.Contains("a\"mpeg2video", file.Literals);
        Assert.Contains("var y = 2;", file.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void ADoubledQuoteDoesNotEndAVerbatimString()
    {
        var file = SourceTree.Read("Example.cs", "var s = @\"a\"\"mpeg2video\"; var y = 2;");

        Assert.Contains("a\"mpeg2video", file.Literals);
        Assert.Contains("var y = 2;", file.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every way C# lets a string interpolate something, each interpolating the same thing.
    /// </summary>
    /// <remarks>
    /// This is the form the hand-written reader got wrong, and getting it wrong is what would let
    /// a forbidden dependency be written past a rule rather than around it.
    /// </remarks>
    /// <param name="declaration">The declaration, which always interpolates the singleton.</param>
    [Theory]
    [InlineData("var s = $\"{Plugin.Instance}\";")]
    [InlineData("var s = $@\"{Plugin.Instance}\";")]
    [InlineData("var s = @$\"{Plugin.Instance}\";")]
    [InlineData("var s = $\"\"\"{Plugin.Instance}\"\"\";")]
    [InlineData("var s = $\"held {Plugin.Instance} here\";")]
    [InlineData("var s = $$\"\"\"{{Plugin.Instance}}\"\"\";")]
    public void WhatAnInterpolatedStringHolesIsStillCode(string declaration)
    {
        var file = SourceTree.Read("Example.cs", declaration);

        Assert.Contains("Plugin.Instance", file.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTextAroundAHoleIsStillText()
    {
        var file = SourceTree.Read("Example.cs", "var s = $\"the codec is mpeg2video, {x} of it\";");

        Assert.Contains("mpeg2video", string.Join("|", file.Literals), StringComparison.Ordinal);
        Assert.DoesNotContain("mpeg2video", file.Code, StringComparison.Ordinal);
        Assert.Contains("x", file.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void ACharacterLiteralIsNeitherTextNorAnIdentifier()
    {
        var file = SourceTree.Read("Example.cs", "var c = 'x'; var y = 2;");

        Assert.Contains("var y = 2;", file.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("'x'", file.Code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("namespace TVHeadEnd.Tvheadend.Catalogs;\n")]
    [InlineData("namespace TVHeadEnd.Tvheadend.Catalogs\n{\n}\n")]
    public void TheDeclaredNamespaceIsRead(string declaration)
    {
        var file = SourceTree.Read("Example.cs", "using System;\n\n" + declaration);

        Assert.Equal("TVHeadEnd.Tvheadend.Catalogs", file.Namespace);
    }

    [Fact]
    public void AFileWithoutANamespaceHasNone()
    {
        Assert.Equal(string.Empty, SourceTree.Read("Example.cs", "var x = 1;").Namespace);
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
            file => string.Equals(file.Namespace, "TVHeadEnd.Tests.Core", StringComparison.Ordinal));
        Assert.Contains(
            SourceTree.SourcesOf("TVHeadEnd"),
            file => file.Namespace.StartsWith("TVHeadEnd.Tvheadend", StringComparison.Ordinal));
    }
}
