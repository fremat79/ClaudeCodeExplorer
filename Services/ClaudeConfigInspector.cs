using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeExplorer.Models;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// Reads the Claude Code configuration that applies to a project folder and produces a tree of
/// <see cref="ConfigNode"/> for the inspector: the effective (merged) settings with override
/// highlighting, plus related config (MCP servers, CLAUDE.md files, .claude/ contents, source files).
/// All reads are best-effort and never throw.
/// </summary>
public static class ClaudeConfigInspector
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static string UserHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private sealed record SourceFile(ConfigSource Source, string Path, JsonObject? Root, bool Malformed);

    /// <summary>Builds the inspector tree for the given project folder.</summary>
    public static List<ConfigNode> Inspect(string folder)
    {
        var roots = new List<ConfigNode>();

        // Ordered lowest → highest precedence (higher wins).
        var sources = new List<SourceFile>
        {
            LoadSource(ConfigSource.User,    Path.Combine(UserHome, ".claude", "settings.json")),
            // User-scope local file (where global tool-permission grants are stored).
            LoadSource(ConfigSource.User,    Path.Combine(UserHome, ".claude", "settings.local.json")),
            LoadSource(ConfigSource.Project, Path.Combine(folder, ".claude", "settings.json")),
            LoadSource(ConfigSource.Local,   Path.Combine(folder, ".claude", "settings.local.json")),
            LoadSource(ConfigSource.Managed, @"C:\Program Files\ClaudeCode\managed-settings.json"),
        };

        roots.Add(BuildSettingsTree(sources));
        roots.Add(BuildMcpTree(folder));
        roots.Add(BuildClaudeMdTree(folder));
        roots.Add(BuildDotClaudeTree(folder));
        roots.Add(BuildSourcesTree(sources));
        return roots;
    }

    private static SourceFile LoadSource(ConfigSource source, string path)
    {
        try
        {
            if (!File.Exists(path)) return new SourceFile(source, path, null, false);
            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text, documentOptions: ParseOptions);
            return new SourceFile(source, path, node as JsonObject, node is not JsonObject);
        }
        catch
        {
            return new SourceFile(source, path, null, true);
        }
    }

    // --- Effective settings (merged) ---

    private static ConfigNode BuildSettingsTree(List<SourceFile> sources)
    {
        var root = new ConfigNode { Name = "Effective settings", Kind = ConfigNodeKind.Branch };
        var contribs = sources.Where(s => s.Root is not null)
                              .Select(s => (s.Source, s.Path, s.Root!))
                              .ToList();
        if (contribs.Count == 0)
        {
            root.Children.Add(Info("(no settings files apply to this folder)"));
            return root;
        }
        MergeObject(root, contribs);
        if (root.Children.Count == 0) root.Children.Add(Info("(all applicable settings files are empty)"));
        return root;
    }

    private static void MergeObject(ConfigNode parent, List<(ConfigSource src, string path, JsonObject obj)> contribs)
    {
        // Union of keys, in first-seen order across sources (low → high).
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, _, obj) in contribs)
            foreach (var kv in obj)
                if (seen.Add(kv.Key)) order.Add(kv.Key);

        foreach (var key in order)
        {
            var perSource = new List<(ConfigSource src, string path, JsonNode? node)>();
            foreach (var (src, path, obj) in contribs)
                if (obj.TryGetPropertyValue(key, out var node)) perSource.Add((src, path, node));
            parent.Children.Add(BuildNode(key, perSource));
        }
    }

    private static ConfigNode BuildNode(string name, List<(ConfigSource src, string path, JsonNode? node)> perSource)
    {
        var (topSrc, topPath, topNode) = perSource[^1]; // highest precedence wins

        if (topNode is JsonObject)
        {
            // Container: no FilePath (double-click expands it rather than opening a file).
            var n = new ConfigNode { Name = name, Kind = ConfigNodeKind.Object, EffectiveSource = topSrc };
            var objContribs = perSource.Where(p => p.node is JsonObject)
                                       .Select(p => (p.src, p.path, (JsonObject)p.node!)).ToList();
            MergeObject(n, objContribs);
            return n;
        }

        if (topNode is JsonArray)
        {
            var n = new ConfigNode { Name = name, Kind = ConfigNodeKind.Array, EffectiveSource = topSrc };
            var byText = new Dictionary<string, ConfigNode>(StringComparer.Ordinal);
            var elemOrder = new List<string>();
            foreach (var (src, path, node) in perSource)
            {
                if (node is not JsonArray arr) continue;
                foreach (var el in arr)
                {
                    var text = ValueText(el);
                    if (!byText.TryGetValue(text, out var en))
                    {
                        en = new ConfigNode
                        {
                            Name = "", DisplayValue = text, Kind = ConfigNodeKind.Value,
                            EffectiveSource = src, FilePath = path,   // element → its source file
                        };
                        byText[text] = en;
                        elemOrder.Add(text);
                    }
                    else
                    {
                        // Present in multiple tiers (union): show the highest, note the others.
                        en.Shadowed.Add(new ShadowedValue { Source = en.EffectiveSource, Value = "" });
                        en.EffectiveSource = src;
                        en.FilePath = path;
                    }
                }
            }
            foreach (var t in elemOrder) n.Children.Add(byText[t]);
            n.DisplayValue = $"[{n.Children.Count}]";
            n.IsExpanded = false; // arrays (e.g. permissions.allow) can be long — start collapsed
            return n;
        }

        // Scalar (or type mismatch across tiers): highest wins, lower differing values are shadowed.
        var leaf = new ConfigNode
        {
            Name = name, Kind = ConfigNodeKind.Value, DisplayValue = ValueText(topNode),
            EffectiveSource = topSrc, FilePath = topPath,   // leaf → the file that defines the effective value
        };
        for (int i = 0; i < perSource.Count - 1; i++)
        {
            var vt = ValueText(perSource[i].node);
            if (vt != leaf.DisplayValue)
                leaf.Shadowed.Add(new ShadowedValue { Source = perSource[i].src, Value = vt });
        }
        leaf.IsOverridden = leaf.Shadowed.Count > 0;
        return leaf;
    }

    // --- MCP servers ---

    private static ConfigNode BuildMcpTree(string folder)
    {
        var root = new ConfigNode { Name = "MCP servers", Kind = ConfigNodeKind.Branch };
        var files = new (ConfigSource src, string path)[]
        {
            (ConfigSource.User, Path.Combine(UserHome, ".claude", ".mcp.json")),
            (ConfigSource.Project, Path.Combine(folder, ".mcp.json")),
            (ConfigSource.Local, Path.Combine(folder, ".claude", ".mcp.json")),
        };
        // Higher tier overrides same-named server.
        var servers = new Dictionary<string, ConfigNode>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (src, path) in files)
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path), documentOptions: ParseOptions) is not JsonObject obj) continue;
                if (obj["mcpServers"] is not JsonObject list) continue;
                foreach (var kv in list)
                {
                    var cmd = (kv.Value as JsonObject)?["command"];
                    var node = new ConfigNode
                    {
                        Name = kv.Key, Kind = ConfigNodeKind.Value, EffectiveSource = src,
                        DisplayValue = cmd is null ? "" : ValueText(cmd), FilePath = path,
                    };
                    if (!servers.ContainsKey(kv.Key)) order.Add(kv.Key);
                    servers[kv.Key] = node; // higher tier wins
                }
            }
            catch { /* ignore malformed */ }
        }
        if (order.Count == 0) root.Children.Add(Info("(no .mcp.json servers)"));
        else foreach (var k in order) root.Children.Add(servers[k]);
        return root;
    }

    // --- CLAUDE.md files ---

    private static ConfigNode BuildClaudeMdTree(string folder)
    {
        var root = new ConfigNode { Name = "CLAUDE.md", Kind = ConfigNodeKind.Branch };
        var files = new (ConfigSource src, string path)[]
        {
            (ConfigSource.Managed, @"C:\Program Files\ClaudeCode\CLAUDE.md"),
            (ConfigSource.User, Path.Combine(UserHome, ".claude", "CLAUDE.md")),
            (ConfigSource.Project, Path.Combine(folder, "CLAUDE.md")),
            (ConfigSource.Project, Path.Combine(folder, ".claude", "CLAUDE.md")),
            (ConfigSource.Local, Path.Combine(folder, "CLAUDE.local.md")),
        };
        bool any = false;
        foreach (var (src, path) in files)
        {
            if (!File.Exists(path)) continue;
            any = true;
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            root.Children.Add(new ConfigNode
            {
                Name = Path.GetFileName(path), Kind = ConfigNodeKind.Value, EffectiveSource = src,
                DisplayValue = $"{size:N0} bytes", FilePath = path,
            });
        }
        if (!any) root.Children.Add(Info("(no CLAUDE.md applies)"));
        return root;
    }

    // --- .claude/ subfolder inventory ---

    private static ConfigNode BuildDotClaudeTree(string folder)
    {
        var root = new ConfigNode { Name = ".claude contents", Kind = ConfigNodeKind.Branch };
        var dot = Path.Combine(folder, ".claude");
        if (!Directory.Exists(dot))
        {
            root.Children.Add(Info("(no .claude folder in this project)"));
            return root;
        }
        foreach (var sub in new[] { "agents", "commands", "skills", "rules", "hooks", "output-styles" })
        {
            var p = Path.Combine(dot, sub);
            if (!Directory.Exists(p)) continue;
            int count = 0;
            try { count = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Count(); } catch { }
            root.Children.Add(new ConfigNode
            {
                Name = sub, Kind = ConfigNodeKind.Value, DisplayValue = $"{count} file{(count == 1 ? "" : "s")}",
                FilePath = p,
            });
        }
        if (root.Children.Count == 0) root.Children.Add(Info("(.claude has no agents/commands/skills/…)"));
        return root;
    }

    // --- Source files list ---

    private static ConfigNode BuildSourcesTree(List<SourceFile> sources)
    {
        var root = new ConfigNode { Name = "Source files (low → high precedence)", Kind = ConfigNodeKind.Branch };
        foreach (var s in sources)
        {
            string state = s.Malformed ? "(unreadable)" : File.Exists(s.Path) ? "" : "(not present)";
            root.Children.Add(new ConfigNode
            {
                Name = s.Source.ToString().ToLowerInvariant(),
                Kind = ConfigNodeKind.Value,
                EffectiveSource = s.Source,
                DisplayValue = string.IsNullOrEmpty(state) ? s.Path : $"{s.Path}  {state}",
                FilePath = File.Exists(s.Path) ? s.Path : null,
            });
        }
        return root;
    }

    private static ConfigNode Info(string text) =>
        new() { Name = text, Kind = ConfigNodeKind.Branch };

    private static string ValueText(JsonNode? n)
    {
        if (n is null) return "null";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            return v.ToJsonString();
        }
        return n.ToJsonString();
    }
}
