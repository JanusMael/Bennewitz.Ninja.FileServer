using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bennewitz.Ninja.FileServer.Pages;

/// <summary>
/// Entry-point page model. Immediately redirects to the file browser route;
/// the view body is never rendered.
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>Redirects to the configured file-browser route (e.g. <c>/files</c>).</summary>
    public IActionResult OnGet()
    {
        return LocalRedirect($"~/{Settings.ServedFilesRoute}");
    }
}
