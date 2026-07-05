using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ClaudeCodeExplorer.Models;

/// <summary>Which settings tier a value came from, lowest → highest precedence.</summary>
public enum ConfigSource { None, User, Project, Local, Managed }

/// <summary>Shape of a node, used to pick an icon / rendering in the tree.</summary>
public enum ConfigNodeKind { Branch, Object, Array, Value }

/// <summary>A lower-precedence value that is shadowed by the effective one.</summary>
public sealed class ShadowedValue
{
    public ConfigSource Source { get; init; }
    public string Value { get; init; } = "";
}

/// <summary>
/// One node in the config-inspector tree: a merged setting, an array element, a file, or a
/// grouping branch. Built once per inspection (no change notification needed).
/// </summary>
public sealed class ConfigNode
{
    public string Name { get; set; } = "";
    public string DisplayValue { get; set; } = "";
    public ConfigNodeKind Kind { get; set; } = ConfigNodeKind.Value;

    /// <summary>Tier that provides the effective value (None for grouping branches).</summary>
    public ConfigSource EffectiveSource { get; set; } = ConfigSource.None;

    /// <summary>True when a lower tier defines a different value that is shadowed by this one.</summary>
    public bool IsOverridden { get; set; }

    /// <summary>Lower-tier values hidden behind the effective one (for the override tooltip).</summary>
    public List<ShadowedValue> Shadowed { get; } = new();

    /// <summary>Absolute path this node can open (settings file, CLAUDE.md, …), if any.</summary>
    public string? FilePath { get; set; }

    public bool IsExpanded { get; set; } = true;
    public ObservableCollection<ConfigNode> Children { get; } = new();

    // --- UI helpers (bindings) ---
    public bool ShowBadge => EffectiveSource != ConfigSource.None;
    public bool ShowValue => Kind == ConfigNodeKind.Value && DisplayValue.Length > 0;
    public bool CanOpen => !string.IsNullOrEmpty(FilePath);

    public string SourceLabel => EffectiveSource switch
    {
        ConfigSource.User => "user",
        ConfigSource.Project => "project",
        ConfigSource.Local => "local",
        ConfigSource.Managed => "managed",
        _ => "",
    };

    public string? OverrideTooltip =>
        Shadowed.Count == 0 ? null :
        "Overrides:\n" + string.Join("\n", Shadowed.Select(s =>
            $"  {s.Source.ToString().ToLowerInvariant()}: {s.Value}"));
}
