using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LocalRating.Configuration;

/// <summary>Opt-in response compatibility settings.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableRatingCompatibility { get; set; }
    public CompatibilityRule[] CompatibilityRules { get; set; } = [];
}

public sealed class CompatibilityRule
{
    public Guid UserId { get; set; }
    public Guid[] LibraryIds { get; set; } = [];
}
