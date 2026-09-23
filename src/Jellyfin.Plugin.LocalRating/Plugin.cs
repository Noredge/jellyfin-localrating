using Jellyfin.Plugin.LocalRating.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LocalRating;

/// <summary>The Local Rating plugin entry point.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages() => [new PluginPageInfo
    {
        Name = "localratingconfiguration",
        EmbeddedResourcePath = "Jellyfin.Plugin.LocalRating.Web.configuration.html"
    }];

    /// <inheritdoc />
    public override string Name => "Local Rating";

    /// <inheritdoc />
    public override string Description => "Private per-user ratings and reviews for the Jellyfin web client.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("d67e52c4-19ca-48ba-81f0-ea6fe703896b");
}
