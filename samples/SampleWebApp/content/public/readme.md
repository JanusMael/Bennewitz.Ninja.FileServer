# Public files

This folder is served at `/files` with the component's own styling — the sample application
contributes no CSS to this page at all.

Things worth clicking:

- **View raw** serves this file's source instead of the rendered HTML.
- **Auto / Light / Dark** pins the colour scheme against your OS preference. The choice survives
  a reload, it themes the whole page rather than just the document body, and it re-colours the
  code below along with everything else.

## Fenced code is tokenised

```csharp
// Every mount is one call. Nothing else is required.
public static void Configure(WebApplication app, string root)
{
    const int MaxDepth = 32;

    app.MapFileServer("/docs", options =>
    {
        options.RootPath = root;
        options.RenderMarkdown = true;
        options.AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".csv"
        };
    })
    .RequireAuthorization("StaffOnly");
}
```

The same applies to other languages — XML:

```xml
<!-- Two dependencies, one dll -->
<ItemGroup>
  <PackageReference Include="Bennewitz.Ninja.FileServer" Version="2026.8.18" />
</ItemGroup>
```

and PowerShell:

```powershell
# Pack the component and run the sample against it
pwsh publish/Pack-Local.ps1
dotnet run --project samples/SampleWebApp
```

A language the tokeniser does not know keeps its text and loses only the colour:

```brainfuck
++++[>++++<-]>[>+>+<<-]
```

## Tables and the rest of GitHub's Markdown

| Column | Meaning |
| ------ | ------- |
| Name   | File or directory |
| Size   | Bytes, human-readable |
| Modified | Last write time |
