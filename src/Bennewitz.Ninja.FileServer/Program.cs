using Markdig;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

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
    /// exception handler → HTTPS redirect (when cert configured) → static files (embedded wwwroot)
    /// → Markdown intercept → file server → routing → Razor Pages.
    /// </summary>
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

        builder.Services.AddRazorPages();
        builder.Services.AddSingleton(
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());

        var app = builder.Build();

        // Redirect WebRootFileProvider to the embedded manifest so no physical wwwroot/ folder
        // is required at runtime. ManifestEmbeddedFileProvider reads the .webmanifest resource
        // generated by GenerateEmbeddedFilesManifest=true in the csproj.
        app.Environment.WebRootFileProvider =
            new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
        }

        if (Settings.HasHttpsCertificate)
        {
            app.UseHttpsRedirection();
        }

        var requestPath = new PathString($"/{servedFilesRoute}");
        var fileProvider = new AllowedExtensionsFileProvider(
            new PhysicalFileProvider(servedFilesRoot),
            Settings.AllowedExtensions);

        var contentTypeProvider = new FileExtensionContentTypeProvider();

        var fileServerOptions = new FileServerOptions()
        {
            FileProvider = fileProvider,
            RequestPath = requestPath,
            EnableDirectoryBrowsing = true,
            StaticFileOptions =
            {
                RequestPath = requestPath,
                ServeUnknownFileTypes = true,
                ContentTypeProvider = new Utf8TextContentTypeProvider(contentTypeProvider),
                OnPrepareResponse = ctx =>
                    ctx.Context.Response.Headers.CacheControl = "no-store"
            },
            DirectoryBrowserOptions =
            {
                Formatter = new DirectoryFormatter()
            }
        };

        app.UseStaticFiles(); // serves embedded /css/, /js/, /lib/ from the assembly

        // Intercept GET requests for .md files and redirect to the Markdown viewer,
        // unless ?raw is present (allows "View raw" to bypass back to the file server).
        app.Use(async (context, next) =>
        {
            var req = context.Request;
            if (req.Method == HttpMethods.Get
                && req.Path.StartsWithSegments(requestPath, out var remaining)
                && remaining.Value?.EndsWith(".md", StringComparison.OrdinalIgnoreCase) == true
                && !req.Query.ContainsKey("raw")
                && (Settings.AllowedExtensions.Count == 0
                    || Settings.AllowedExtensions.Contains(".md")))
            {
                var relPath = Uri.EscapeDataString(remaining.Value.TrimStart('/'));
                context.Response.Redirect($"/view?path={relPath}");
                return;
            }
            await next(context);
        });

        app.UseFileServer(fileServerOptions);

        app.UseRouting();

        app.MapRazorPages();

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
}
