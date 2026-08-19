using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Mounts a browsable, downloadable view of a directory onto an application's routes.
/// </summary>
public static class FileServerEndpointRouteBuilderExtensions
{
    private const string DirectoryView = "/Views/FileServer/Directory.cshtml";
    private const string MarkdownView  = "/Views/FileServer/Markdown.cshtml";

    private static readonly Utf8TextContentTypeProvider ContentTypes =
        new(new FileExtensionContentTypeProvider());

    /// <summary>
    /// Mounts a file server at <paramref name="prefix"/>.
    /// </summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <param name="prefix">
    /// Route prefix for the mount, e.g. <c>/docs</c>. A leading slash is added if absent and a
    /// trailing slash removed.
    /// </param>
    /// <param name="configure">Configures this mount. <c>RootPath</c> must be set.</param>
    /// <returns>
    /// The mount's route group, so conventions such as <c>RequireAuthorization()</c> can be
    /// applied to every route it owns.
    /// </returns>
    /// <remarks>
    /// Call any number of times to serve several directories, each independently configured
    /// and independently protected. Files are served from endpoints rather than static-file
    /// middleware precisely so that authorization applied to this group also covers downloads.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <c>AddFileServer()</c> was not called; <c>RootPath</c> was not set; the prefix is already
    /// mounted; or the root overlaps another mount's root.
    /// </exception>
    /// <exception cref="ArgumentException"><c>RootPath</c> is not an absolute path.</exception>
    /// <exception cref="DirectoryNotFoundException"><c>RootPath</c> does not exist.</exception>
    public static RouteGroupBuilder MapFileServer(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        Action<FileServerMountOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var registry = endpoints.ServiceProvider.GetService<FileServerMountRegistry>()
            ?? throw new InvalidOperationException(
                "The file server's services are not registered. Call AddFileServer() on your " +
                "service collection before calling MapFileServer().");

        var options = new FileServerMountOptions();
        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new InvalidOperationException(
                $"RootPath was not set for the file server mounted at '{prefix}'. Set it to the " +
                "absolute path of the directory to serve.");

        if (!Path.IsPathRooted(options.RootPath))
            throw new ArgumentException(
                $"RootPath '{options.RootPath}' is not an absolute path. Provide a fully-qualified " +
                "path such as /srv/files or C:\\Share.",
                nameof(configure));

        if (!Directory.Exists(options.RootPath))
            throw new DirectoryNotFoundException(
                $"The directory to serve does not exist: '{options.RootPath}'.");

        var mount = new FileServerMount(NormalisePrefix(prefix), options);
        registry.Register(mount);

        // Mapped outside the group on purpose. Inside it, a caller's RequireAuthorization()
        // would append authorization metadata after this endpoint's own AllowAnonymous(), and
        // which one wins depends on metadata ordering. Out here, group conventions cannot
        // reach it, so an unauthenticated visitor still gets a styled page at the login prompt.
        endpoints.MapGet(
                $"{mount.Prefix}/{FileServerAssets.RouteSegment}/{{version}}/{{**assetPath}}",
                (HttpContext http, string assetPath) => ServeAsset(http, assetPath))
            .AllowAnonymous()
            .WithName($"FileServerAssets{mount.Prefix.Replace('/', '_')}");

        var group = endpoints.MapGroup(mount.Prefix);
        group.WithMetadata(mount);

        // Cast to Delegate: a lambda whose only parameter is HttpContext otherwise binds as a
        // RequestDelegate, which returns Task and would silently discard the IResult.
        group.MapGet("/", (Delegate)((HttpContext http) => Handle(http, string.Empty)));
        group.MapGet("/{**path}", (HttpContext http, string path) => Handle(http, path));

        return group;
    }

    private static string NormalisePrefix(string prefix)
    {
        var trimmed = prefix.Trim().TrimEnd('/');
        if (trimmed.Length == 0) return "/";
        return trimmed.StartsWith('/') ? trimmed : '/' + trimmed;
    }

    private static async Task<IResult> Handle(HttpContext http, string path)
    {
        var mount = http.GetEndpoint()?.Metadata.GetMetadata<FileServerMount>();
        if (mount is null)
            return Results.NotFound();

        path = path.Trim('/');

        // Containment first, before anything touches the filesystem on the caller's behalf.
        if (!mount.TryResolve(path, out var fullPath))
            return Results.NotFound();

        if (Directory.Exists(fullPath))
        {
            return mount.Options.EnableDirectoryBrowsing
                ? await RenderDirectory(http, mount, path, fullPath)
                : Results.NotFound();
        }

        if (!File.Exists(fullPath))
            return Results.NotFound();

        var fileName = Path.GetFileName(fullPath);

        // Re-checked here and not left to AllowedExtensionsFileProvider: serving a file by
        // physical path bypasses the provider entirely, so without this a filtered extension
        // would be hidden from listings yet still downloadable by direct URL.
        if (!mount.IsAllowed(fileName))
            return Results.NotFound();

        var isMarkdown = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

        if (isMarkdown && mount.Options.RenderMarkdown && !http.Request.Query.ContainsKey("raw"))
            return await RenderMarkdown(http, mount, path, fullPath);

        return ServeFile(http, mount, fullPath, fileName);
    }

