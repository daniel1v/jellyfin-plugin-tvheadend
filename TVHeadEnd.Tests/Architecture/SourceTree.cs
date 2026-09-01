using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TVHeadEnd.Tests.Architecture;

/// <summary>
/// The repository's own source, read as the compiler reads it.
/// </summary>
/// <remarks>
/// <para>
/// Most architecture rules here are checked against compiled metadata instead, because metadata
/// cannot be wrong about what an assembly actually depends on. Two of them cannot be: reaching a
/// singleton through a static property and writing a host's codec name into a literal both compile
/// to something indistinguishable from legitimate code. Those are the ones this exists for.
/// </para>
/// <para>
/// Comments and the text of literals are separated from code before any rule looks at it, so a
/// remark <em>about</em> a forbidden dependency never counts as one -- while what an interpolated
/// string holds in its holes stays code, because it is. That distinction is the whole reason this
/// is not a grep.
/// </para>
/// <para>
/// It is Roslyn's parser and not a lexer of this repository's own. The one written by hand handled
/// the string forms it had been shown and mis-read the rest, which turns a rule into something a
/// violation can be written around: <c>$@"{Plugin.Instance}"</c> was not code, and a raw
/// interpolated string was not anything. C# has eight ways to write a string and will have more.
/// </para>
/// </remarks>
internal static class SourceTree
{
    /// <summary>
    /// One source file, split into the parts a rule may ask different questions of.
    /// </summary>
    /// <param name="Path">The path relative to the repository root, for the failure message.</param>
    /// <param name="Namespace">The namespace the file declares, or an empty string.</param>
    /// <param name="Code">
    /// The file as code: comments gone, the text of every literal gone, and everything an
    /// interpolated string interpolates still present.
    /// </param>
    /// <param name="Literals">The text of the file's string literals, however they were written.</param>
    internal sealed record File(string Path, string Namespace, string Code, IReadOnlyList<string> Literals);

    /// <summary>
    /// Gets the repository root, found by the one file that is only ever at the top of it.
    /// </summary>
    internal static DirectoryInfo Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "build.yaml")))
            {
                directory = directory.Parent;
            }

            return directory ?? throw new InvalidOperationException("No repository root above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>
    /// Reads one file of the repository.
    /// </summary>
    /// <param name="relativePath">Its path from the repository root.</param>
    /// <returns>Its contents.</returns>
    internal static string ReadFile(string relativePath)
        => System.IO.File.ReadAllText(Path.Combine(Root.FullName, relativePath), Encoding.UTF8);

    /// <summary>
    /// Reads every C# source file of one project.
    /// </summary>
    /// <param name="projectDirectory">The project directory, relative to the repository root.</param>
    /// <returns>The files, with build output left out.</returns>
    internal static IReadOnlyList<File> SourcesOf(string projectDirectory)
    {
        var root = Root.FullName;
        var project = Path.Combine(root, projectDirectory);

        return [.. Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Read(Path.GetRelativePath(root, path), System.IO.File.ReadAllText(path, Encoding.UTF8)))];
    }

    /// <summary>
    /// Splits one source file into code, declared namespace and literal text.
    /// </summary>
    /// <param name="path">The path to report it under.</param>
    /// <param name="source">The file's text.</param>
    /// <returns>The split file.</returns>
    internal static File Read(string path, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        var literals = new List<string>();
        var code = new StringBuilder(source.Length);

        foreach (var token in root.DescendantTokens())
        {
            Append(code, token.LeadingTrivia);

            switch (token.Kind())
            {
                // A string, in any of the ways C# lets one be written. What it says is text; that
                // it is there at all is code, so the quotes stay and the contents do not.
                case SyntaxKind.StringLiteralToken:
                case SyntaxKind.SingleLineRawStringLiteralToken:
                case SyntaxKind.MultiLineRawStringLiteralToken:
                case SyntaxKind.Utf8StringLiteralToken:
                case SyntaxKind.Utf8SingleLineRawStringLiteralToken:
                case SyntaxKind.Utf8MultiLineRawStringLiteralToken:
                    literals.Add(token.ValueText);
                    code.Append("\"\"");
                    break;

                // The text between the holes of an interpolated string. The holes themselves are
                // ordinary tokens and arrive here on their own, which is exactly right: what a
                // string interpolates is code that happens to be written inside quotes.
                case SyntaxKind.InterpolatedStringTextToken:
                    literals.Add(token.ValueText);
                    break;

                case SyntaxKind.CharacterLiteralToken:
                    code.Append("' '");
                    break;

                default:
                    code.Append(token.Text);
                    break;
            }

            Append(code, token.TrailingTrivia);
        }

        return new File(path, NamespaceOf(root), code.ToString(), literals);
    }

    /// <summary>
    /// Keeps the layout and drops everything that is not code.
    /// </summary>
    /// <remarks>
    /// Whitespace is kept verbatim so that neighbouring tokens stay neighbours: a rule looking for
    /// "MediaBrowser." must not be defeated by a space this reader introduced. A comment leaves one
    /// space behind, which is enough to keep the tokens either side of it apart.
    /// </remarks>
    private static void Append(StringBuilder code, SyntaxTriviaList trivia)
    {
        foreach (var piece in trivia)
        {
            if (piece.IsKind(SyntaxKind.WhitespaceTrivia) || piece.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                code.Append(piece.ToFullString());
            }
            else
            {
                code.Append(' ');
            }
        }
    }

    private static string NamespaceOf(SyntaxNode root)
        => root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()
            ?? string.Empty;
}
