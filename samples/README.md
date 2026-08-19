# Samples

## SampleWebApp

An ASP.NET Core application that installs `Bennewitz.Ninja.FileServer` **from a package** and
mounts it four times, so the component is exercised the way a consumer gets it rather than
through a project reference that would let the compiler see types the package might not ship.

### Running it

```sh
# 1. Pack the component into publish/local-feed (gitignored)
pwsh publish/Pack-Local.ps1

# 2. Run the sample — it restores the newest local prerelease from that feed
dotnet run --project samples/SampleWebApp
```

Then open <http://localhost:5000>. Iterating on the component means re-running step 1: each pack
gets a fresh version, and the sample's floating `*-*` reference picks it up.

To check the sample against a published package instead:

```sh
dotnet run --project samples/SampleWebApp -p:FileServerVersion=2026.8.18
```

### What each mount demonstrates

| Route | Configuration | What to look at |
| --- | --- | --- |
| `/files` | defaults | The component's own styling, in an app that contributes no CSS to that page. Open `readme.md`: four fenced blocks, three of them tokenised and one in a language the tokeniser does not know. Cycle **Auto / Light / Dark** and watch the code re-colour with the page — then go back to the listing, which carries the same control and keeps the scheme you picked. |
| `/docs` | `LayoutPath` | The same browser rendered inside this app's layout — the dark header is the host's, everything below it is the package's. |
| `/reports` | `AllowedExtensions` | `notes.txt` is on disk but absent from the listing, and `/reports/notes.txt` is refused. The filter governs downloads, not just display. |
| `/private` | `RequireAuthorization()` + `LayoutPath` | Request `/private/salaries.csv` while signed out. The challenge lands on the *file*, and signing in returns you to it. |

### The two questions the sample answers

**Do several mounts really run side by side?** All four are live at once, each with its own root,
filter, layout, and policy, and none of them shares state with the others. `shared.txt` exists in
two mounts with different contents — `/files/shared.txt` and `/docs/shared.txt` serve their own
copy — while `/files/index.md` is a 404 because that document belongs to `/docs`. A mount reaches
inside its own root and nowhere else.

**Does authorization actually cover downloads?** Files are served from endpoints rather than
static-file middleware, so a convention on the route group applies to them. Signed out, every URL
under `/private` redirects to the login page — the listing, the rendered Markdown, and the CSV
alike. The one thing that stays anonymous is the component's stylesheet endpoint, which is mapped
outside the group on purpose so the login page you are redirected to is still styled.

### Sign-in

Any name is accepted. The login page exists to show the challenge-and-return round trip, not to
model authentication.
