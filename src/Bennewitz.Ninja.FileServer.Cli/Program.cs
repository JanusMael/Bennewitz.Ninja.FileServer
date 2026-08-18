using System.Diagnostics;
using System.Text.Encodings.Web;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Application entry point. Configures the Kestrel web server and the HTTP request pipeline,
/// then starts serving files from the directory specified in <see cref="Settings.ServedFilesRoot"/>.
/// </summary>
public static class Program
{
    /// <summary>
    /// Initialises settings from all configuration sources, registers global exception handlers,
    /// then runs the application. Startup exceptions are classified and translated to a structured
    /// <see cref="ExitCode"/> so the container runtime can distinguish configuration errors from
    /// transient crashes.
    /// </summary>
    public static async Task Main(string[] args)
    {
        // Last-resort handler for exceptions that escape all catch blocks — background threads,
        // raw ThreadPool callbacks, or anything else that slips past the request pipeline.
        // Write to stderr (Docker captures both stdout and stderr in `docker logs`) then exit
        // with a deterministic code before the CLR terminates the process with a platform-
        // dependent value.
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            Console.Error.WriteLine($"[FATAL] Unhandled exception (IsTerminating={e.IsTerminating}):");
            Console.Error.WriteLine(e.ExceptionObject);
            Environment.Exit(ExitCode.UnhandledException);
        };

        // In .NET 6+ an unobserved task exception no longer crashes the process, but it still
        // indicates a fire-and-forget bug worth surfacing. Mark it observed so the GC finalizer
        // does not re-throw after we log it.
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            Console.Error.WriteLine($"[WARN] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        try
        {
            Settings.Initialize(args);
            await Start(args);
        }
        catch (Exception e)
        {
            var code = ClassifyStartupException(e);
            Console.Error.WriteLine($"[FATAL] {e.Message}");
            if (code == ExitCode.UnhandledException)
                Console.Error.WriteLine(e.ToString());
            Environment.Exit(code);
        }
    }

    /// <summary>
    /// Maps a startup exception to the appropriate <see cref="ExitCode"/> so the container
    /// runtime can act on the reason for the failure without parsing log output.
    /// </summary>
    private static int ClassifyStartupException(Exception e) => e switch
    {
        // Settings validation throws InvalidOperationException for missing or malformed config.
        InvalidOperationException => ExitCode.ConfigurationError,

        // Thrown when the served directory or certificate file does not exist at the configured path.
        DirectoryNotFoundException or FileNotFoundException => ExitCode.EnvironmentError,

        // Unexpected — may recover on restart.
        _ => ExitCode.UnhandledException
    };


    /// <summary>
    /// Builds and runs the web application. Pipeline order:
    /// exception handler → HTTPS redirect (when cert configured) → routing → the mounted file
    /// server.
    /// </summary>
    /// <remarks>
    /// The browser UI, containment, Markdown rendering, and asset serving all live in the
    /// component; this host only translates <see cref="Settings"/> into one call to
    /// <c>MapFileServer</c>. Anything a consumer of the package cannot do, this executable
    /// cannot do either — which is the point, since it keeps the package honest.
    /// </remarks>
    private static async Task Start(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.UseKestrel(serverOptions =>
        {
            serverOptions.ListenAnyIP(Settings.HttpPort);

            if (Settings.HasHttpsCertificate)
            {
                serverOptions.ListenAnyIP(Settings.HttpsPort, listenOptions =>
                {
                    listenOptions.UseHttps(Settings.CertificatePath, Settings.CertificatePassword);
                });
            }
        });

        // Validate all required settings before building — fail fast with a clear message.
        var servedFilesRoot = Settings.ServedFilesRoot;
        var servedFilesRoute = Settings.ServedFilesRoute;

        if (!Directory.Exists(servedFilesRoot))
            throw new DirectoryNotFoundException(
                $"Served files directory does not exist: '{servedFilesRoot}'. " +
                "Create the directory or update ServedFilesRoot in settings.json / FILE_SERVER_ROOT.");

        if (Settings.HasHttpsCertificate && !File.Exists(Settings.CertificatePath))
            throw new FileNotFoundException(
                $"Certificate file does not exist: '{Settings.CertificatePath}'. " +
                "Check volume mounts or update CertificatePath in settings.json / FILE_SERVER_CERT_PATH.");

        builder.Services.AddFileServer();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            // Handled inline rather than by re-executing to an error page: this host has no pages
            // of its own now that the browser UI belongs to the component.
            app.UseExceptionHandler(errorApp => errorApp.Run(WriteErrorPage));
        }

        if (Settings.HasHttpsCertificate)
        {
            app.UseHttpsRedirection();
        }

        app.MapFileServer(servedFilesRoute, options =>
        {
            options.RootPath = servedFilesRoot;
            options.AllowedExtensions = Settings.AllowedExtensions;
        });

        // Serving that one directory is all this executable does, so the root is a redirect to it
        // rather than a landing page. PathBase is honoured for the sake of a reverse proxy that
        // strips a prefix, even though nothing here sets one.
        app.MapGet("/", (HttpContext http) =>
            Results.Redirect($"{http.Request.PathBase}/{servedFilesRoute}"));

        if (Settings.HasHttpsCertificate)
        {
            Console.WriteLine($"Serving '{servedFilesRoot}' at https://localhost:{Settings.HttpsPort}/{servedFilesRoute}");
            Console.WriteLine($"  HTTP on port {Settings.HttpPort} redirects to HTTPS.");
        }
        else
        {
            Console.WriteLine($"Serving '{servedFilesRoot}' at http://localhost:{Settings.HttpPort}/{servedFilesRoute}");
        }

        if (Settings.AllowedExtensions.Count == 0 || Settings.AllowedExtensions.Contains(".md"))
            Console.WriteLine("  Markdown files rendered as HTML (append ?raw to serve raw text).");

        await app.RunAsync();
    }

    /// <summary>
    /// Writes the response for a request that failed with an unhandled exception. Reports the
    /// trace identifier so a log line can be matched to the visitor's report, and nothing else —
    /// the exception itself stays in the log.
    /// </summary>
    private static async Task WriteErrorPage(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        // Framework-generated, but encoded rather than trusted: the header-derived W3C trace
        // identifier can carry whatever a caller sent.
        var requestId = HtmlEncoder.Default.Encode(
            Activity.Current?.Id ?? context.TraceIdentifier);

        // Two '$' so a single brace is literal CSS and only '{{…}}' interpolates.
        await context.Response.WriteAsync(
            $$"""
              <!DOCTYPE html>
              <html lang="en">
              <head>
                  <meta charset="utf-8"/>
                  <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
                  <title>Error</title>
                  <style>
                      body { font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
                              max-width: 40rem; margin: 4rem auto; padding: 0 1rem; line-height: 1.5; }
                      code { font-size: .9em; }
                      .muted { color: #6c757d; }
                      @media (prefers-color-scheme: dark) {
                          body { background: #1a1d20; color: #dee2e6; }
                          .muted { color: #9ca3af; }
                      }
                  </style>
              </head>
              <body>
              <h1>Something went wrong.</h1>
              <p>An unexpected error occurred while processing your request. Please try again.</p>
              <p class="muted">Request ID: <code>{{requestId}}</code></p>
              <p>If this keeps happening, contact your administrator and include the Request ID.</p>
              </body>
              </html>
              """,
            context.RequestAborted);
    }
}
