# Public files

This folder is served at `/files` with the component's own styling — the sample application
contributes no CSS to this page at all.

Things worth clicking:

- **View raw** serves this file's source instead of the rendered HTML.
- **Auto / Light / Dark** pins the colour scheme against your OS preference. The choice survives
  a reload, and it themes the whole page rather than just the document body.

| Column | Meaning |
| ------ | ------- |
| Name   | File or directory |
| Size   | Bytes, human-readable |
| Modified | Last write time |

```csharp
app.MapFileServer("/files", options => options.RootPath = "/srv/public");
```
