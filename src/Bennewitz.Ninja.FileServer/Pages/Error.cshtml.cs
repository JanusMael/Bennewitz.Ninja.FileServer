using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bennewitz.Ninja.FileServer.Pages;

/// <summary>
/// Error page model displayed when an unhandled exception occurs. Wired via
/// <c>app.UseExceptionHandler("/Error")</c> in non-development environments.
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    /// <summary>The ASP.NET Core request or activity trace identifier for the failed request.</summary>
    public string? RequestId { get; set; }

    /// <summary><c>true</c> when <see cref="RequestId"/> is non-empty and should be displayed.</summary>
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>Populates <see cref="RequestId"/> from the current activity or HTTP context trace.</summary>
    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}
