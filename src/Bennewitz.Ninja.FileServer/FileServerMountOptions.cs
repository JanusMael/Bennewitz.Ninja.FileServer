namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Per-mount configuration for a file-server route registered with
/// <c>MapFileServer</c>. Each mount owns its own instance; nothing is shared between mounts,
/// so a host may serve any number of directories with independent filters, layouts, and
/// authorization policies.
/// </summary>
/// <remarks>
/// Deliberately not named <c>FileServerOptions</c>: ASP.NET Core already defines
/// <see cref="Microsoft.AspNetCore.Builder.FileServerOptions"/>, and
/// <c>Microsoft.AspNetCore.Builder</c> is an implicit using in the Web SDK. Sharing the name
/// would make the type ambiguous in essentially every consuming project.
/// </remarks>
public sealed class FileServerMountOptions
{
    /// <summary>
    /// Absolute path to the directory whose contents are served. Must be fully qualified
    /// (e.g. <c>/srv/files</c> or <c>C:\Share</c>) and must exist when the mount is registered.
    /// Validated by <c>MapFileServer</c>, which fails at startup rather than at request time.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// File extensions that may be listed and downloaded. When empty (the default) every file
    /// is served. Compared case-insensitively; the leading dot is expected (<c>.pdf</c>).
    /// An empty string matches files with no extension.
    /// </summary>
    public IReadOnlySet<string> AllowedExtensions { get; set; } =
        new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether directory contents may be browsed. When <c>false</c>, the mount root and any
    /// directory path return 404 while individual file downloads still succeed.
    /// Default: <c>true</c>.
    /// </summary>
    public bool EnableDirectoryBrowsing { get; set; } = true;

    /// <summary>
    /// Whether Markdown files are rendered as HTML. When <c>false</c>, <c>.md</c> files are
    /// served as raw bytes like any other file. Default: <c>true</c>.
    /// </summary>
    public bool RenderMarkdown { get; set; } = true;

    /// <summary>
    /// Application-relative path to the layout used for this mount's pages, for example
    /// <c>/Views/Shared/_Layout.cshtml</c> or <c>/Pages/Shared/_Layout.cshtml</c>. When
    /// <c>null</c> (the default) the component's own self-contained layout is used, which
    /// produces the same appearance as the standalone server.
    /// </summary>
    /// <remarks>
    /// A host layout must not rely on the component declaring Razor sections — the component
    /// never does, because an unrendered declared section throws at request time.
    /// </remarks>
    public string? LayoutPath { get; set; }

    /// <summary>
    /// Whether the component emits a link to its own stylesheet. Default: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// The component never depends on the host providing CSS. Its stylesheet is self-contained,
    /// served from this mount's own asset endpoint, and referenced from within the page body
    /// rather than a layout section — so it applies identically whether the host supplies a
    /// layout or not, and whether that layout brings a CSS framework or nothing at all. Class
    /// names are prefixed so they cannot collide with a host's own framework.
    /// <para>
    /// Set to <c>false</c> only to style the file browser yourself: the markup keeps its
    /// prefixed class names and no stylesheet link is emitted.
    /// </para>
    /// </remarks>
    public bool IncludeDefaultStyles { get; set; } = true;

    /// <summary>
    /// Value sent in the <c>Cache-Control</c> response header for served files.
    /// Default: <c>no-store</c>.
    /// </summary>
    public string CacheControl { get; set; } = "no-store";
}
