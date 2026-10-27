using System.Globalization;
using System.Text.RegularExpressions;
using DiskMap.Core.Model;

namespace DiskMap.Core.Query;

/// <summary>
/// Search syntax for the title-bar box. Terms are ANDed:
/// <c>ext:gguf</c>, <c>size:&gt;20GB</c>, <c>size:&lt;500MB</c>, <c>older:1y</c>, <c>newer:30d</c>,
/// <c>cat:games</c>, anything else matches part of the name.
/// </summary>
public sealed partial class NodeQuery
{
    private readonly List<Func<DiskNode, bool>> _fileTests = new();
    private readonly List<Func<DiskNode, bool>> _nameTests = new();

    public bool IsEmpty => _fileTests.Count == 0 && _nameTests.Count == 0;

    /// <summary>True when the query only matches names, so folders can match themselves.</summary>
    public bool NamesOnly => _fileTests.Count == 0 && _nameTests.Count > 0;

    public static NodeQuery Parse(string? text, Category? category = null)
    {
        var q = new NodeQuery();
        foreach (var raw in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = raw.ToLowerInvariant();
            if (t.StartsWith("ext:") && t.Length > 4)
            {
                var ext = "." + t[4..].TrimStart('.');
                q._fileTests.Add(n => !n.IsDirectory && n.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            }
            else if (SizeTerm().Match(t) is { Success: true } m)
            {
                double value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                long limit = (long)(value * m.Groups[3].Value switch
                {
                    "kb" => 1L << 10, "mb" => 1L << 20, "tb" => 1L << 40, _ => 1L << 30,
                });
                bool greater = m.Groups[1].Value == ">";
                q._fileTests.Add(n => !n.IsDirectory && (greater ? n.Size > limit : n.Size < limit));
            }
            else if (AgeTerm().Match(t) is { Success: true } a)
            {
                int amount = int.Parse(a.Groups[2].Value, CultureInfo.InvariantCulture);
                var cutoff = a.Groups[3].Value switch
                {
                    "d" => DateTime.Now.AddDays(-amount),
                    "w" => DateTime.Now.AddDays(-7 * amount),
                    "m" => DateTime.Now.AddMonths(-amount),
                    _ => DateTime.Now.AddYears(-amount),
                };
                bool older = a.Groups[1].Value == "older";
                q._fileTests.Add(n => !n.IsDirectory && n.Modified != default && (older ? n.Modified < cutoff : n.Modified > cutoff));
            }
            else if (t.StartsWith("cat:") && t.Length > 4)
            {
                var wanted = t[4..];
                var match = Categories.All.FirstOrDefault(c =>
                    Categories.Name(c).StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ||
                    c.ToString().StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
                q._fileTests.Add(n => !n.IsDirectory && n.Category == match);
            }
            else
            {
                var term = raw;
                q._nameTests.Add(n => n.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                      n.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
            }
        }
        if (category is { } cat) q._fileTests.Add(n => !n.IsDirectory && n.Category == cat);
        return q;
    }

    /// <summary>
    /// Marks every node under <paramref name="root"/> that matches or contains a match.
    /// Returns the matching items (files, or folders matched by name) and their total size.
    /// </summary>
    public HashSet<DiskNode> Evaluate(DiskNode root, out List<DiskNode> matches, out long matchedBytes)
    {
        var hits = new HashSet<DiskNode>(ReferenceEqualityComparer.Instance);
        var found = new List<DiskNode>();
        long bytes = 0;

        bool Visit(DiskNode n)
        {
            bool hit = false;
            if (n.Children != null)
            {
                foreach (var c in n.Children)
                {
                    if (Visit(c)) hit = true;
                }
                if (!hit && NamesOnly && !ReferenceEquals(n, root) && _nameTests.All(t => t(n)))
                {
                    hit = true;
                    found.Add(n);
                    bytes += n.Size;
                }
            }
            else if (_fileTests.All(t => t(n)) && _nameTests.All(t => t(n)))
            {
                hit = true;
                found.Add(n);
                bytes += n.Size;
            }
            if (hit) hits.Add(n);
            return hit;
        }

        Visit(root);
        matches = found;
        matchedBytes = bytes;
        return hits;
    }

    [GeneratedRegex(@"^size:([<>])(\d+(?:\.\d+)?)(kb|mb|gb|tb)?$")]
    private static partial Regex SizeTerm();

    [GeneratedRegex(@"^(older|newer):(\d+)([dwmy])$")]
    private static partial Regex AgeTerm();
}
