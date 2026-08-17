using Markdig;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Registers the services needed to mount a file server inside an ASP.NET Core application.
/// </summary>
public static class FileServerServiceCollectionExtensions
{
    /// <summary>
    /// Adds the file-server component's services. Call once during startup; mounts are then
    /// declared with <c>MapFileServer</c>. Calling it more than once is harmless.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// Registers the Razor view engine via <c>AddMvcCore</c> rather than <c>AddRazorPages</c>,
    /// which would pull in page routing this component never uses. Both are additive and safe
    /// alongside a host that already called <c>AddRazorPages</c> or <c>AddControllersWithViews</c>.
    /// </remarks>
    public static IServiceCollection AddFileServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<FileServerMountRegistry>();
        services.TryAddSingleton<RazorViewRenderer>();
        services.TryAddSingleton(new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());

        services.AddMvcCore()
            .AddRazorViewEngine()
            // Register this assembly's compiled views explicitly instead of relying on
            // DependencyContext-based discovery. Discovery does work in the configurations
            // tested, but it is not something to depend on: it has been fragile across
            // releases and would be the first thing to break under trimming.
            .ConfigureApplicationPartManager(manager =>
            {
                var assembly = typeof(FileServerServiceCollectionExtensions).Assembly;

                if (!manager.ApplicationParts.Any(part =>
                        part is CompiledRazorAssemblyPart compiled && compiled.Assembly == assembly))
                {
                    manager.ApplicationParts.Add(new CompiledRazorAssemblyPart(assembly));
                }
            });

        return services;
    }
}