    private static IResult ServeFile(
        HttpContext http,
        FileServerMount mount,
        string fullPath,
        string fileName)
    {
        var info = new FileInfo(fullPath);

        if (!ContentTypes.TryGetContentType(fileName, out var contentType))
            contentType = "application/octet-stream";

        http.Response.Headers.CacheControl = mount.Options.CacheControl;

        // lastModified and entityTag are supplied explicitly so conditional requests behave
        // deterministically rather than depending on framework defaults.
        var lastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");

        return Results.File(
            fullPath,
            contentType,
            lastModified: lastModified,
            entityTag: etag,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> RenderDirectory(
        HttpContext http,
        FileServerMount mount,
        string path,
        string fullPath)
    {
        var renderer = http.RequestServices.GetRequiredService<RazorViewRenderer>();

        var contents = mount.FileProvider.GetDirectoryContents(path);

        var entries = contents
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new FileServerEntry(
                entry.Name,
                mount.Url(http.Request, Combine(path, entry.Name)),
                entry.IsDirectory,
                entry.IsDirectory ? null : entry.Length,
                entry.IsDirectory ? null : entry.LastModified))
            .ToList();

        var model = new FileServerDirectoryViewModel
        {
            Title = path.Length == 0 ? mount.Prefix.TrimStart('/') : path,
            Breadcrumbs = BuildCrumbs(http, mount, path, includeLeaf: true),
            StylesheetUrl = AssetUrl(http, mount, "css/fileserver.css"),
            ScriptUrl = AssetUrl(http, mount, "js/fileserver.js"),
            Entries = entries,
            ParentUrl = path.Length == 0
                ? null
                : mount.Url(http.Request, ParentOf(path))
        };

        await renderer.RenderAsync(http, DirectoryView, model, mount.Options.LayoutPath);
        return Results.Empty;
    }

    private static async Task<IResult> RenderMarkdown(
        HttpContext http,
        FileServerMount mount,
        string path,
        string fullPath)
    {
        var renderer = http.RequestServices.GetRequiredService<RazorViewRenderer>();
        var pipeline = http.RequestServices.GetRequiredService<MarkdownPipeline>();

        string source;
        try
        {
            source = await File.ReadAllTextAsync(fullPath, http.RequestAborted);
        }
        catch (IOException)
        {
            return Results.NotFound();
        }

        var fileName = Path.GetFileName(fullPath);

        var model = new FileServerMarkdownViewModel
        {
            Title = fileName,
            Breadcrumbs = BuildCrumbs(http, mount, path, includeLeaf: false),
            StylesheetUrl = AssetUrl(http, mount, "css/fileserver.css"),
            MarkdownStylesheetUrl = AssetUrl(http, mount, "css/github-markdown.min.css"),
            ScriptUrl = AssetUrl(http, mount, "js/fileserver.js"),
            Content = new HtmlString(Markdown.ToHtml(source, pipeline)),
            FileName = fileName,
            RawUrl = mount.Url(http.Request, path) + "?raw=1"
        };

        await renderer.RenderAsync(http, MarkdownView, model, mount.Options.LayoutPath);
        return Results.Empty;
    }

    private static IResult ServeAsset(HttpContext http, string assetPath)
    {
        if (!FileServerAssets.TryOpen(assetPath, out var content, out var contentType))
            return Results.NotFound();

        // The version segment changes with every build of this assembly, so a given URL always
        // answers with the same bytes and can be cached for as long as the client likes. Said
        // explicitly rather than left to heuristic freshness, which would re-request the
        // stylesheet on most navigations.
        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        return Results.Stream(content, contentType, enableRangeProcessing: false);
    }

    private static string? AssetUrl(HttpContext http, FileServerMount mount, string asset)
    {
        if (!mount.Options.IncludeDefaultStyles)
            return null;

        var basePath = http.Request.PathBase.HasValue ? http.Request.PathBase.Value : string.Empty;
        return $"{basePath}{mount.Prefix}/{FileServerAssets.RouteSegment}/{FileServerAssets.Version}/{asset}";
    }

    private static List<FileServerCrumb> BuildCrumbs(
        HttpContext http,
        FileServerMount mount,
        string path,
        bool includeLeaf)
    {
        var crumbs = new List<FileServerCrumb>
        {
            new(mount.Prefix.TrimStart('/'), mount.Url(http.Request))
        };

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var limit = includeLeaf ? segments.Length : segments.Length - 1;

        for (var i = 0; i < limit; i++)
        {
            crumbs.Add(new FileServerCrumb(
                segments[i],
                mount.Url(http.Request, string.Join('/', segments.Take(i + 1)))));
        }

        return crumbs;
    }

    private static string Combine(string path, string name) =>
        path.Length == 0 ? name : $"{path}/{name}";

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }
}
