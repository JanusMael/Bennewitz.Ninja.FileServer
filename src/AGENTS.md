# AGENTS.md — `src/`

Two projects. The CLI is a thin host over the component's public API.

| Project | Assembly | What it is |
|---|---|---|
| `Bennewitz.Ninja.FileServer` | `Bennewitz.Ninja.FileServer.Hosting` | The component: a Razor class library packed as `Bennewitz.Ninja.FileServer`. `README.md` here is the package's readme |
| `Bennewitz.Ninja.FileServer.Cli` | `Bennewitz.Ninja.FileServer` | The standalone server: `Settings` reads `settings.json`, `FILE_SERVER_*` and flags, and `Program` makes one `MapFileServer` call. Not packable |

## Rules

| Rule | Why | Guarded by |
|---|---|---|
| **`Handle` checks in order: `TryResolve`, then `Policy.IsRefused`, then the directory branch, then `IsAllowed`** | A sensitive directory must be refused as a listing too, and a file served by physical path bypasses `ListingFileProvider` | `SensitivePathTests`; `MountRequestTests.Mount_AllowedExtensions_HidesAndRefusesFilteredFiles` |
| `FileServerMount` builds its `PhysicalFileProvider` with `ExclusionFilters.None` | The provider's own filter governs listings only and cannot know about exposure | `SensitivePathTests.EveryFileIsListedExactlyWhenItIsServed` |
| `MapFileServer` normalises options by replacing each collection, never editing the caller's | A caller reusing one set for several mounts would see it change | `Mount_AllowedExtensions_DoesNotMutateTheCallersSet`; `MapFileServer_DoesNotModifyTheCallersPatternLists` |
| A pattern that is rooted or has a `..` segment is refused at registration, naming its option | A pattern could otherwise reach outside the mount root | `GlobPatterns.Normalise`; `GlobPatternTests` |
| `wwwroot/` is an `EmbeddedResource` read through `ManifestEmbeddedFileProvider`; `StaticWebAssetsEnabled` stays `false` | The host needs no `UseStaticFiles`, and a single-file publish has no loose files | `MountRequestTests.AssetEndpoint_ServesEmbeddedAssetsWithImmutableCaching` |
| `FileServerAssets.TryOpen` refuses `..`, a leading `/` and `\` | The asset route names a fixed set of files and must not navigate | `AssetEndpoint_PathThatNavigatesRatherThanNames_IsRefused` |
| `AddFileServer` registers the compiled views as a `CompiledRazorAssemblyPart` | Discovery by dependency context has been fragile and breaks first under trimming | every rendering test in `MountRequestTests` |
| `AddRazorSupportForMvc` stays `true` | `Sdk.Razor` will not compile MVC `.cshtml` views without it | the build |
| Every class in `fileserver.css` is prefixed `bnfs-` | The pages must neither need nor collide with a host's CSS | `wwwroot/css/fileserver.css` |
| `MarkdownSyntaxHighlighter` emits GitHub's token classes, never ColorCode's | The colour-scheme toggle governs code through `github-markdown.min.css` | `MarkdownHighlightingTests.FencedCSharp_DoesNotEmitColorCodesOwnClassNames` |
| `Settings` resolves `settings.json` from `AppContext.BaseDirectory`, never `Process.MainModule` | Under `dotnet App.dll` the main module is the shared host, so a mounted `/app/settings.json` was ignored | `Settings.cs` |
| A startup failure exits with an `ExitCode`: configuration `2`, missing directory or certificate `3` | An orchestrator tells a config error, which a restart cannot fix, from a crash | `Program.ClassifyStartupException` |
| The CLI has no tests of its own | Its behaviour is the component's; anything it needs must be public API | `CONTRIBUTING.md`, "Tests" |
