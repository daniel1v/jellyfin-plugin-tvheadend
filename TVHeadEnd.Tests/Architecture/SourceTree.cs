using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TVHeadEnd.Tests.Architecture;

/// <summary>
/// The repository's own source, read as text so that rules about it can be asserted.
/// </summary>
/// <remarks>
/// <para>
/// Most architecture rules here are checked against compiled metadata instead, because metadata
/// cannot be wrong about what an assembly actually depends on. Two of them cannot be: reaching a
/// singleton through a static property and writing a host's codec name into a literal both compile
/// to something indistinguishable from legitimate code. Those are the ones this exists for.
/// </para>
/// <para>
/// Comments and the insides of literals are separated out before any rule looks at the code, so a
/// remark <em>about</em> a forbidden dependency never counts as one. That distinction is the whole
/// reason this is not a grep.
/// </para>
/// </remarks>
internal static class SourceTree
{
    /// <summary>
    /// One source file, split into the parts a rule may ask different questions of.
    /// </summary>
    /// <param name="Path">The path relative to the repository root, for the failure message.</param>
    /// <param name="Namespace">The namespace the file declares, or an empty string.</param>
    /// <param name="Code">The file with every comment and every literal's contents removed.</param>
    /// <param name="Literals">The contents of the file's string literals.</param>
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
    /// Splits one source file into code, declared namespace and literals.
    /// </summary>
    /// <param name="path">The path to report it under.</param>
    /// <param name="source">The file's text.</param>
    /// <returns>The split file.</returns>
    internal static File Read(string path, string source)
    {
        var code = new StringBuilder(source.Length);
        var literals = new List<string>();
        var literal = new StringBuilder();

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                code.Append('\n');
                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                code.Append(' ');
                continue;
            }

            if (c == '@' && next == '"')
            {
                i += 2;
                literal.Clear();
                while (i < source.Length)
                {
                    if (source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        literal.Append('"');
                        i += 2;
                        continue;
                    }

                    if (source[i] == '"')
                    {
                        break;
                    }

                    literal.Append(source[i++]);
                }

                literals.Add(literal.ToString());
                code.Append("\"\"");
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;

                // An interpolated string holds code as well as text, so its contents stay in both
                // halves. Otherwise a dependency written inside a "{...}" hole would be filed as
                // text and no rule about code would ever see it.
                var interpolated = code.Length > 0 && code[^1] == '$';
                i++;
                literal.Clear();
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        literal.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }

                    literal.Append(source[i++]);
                }

                if (quote == '"')
                {
                    literals.Add(literal.ToString());
                }

                code.Append(quote);
                if (interpolated)
                {
                    code.Append(literal);
                }

                code.Append(quote);
                continue;
            }

            code.Append(c);
        }

        return new File(path, NamespaceOf(code.ToString()), code.ToString(), literals);
    }

    private static string NamespaceOf(string code)
    {
        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("namespace ", StringComparison.Ordinal))
            {
                return trimmed["namespace ".Length..].TrimEnd(';', ' ', '{');
            }
        }

        return string.Empty;
    }
}
