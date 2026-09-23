using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LocalRating;

/// <summary>Registers Local Rating services in Jellyfin's dependency container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LocalRatingRepository>();
        serviceCollection.AddTransient<IStartupFilter, LocalRatingStartupFilter>();
        Compatibility.CompatibilityRegistration.Register(serviceCollection);
    }
}
