using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Studio.UI.Services;

namespace Studio.UI;

/// <summary>Registration of the shared UI services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the UI services. Hosts register their own <see cref="Formats.Library.IPackLibrary"/>,
    /// <see cref="IDeviceService"/> and <see cref="IFileSaver"/> before or after this call; browser defaults are used otherwise.
    /// </summary>
    public static IServiceCollection AddStudioUI(this IServiceCollection services)
    {
        services.AddScoped<StudioJs>();
        services.AddScoped<EditorSession>();
        services.TryAddScoped<Formats.Library.IPackLibrary, BrowserPackLibrary>();
        services.TryAddScoped<IFileSaver, BrowserFileSaver>();
        services.TryAddSingleton<IDeviceService, UnsupportedDeviceService>();
        return services;
    }
}
