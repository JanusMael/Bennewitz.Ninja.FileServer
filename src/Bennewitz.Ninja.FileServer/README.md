# Bennewitz.Ninja.FileServer

Mount a browsable, downloadable view of a directory onto a route of an ASP.NET Core
application. Directory listings and rendered Markdown, styled out of the box, with downloads
covered by whatever authorization you put on the mount.

```bash
dotnet add package Bennewitz.Ninja.FileServer
```

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFileServer();

var app = builder.Build();

app.MapFileServer("/docs", options =>
{
    options.RootPath = "/srv/docs";      // absolute, must exist at startup
});

app.Run();
```

`/docs` now lists the directory, `/docs/guide/setup.md` renders as HTML, and
`/docs/guide/setup.md?raw` serves the source. Nothing else is required: the views, the
stylesheet, and the colour-scheme script are compiled or embedded into the assembly and served
from the mount's own endpoint, so the component works in a host that never calls
`UseStaticFiles` and in a single-file publish.

## Protecting a mount

`MapFileServer` returns the mount's route group, so a convention applied to it covers every
route the mount owns — listings and file downloads alike:

```csharp
app.MapFileServer("/private", options => options.RootPath = "/srv/private")
   .RequireAuthorization("StaffOnly");
```

Files are served from endpoints rather than static-file middleware precisely for this reason:
static-file middleware produces no endpoints, so authorization would have nothing to enforce
against and downloads would stay open while listings looked protected. The stylesheet endpoint
is deliberately left anonymous, so the login page a challenge redirects to is still styled.

## Several directories at once

Each mount is configured and protected independently, with no shared state:

```csharp
app.MapFileServer("/public", o => o.RootPath = "/srv/public");
app.MapFileServer("/reports", o =>
{
    o.RootPath = "/srv/reports";
    o.AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".csv" };
}).RequireAuthorization();
```

Registrations that would make authorization ambiguous fail while the pipeline is being built,
not at request time: a duplicate prefix, or a root that overlaps another mount's root.

## Options

| Option | Default | Purpose |
| --- | --- | --- |
| `RootPath` | *(required)* | Absolute path of the directory to serve. Must exist at startup. |
| `AllowedExtensions` | *(empty — all files)* | Extensions that may be listed **and** downloaded, e.g. `.pdf`. Applied on both paths. |
| `EnableDirectoryBrowsing` | `true` | When `false`, directories 404 while direct file downloads still work. |
| `RenderMarkdown` | `true` | When `false`, `.md` files are served as raw bytes. |
| `LayoutPath` | `null` | A host layout to render inside, e.g. `/Views/Shared/_Layout.cshtml`. Defaults to the component's own self-contained layout. |
| `IncludeDefaultStyles` | `true` | Emit the component's stylesheet and colour-scheme toggle. Set `false` to style the markup yourself. |
| `CacheControl` | `no-store` | `Cache-Control` sent with served files. |

## Markdown

`.md` files render as HTML, with `?raw` serving the source. Listings and documents alike carry an
Auto / Light / Dark control that pins a colour scheme against the reader's system preference,
remembered under one key so it holds across both. Fenced code blocks are tokenised
into GitHub's own token classes — `pl-k`, `pl-s`, `pl-c` and the rest — which the bundled
stylesheet already colours, so highlighted code follows the colour-scheme toggle rather than
carrying colours of its own. Roughly two dozen languages are recognised (`csharp`, `xml`,
`javascript`, `typescript`, `powershell`, `sql`, `python`, `java`, `cpp`, `json`, `css`, `html`
and friends); a fence tagged with anything else keeps its text and loses only the colour.

Set `RenderMarkdown = false` to serve `.md` files as bytes like any other file.

## Using your own layout

```csharp
app.MapFileServer("/docs", options =>
{
    options.RootPath = "/srv/docs";
    options.LayoutPath = "/Views/Shared/_Layout.cshtml";
});
```

The component's views never declare Razor sections, so a layout is under no obligation to
render any — an unrendered declared section throws at request time, and a component cannot
know what a host layout renders. Its stylesheet link is emitted in the page body for the same
reason. Every class is prefixed `bnfs-`, so the styles cannot collide with a host's own
framework, and dropping them (`IncludeDefaultStyles = false`) leaves the markup intact for you
to style.

## Containment

Requests are resolved against the mount root with every path segment's symlinks resolved
first, then compared ordinally. `Path.GetFullPath` alone is string canonicalisation that never
touches the filesystem, so a link anywhere along the path — not just at the leaf — would
otherwise escape the root undetected.

## Standalone server

The same component powers a standalone single-file binary for six platforms, configured by
`settings.json`, environment variables, or CLI arguments, with Docker images and HTTPS
support. See the [repository](https://github.com/JanusMael/Bennewitz.Ninja.FileServer).

## License

MIT.

This package embeds [github-markdown-css](https://github.com/sindresorhus/github-markdown-css) by Sindre Sorhus for rendered-Markdown styling, used under its MIT licence. The notice travels inside the package as `THIRD-PARTY-NOTICES.md`.
