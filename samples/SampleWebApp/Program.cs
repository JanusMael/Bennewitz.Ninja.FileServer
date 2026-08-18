using Bennewitz.Ninja.FileServer;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// One call registers everything the component needs: the view engine for its own compiled
// views, the Markdown pipeline, and the registry that catches conflicting mounts.
builder.Services.AddFileServer();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/Login");

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

var content = Path.Combine(app.Environment.ContentRootPath, "content");

// The plainest form: a directory, browsable at a route, styled by the component itself.
app.MapFileServer("/files", options => options.RootPath = Path.Combine(content, "public"));

// The same component rendered inside this application's layout, so the file browser sits under
// the site's own header and navigation instead of standing alone.
app.MapFileServer("/docs", options =>
{
    options.RootPath = Path.Combine(content, "docs");
    options.LayoutPath = "/Pages/Shared/_Layout.cshtml";
});

// Only these extensions are listed, and only these can be downloaded — the filter applies to
// both, so a hidden file is not quietly reachable by typing its URL.
app.MapFileServer("/reports", options =>
{
    options.RootPath = Path.Combine(content, "reports");
    options.AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv", ".md" };
});

// MapFileServer returns the mount's route group, so this covers every route the mount owns:
// the listing, the rendered Markdown, and the file downloads themselves. Try fetching
// /private/salaries.csv while signed out — the challenge applies to the bytes, not just the page
// that links to them.
//
// It borrows the host layout as well, so a signed-in visitor keeps the site's header and its
// sign-out link instead of landing somewhere that looks like a different application.
app.MapFileServer("/private", options =>
    {
        options.RootPath = Path.Combine(content, "private");
        options.LayoutPath = "/Pages/Shared/_Layout.cshtml";
    })
   .RequireAuthorization();

app.Run();
