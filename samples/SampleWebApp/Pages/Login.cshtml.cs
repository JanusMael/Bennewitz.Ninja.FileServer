using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SampleWebApp.Pages;

/// <summary>
/// Signs in whoever asks. The point is the round trip a protected mount produces — challenge,
/// sign in, land back on the file that was requested — not the credentials.
/// </summary>
public class LoginModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string name, string? returnUrl)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? "sample-user" : name)],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        // Only a local URL: returnUrl arrives from the query string, and following an absolute
        // one would make this an open redirect.
        return Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToPage("/Index");
    }
}
