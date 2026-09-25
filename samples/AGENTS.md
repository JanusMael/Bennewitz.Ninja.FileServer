# AGENTS.md — `samples/`

A host that installs the component the way a consumer does. It is not in
`Bennewitz.Ninja.FileServer.sln`. How to run it, and what each mount demonstrates, is in
`README.md` here.

| Path | What it is |
|---|---|
| `SampleWebApp/` | An ASP.NET Core app mounting the component at `/files`, `/docs`, `/reports` and `/private` |
| `SampleWebApp/content/` | The four mounts' roots, including `shared.txt` in two of them with different contents |
| `nuget.config` | Adds `../publish/local-feed` as the source `bnfs-local`, for `samples/` only, and maps `Bennewitz.Ninja.FileServer` to both it and nuget.org, and `*` to nuget.org |
| `README.md` | Running the sample, and what to look at under each mount |

## Rules

| Rule | Why | Guarded by |
|---|---|---|
| **The component is a `PackageReference`, never a `ProjectReference`** | A project reference lets the compiler see types the package might not ship | `SampleWebApp.csproj` |
| The reference's version is `$(FileServerVersion)`, never a literal | A literal leaves `-p:FileServerVersion=…` silently ignored | `SampleWebApp.csproj` |
| The `FileServerVersion` default names a version already on nuget.org, bumped only after a release is live | A fresh clone restores a package that does not exist yet | `CONTRIBUTING.md`, "Releasing", step 4 |
| The local feed is configured here, not at the root | It cannot affect the library or the CLI build | `nuget.config` |
| `nuget.config` restates `*` → nuget.org beside its own mappings | It maps a pattern under the `nuget.org` key, and a child's patterns for a source key replace the root `NuGet.config`'s for that key; without `*`, restore fails `NU1100` on every other package | restoring the sample |
| Each mount keeps demonstrating one claim: default styling, a host `LayoutPath`, `AllowedExtensions`, `RequireAuthorization()` | The sample is how those claims are checked against a real package | `README.md`, "What each mount demonstrates" |
| `LoginModel` signs in any name, and returns only to a local `returnUrl` | It shows the challenge-and-return round trip, not authentication; an absolute `returnUrl` would make it an open redirect | `SampleWebApp/Pages/Login.cshtml.cs`, `Url.IsLocalUrl` |
