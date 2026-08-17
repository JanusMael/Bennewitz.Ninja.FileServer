using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System.Text;
using System.Text.Encodings.Web;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Replaces the built-in <see cref="HtmlDirectoryFormatter"/> with one that uses Bootstrap
/// (served from the embedded wwwroot) and respects <c>prefers-color-scheme</c> for dark mode.
/// </summary>
internal sealed class DirectoryFormatter : IDirectoryFormatter
{
    private static readonly HtmlEncoder Enc = HtmlEncoder.Default;

    public async Task GenerateContentAsync(HttpContext context, IEnumerable<IFileInfo> contents)
    {
        context.Response.ContentType = "text/html; charset=utf-8";

        var path      = context.Request.Path.Value ?? "/";
        var segments  = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var routeDepth = Settings.ServedFilesRoute.Trim('/').Split('/').Length;
        var isRoot    = segments.Length <= routeDepth;

        var entries = contents
            .OrderBy(e => e.IsDirectory ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build a URL-encoded href for the first `depth` path segments.
        string MakeHref(int depth) =>
            depth == 0 ? "/" : "/" + string.Join("/", segments.Take(depth).Select(Uri.EscapeDataString));

        var sb = new StringBuilder(4096);

        // $$""" (two dollar signs) so CSS { } are literal and {{expr}} is interpolation.
        sb.Append($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8"/>
              <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
              <title>Index of {{Enc.Encode(path)}}</title>
              <link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css"/>
              <style>
                :root {
                  --fs-bg:#fff;--fs-text:#212529;--fs-border:#dee2e6;
                  --fs-hover:#f8f9fa;--fs-muted:#6c757d;--fs-head:#f8f9fa;
                }
                @media(prefers-color-scheme:dark){
                  :root {
                    --fs-bg:#1a1d20;--fs-text:#dee2e6;--fs-border:#373b3e;
                    --fs-hover:#25282b;--fs-muted:#9ca3af;--fs-head:#212529;
                  }
                }
                body{background:var(--fs-bg);color:var(--fs-text);}
                a{color:var(--fs-text);text-decoration:none;}
                a:hover{text-decoration:underline;}
                .breadcrumb-item+.breadcrumb-item::before{color:var(--fs-muted);}
                .table{color:var(--fs-text);border-color:var(--fs-border);}
                .table>:not(caption)>*>*{background:transparent;border-color:var(--fs-border);}
                .table-hover>tbody>tr:hover>*{background:var(--fs-hover);}
                thead th{background:var(--fs-head);}
                .sz{width:7rem;text-align:right;}
                .mod{width:11rem;}
              </style>
            </head>
            <body>
            <div class="container py-3">
            """);

        // Breadcrumb
        sb.Append("<nav aria-label=\"breadcrumb\"><ol class=\"breadcrumb\">");
        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            var label  = Enc.Encode(segments[i]);
            if (isLast)
                sb.Append($"<li class=\"breadcrumb-item active\" aria-current=\"page\">{label}</li>");
            else
                sb.Append($"<li class=\"breadcrumb-item\"><a href=\"{Enc.Encode(MakeHref(i + 1))}\">{label}</a></li>");
        }
        sb.Append("</ol></nav>");

        sb.Append("""
            <table class="table table-hover table-sm align-middle">
              <thead><tr>
                <th>Name</th>
                <th class="sz">Size</th>
                <th class="mod text-muted">Modified</th>
              </tr></thead>
              <tbody>
            """);

        if (!isRoot)
        {
            sb.Append($"""
                <tr>
                  <td><a href="{Enc.Encode(MakeHref(segments.Length - 1))}">📁 ..</a></td>
                  <td class="sz text-muted">—</td><td class="mod text-muted">—</td>
                </tr>
                """);
        }

        foreach (var e in entries)
        {
            var href    = Enc.Encode(MakeHref(segments.Length) + "/" + Uri.EscapeDataString(e.Name));
            var icon    = e.IsDirectory ? "📁" : "📄";
            var display = Enc.Encode(e.Name) + (e.IsDirectory ? "/" : "");
            var size    = e.IsDirectory ? "—" : FormatSize(e.Length);
            var mod     = e.LastModified == default
                ? "—"
                : e.LastModified.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

            sb.Append($"""
                <tr>
                  <td><a href="{href}">{icon} {display}</a></td>
                  <td class="sz text-muted">{Enc.Encode(size)}</td>
                  <td class="mod text-muted">{Enc.Encode(mod)}</td>
                </tr>
                """);
        }

        sb.Append("</tbody></table></div></body></html>");

        await context.Response.WriteAsync(sb.ToString(), Encoding.UTF8);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 0             => "—",
        < 1_024         => $"{bytes} B",
        < 1_048_576     => $"{bytes / 1_024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _               => $"{bytes / 1_073_741_824.0:F1} GB"
    };
}
