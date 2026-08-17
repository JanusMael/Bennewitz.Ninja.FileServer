using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Renders one of this library's Razor views straight to the response, outside the MVC
/// pipeline, so the file browser can be served from plain endpoints and inherit whatever
/// authorization the host attached to them.
/// </summary>
internal sealed class RazorViewRenderer(
    IRazorViewEngine viewEngine,
    ITempDataProvider tempDataProvider)
{
    /// <summary>ViewData key carrying the caller-selected layout path.</summary>
    internal const string LayoutKey = "__FileServerLayout";

    internal async Task RenderAsync<TModel>(
        HttpContext http,
        string viewPath,
        TModel model,
        string? layoutPath)
    {
        // The ActionContext must carry the REAL route data. A host layout typically contains
        // anchor tag helpers (asp-page, asp-controller) that resolve URLs through
        // IUrlHelper/LinkGenerator, and those read ActionContext.RouteData — a blank RouteData
        // makes them throw or silently emit wrong links in the host's own navigation.
        var actionContext = new ActionContext(
            http,
            http.GetRouteData(),
            new ActionDescriptor());

        var result = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: true);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"The view '{viewPath}' could not be located. This usually means the library was " +
                "not registered as an MVC application part. Searched: " +
                string.Join("; ", result.SearchedLocations ?? []));
        }

        var viewData = new ViewDataDictionary<TModel>(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };

        if (!string.IsNullOrWhiteSpace(layoutPath))
            viewData[LayoutKey] = layoutPath;

        // Buffered rather than streamed: rendering must be able to fail into the host's
        // exception handler without a half-written response already on the wire.
        using var writer = new StringWriter();

        var viewContext = new ViewContext(
            actionContext,
            result.View,
            viewData,
            new TempDataDictionary(http, tempDataProvider),
            writer,
            new HtmlHelperOptions());

        await result.View.RenderAsync(viewContext);

        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.WriteAsync(writer.ToString());
    }
}
