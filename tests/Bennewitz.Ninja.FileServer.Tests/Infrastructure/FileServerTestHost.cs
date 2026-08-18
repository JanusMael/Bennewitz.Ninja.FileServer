using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bennewitz.Ninja.FileServer.Tests.Infrastructure;

/// <summary>
/// Builds a real ASP.NET Core pipeline over <see cref="TestServer"/>, configured the way a host
/// application would configure it.
/// </summary>
/// <remarks>
/// Deliberately not <c>WebApplicationFactory</c>: that needs an entry-point assembly, and the
/// component under test is a library with no host of its own. Building the pipeline here also
/// lets each test declare its own mounts and policies, which is the surface being tested.
/// </remarks>
internal sealed class FileServerTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private FileServerTestHost(IHost host)
    {
        _host = host;
        Client = host.GetTestClient();
    }

    internal HttpClient Client { get; }

    /// <summary>
    /// Starts a host whose endpoints are declared by <paramref name="map"/>. Authentication and
    /// authorization are always wired up, so a mount that calls <c>RequireAuthorization</c>
    /// behaves exactly as it would in an application that has them.
    /// </summary>
    internal static async Task<FileServerTestHost> StartAsync(Action<IEndpointRouteBuilder> map)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddFileServer();

                        services.AddAuthentication(TestAuthenticationHandler.SchemeName)
                            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                                TestAuthenticationHandler.SchemeName, _ => { });

                        services.AddAuthorization();
                        services.AddRouting();
                    })
                    .ConfigureLogging(logging => logging.ClearProviders())
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(map);
                    });
            })
            .StartAsync();

        return new FileServerTestHost(host);
    }

    /// <summary>
    /// Builds the pipeline without starting it, so a registration that is supposed to fail during
    /// construction throws where the test can observe it.
    /// </summary>
    internal static Task StartExpectingFailureAsync(Action<IEndpointRouteBuilder> map) =>
        StartAsync(map).AsDisposedTask();

    internal HttpRequestMessage AuthenticatedGet(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthenticationHandler.HeaderName, "tester");
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}

internal static class TestHostTaskExtensions
{
    /// <summary>Awaits a host that is expected to throw, disposing it if it unexpectedly starts.</summary>
    internal static async Task AsDisposedTask(this Task<FileServerTestHost> task)
    {
        var host = await task;
        await host.DisposeAsync();
    }
}
