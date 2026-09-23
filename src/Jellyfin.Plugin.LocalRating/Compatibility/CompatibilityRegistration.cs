using MediaBrowser.Controller.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.LocalRating.Compatibility;

public static class CompatibilityRegistration
{
    public static bool Register(IServiceCollection services)
    {
        var native = services.LastOrDefault(d => d.ServiceType == typeof(IDtoService) && !d.IsKeyedService);
        if (native is null) return false;
        var key = new object();
        services.Remove(native);
        if (native.ImplementationInstance is IDtoService instance)
            services.AddKeyedSingleton<IDtoService>(key, instance);
        else
            services.Add(ServiceDescriptor.DescribeKeyed(typeof(IDtoService), key, (provider, _) =>
                native.ImplementationFactory?.Invoke(provider) ?? ActivatorUtilities.CreateInstance(provider, native.ImplementationType!), native.Lifetime));
        services.AddHttpContextAccessor();
        services.Add(new ServiceDescriptor(typeof(IDtoService), provider => new CompatibilityDtoService(
            provider.GetRequiredKeyedService<IDtoService>(key), provider.GetRequiredService<IHttpContextAccessor>()), native.Lifetime));
        services.TryAddSingleton<IRatingScope, RatingScope>();
        services.AddScoped<CompatibilityFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<CompatibilityFilter>());
        return true;
    }
}
