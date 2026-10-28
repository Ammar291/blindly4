using System.Windows.Automation;

namespace Blindly;

internal sealed class Desktop
{
    readonly Node root;
    internal Desktop(int pid)
    {
        if (pid <= 0 || !Native.Windows().Any(w => w.Pid == pid)) throw CliError.Access($"No visible windows for PID {pid}");
        root = new Node(null, pid);
    }
    internal Node Resolve(string path)
    {
        var node = root;
        foreach (int index in Guards.Path(path))
            node = node.Children().Skip(index).FirstOrDefault() ?? throw CliError.Access($"Path {path} no longer exists; rediscover the target");
        return node;
    }
    internal IEnumerable<(Node Node, string Path)> Walk(int depth, string under = "")
    {
        return Visit(Resolve(under), under, depth);
        static IEnumerable<(Node, string)> Visit(Node n, string path, int remaining)
        {
            yield return (n, path);
            if (remaining == 0) yield break;
            int index = 0;
            foreach (var child in n.Children())
            {
                string next = path.Length == 0 ? $"{index}" : $"{path}.{index}";
                foreach (var row in Visit(child, next, remaining - 1)) yield return row;
                index++;
            }
        }
    }
    internal static void Tree(Invocation i)
    {
        var desktop = new Desktop(i.Pid);
        int remaining = i.Integer("max-nodes", 250, 1, 10000);
        int depth = i.Integer("depth", 4, 0, 64);
        bool xml = Xml.Format(i) == "xml", truncated = false;
        Dictionary<string, object?> Build(Node n, int d, string path)
        {
            remaining--;
            var row = n.Detail(xml ? path : null);
            var children = new List<object>();
            int index = 0;
            foreach (var child in n.Children())
            {
                if (remaining == 0 || d == 0) { truncated = true; break; }
                children.Add(Build(child, d - 1, path.Length == 0 ? $"{index}" : $"{path}.{index}"));
                index++;
            }
            if (children.Count > 0) row["children"] = children;
            return row;
        }
        var tree = Build(desktop.root, depth, "");
        if (xml) Xml.Tree(tree, truncated); else Program.Json(new { tree, truncated });
    }
    internal static void Show(Invocation i)
    {
        int count = 0;
        foreach (var (node, path) in new Desktop(i.Pid).Walk(i.Integer("depth", 4, 0, 64)))
        {
            if (++count > 10000) { Console.WriteLine("[truncated: 10000 nodes]"); break; }
            var row = node.Detail();
            Console.WriteLine($"{path,-18} {row["role"]} {row.GetValueOrDefault("title")} {row.GetValueOrDefault("value")}");
        }
    }
    // Cheapest UI Automation reads first. Each read is a cross-process call, so a node is
    // rejected on its first failing filter and the full Detail is built only for matches.
    internal static readonly (string Key, Func<Node, string?> Read)[] Filters = [
        ("identifier", n => n.Identifier), ("role", n => n.Role), ("title", n => n.Title),
        ("description", n => n.Description), ("value", n => n.Value)
    ];
    internal static bool Matches(string key, string? actual, string wanted) => key == "identifier"
        ? string.Equals(actual ?? "", wanted, StringComparison.Ordinal)
        : (actual ?? "").Contains(wanted, StringComparison.OrdinalIgnoreCase);
    internal static void Find(Invocation i)
    {
        var active = Filters.Where(f => i.Has(f.Key)).ToArray();
        if (active.Length == 0) throw new CliError("find requires --title, --role, --value, --description or --identifier");
        int limit = i.Integer("limit", 25, 1, 10000), visited = 0;
        var under = i.Optional("under") ?? "";
        Guards.Path(under);
        bool xml = Xml.Format(i) == "xml";
        var matches = new List<Dictionary<string, object?>>();
        bool truncated = false;
        foreach (var (node, path) in new Desktop(i.Pid).Walk(i.Integer("depth", 8, 0, 64), under))
        {
            if (++visited > 10000) { truncated = true; break; }
            if (active.All(f => Matches(f.Key, f.Read(node), i.Value(f.Key)))) matches.Add(node.Detail(path));
            if (matches.Count >= limit) { truncated = true; break; }
        }
        if (xml) Xml.Matches(matches, truncated); else Program.Json(new { matches, truncated });
    }
    internal static void Focused(Invocation i)
    {
        var focused = AutomationElement.FocusedElement;
        if (focused == null || (i.Has("pid") && Native.ForegroundPid != i.Pid))
            throw new CliError("No focused control in the requested application", 77, "focus_unavailable");
        Program.Json(new Node(focused, i.Pid).Detail());
    }
    internal static void Mutate(Invocation i, string action, Action<Node> mutate)
    {
        var path = i.Value("path");
        var target = new Desktop(i.Pid).Resolve(path);
        Expect(i, target);
        mutate(target);
        Program.Json(new { path, action, ok = true });
    }
    static void Expect(Invocation i, Node target, string? path = null)
    {
        if (i.Has("expect-description") && !(target.Detail()["description"]?.ToString() ?? "").Contains(i.Value("expect-description"), StringComparison.OrdinalIgnoreCase))
            throw new CliError($"{path ?? i.Value("path")} does not match --expect-description");
    }
    // Instead of --path, --under PATH with --title/--role/--identifier presses the first match below PATH
    // as it is when the press runs, so a press scheduled with --at survives the page renumbering children.
    static (Node Node, string Path) Target(Invocation i, Desktop desktop)
    {
        if (i.Has("path")) return (desktop.Resolve(i.Value("path")), i.Value("path"));
        var active = Filters.Where(f => i.Has(f.Key)).ToArray();
        if (!i.Has("under") || active.Length == 0) throw new CliError("press needs --path, or --under with --title, --role or --identifier");
        var under = i.Value("under");
        Guards.Path(under);
        foreach (var (node, path) in desktop.Walk(i.Integer("depth", 8, 0, 64), under))
            if (active.All(f => Matches(f.Key, f.Read(node), i.Value(f.Key)))) return (node, path);
        throw CliError.Access($"No element below {under} matches");
    }
    internal static void Press(Invocation i)
    {
        i.Pair("require-value-path", "require-value");
        var desktop = new Desktop(i.Pid);
        var (target, path) = Target(i, desktop);
        Expect(i, target, path);
        if (i.Has("require-value-path") && !desktop.Resolve(i.Value("require-value-path")).ExactDraft(i.Value("require-value")))
            throw CliError.Access("Press blocked: the draft does not exactly match --require-value");
        target.Press();
        if (i.Has("require-selected"))
        {
            for (int attempt = 0; attempt < 20 && !target.Selected; attempt++) Thread.Sleep(50);
            if (!target.Selected) throw CliError.Access("Press completed but target was not selected; do not continue");
        }
        var result = new Dictionary<string, object?> { ["path"] = path, ["action"] = "AXPress", ["ok"] = true };
        if (i.Has("require-selected")) result["selected"] = true;
        if (i.Has("require-value-path")) result["verified"] = true;
        Program.Json(result);
    }
}
