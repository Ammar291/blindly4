using System.Globalization;
using System.Text;

namespace Blindly;

// Renders element details as the XML UI tree: element name = role, one element per line.
internal static class Xml
{
    internal static string Format(Invocation i)
    {
        var format = i.Optional("format") ?? "json";
        if (format is not ("json" or "xml")) throw new CliError("--format must be json or xml");
        return format;
    }
    // Control characters (including newlines) become spaces so every element stays on one line.
    internal static string Escape(string text) => new StringBuilder(text.Length).AppendJoin("", text.Select(c => c switch {
        '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;",
        _ when char.IsControl(c) => " ",
        _ => c.ToString()
    })).ToString();
    static string? Text(object? value) => value switch {
        null => null, string s => s, bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
    static double Number(object box, string name) => Convert.ToDouble(box.GetType().GetProperty(name)?.GetValue(box) ?? 0.0, CultureInfo.InvariantCulture);
    static string Name(object? role) => role is string r && r.Length > 0 && char.IsAsciiLetter(r[0]) && r.All(char.IsAsciiLetterOrDigit) ? r : "AXGroup";
    static string Attributes(IReadOnlyDictionary<string, object?> row)
    {
        var parts = new List<(string Name, string Value)>();
        void Add(string name, string? value) { if (!string.IsNullOrEmpty(value)) parts.Add((name, value)); }
        var title = Text(row.GetValueOrDefault("title"));
        var description = Text(row.GetValueOrDefault("description"));
        Add("path", Text(row.GetValueOrDefault("path")));
        Add("title", title);
        Add("value", Text(row.GetValueOrDefault("value")));
        Add("id", Text(row.GetValueOrDefault("identifier")));
        if (description != title) Add("description", description);
        if (row.GetValueOrDefault("actions") is string[] actions) Add("actions", string.Join(' ', actions));
        if (row.GetValueOrDefault("position") is { } p && row.GetValueOrDefault("size") is { } s)
            Add("frame", string.Join(',', new[] { Number(p, "x"), Number(p, "y"), Number(s, "width"), Number(s, "height") }
                .Select(n => Math.Round(n).ToString(CultureInfo.InvariantCulture))));
        if (row.GetValueOrDefault("enabled") is false) Add("enabled", "false");
        if (row.GetValueOrDefault("focused") is true) Add("focused", "true");
        if (row.GetValueOrDefault("selected") is true) Add("selected", "true");
        return string.Concat(parts.Select(a => $" {a.Name}=\"{Escape(a.Value)}\""));
    }
    internal static string Element(IReadOnlyDictionary<string, object?> row) => $"<{Name(row.GetValueOrDefault("role"))}{Attributes(row)} />";
    internal static void Matches(IEnumerable<Dictionary<string, object?>> matches, bool truncated)
    {
        Console.WriteLine($"<matches truncated=\"{(truncated ? "true" : "false")}\">");
        foreach (var row in matches) Console.WriteLine("  " + Element(row));
        Console.WriteLine("</matches>");
    }
    internal static void Tree(Dictionary<string, object?> tree, bool truncated)
    {
        Console.WriteLine($"<tree truncated=\"{(truncated ? "true" : "false")}\">");
        Write(tree, 1);
        Console.WriteLine("</tree>");
        static void Write(Dictionary<string, object?> row, int depth)
        {
            var indent = new string(' ', depth * 2);
            if (row.GetValueOrDefault("children") is List<object> { Count: > 0 } children)
            {
                Console.WriteLine(indent + Element(row)[..^3] + ">");
                foreach (var child in children.Cast<Dictionary<string, object?>>()) Write(child, depth + 1);
                Console.WriteLine($"{indent}</{Name(row.GetValueOrDefault("role"))}>");
            }
            else Console.WriteLine(indent + Element(row));
        }
    }
}
