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
    /// is served. Compared case-insensitively; the leading dot is optional and normalised when
    /// the mount is registered, so <c>pdf</c> and <c>.pdf</c> are equivalent. An empty string
    /// matches files with no extension.
    /// </summary>
    /// <remarks>
    /// Normalisation replaces this set rather than editing it, so the instance you assign is
    /// never modified. Matching compares against <see cref="Path.GetExtension(string)"/>, which
    /// always returns the dotted form — a dotless entry would otherwise match nothing and the
    /// mount would serve nothing at all, silently.
    /// </remarks>
    public IReadOnlySet<string> AllowedExtensions { get; set; } =
        new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <paramref name="extensions"/> as a case-insensitive set in the dotted form that
    /// <see cref="Path.GetExtension(string)"/> produces: entries are trimmed, a missing leading
    /// dot is added, and an empty entry is preserved as the marker for files with no extension.
    /// </summary>
    /// <remarks>
    /// The single normaliser for the whole product. Both the library's registration path and the
    /// CLI's configuration binding call it, so the two surfaces cannot drift into disagreeing
    /// about whether a dot is required — which is exactly what they had done.
    /// <para>
    /// Public rather than internal so the CLI can reach it without internals access: that host
    /// deliberately consumes only what a package consumer can, which is what keeps the package
    /// honest. It is also useful directly when validating configuration before assigning it,
    /// though a mount normalises whatever it is given.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> NormaliseExtensions(IEnumerable<string>? extensions)
    {
        if (extensions is null)
            return new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);

        var normalised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var extension in extensions)
        {
            var trimmed = extension.Trim();

            // The empty entry means "no extension", which is what Path.GetExtension returns for
            // such a file. Prepending a dot here would quietly delete the only way to list them.
            normalised.Add(trimmed.Length == 0 || trimmed.StartsWith('.') ? trimmed : '.' + trimmed);
        }

        return normalised;
    }

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
    /// Whether the component emits its own presentation: a link to its stylesheet, and on
    /// Markdown pages the colour-scheme toggle that overrides it. Default: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// The component never depends on the host providing CSS. Its stylesheet is self-contained,
    /// served from this mount's own asset endpoint, and referenced from within the page body
    /// rather than a layout section — so it applies identically whether the host supplies a
    /// layout or not, and whether that layout brings a CSS framework or nothing at all. Class
    /// names are prefixed so they cannot collide with a host's own framework.
    /// <para>
    /// Set to <c>false</c> only to style the file browser yourself: the markup keeps its
    /// prefixed class names, and neither the stylesheet link nor the toggle is emitted — the
    /// toggle goes with the stylesheets because overriding them is its only purpose.
    /// </para>
    /// </remarks>
    public bool IncludeDefaultStyles { get; set; } = true;

    /// <summary>
    /// Value sent in the <c>Cache-Control</c> response header for served files.
    /// Default: <c>no-store</c>.
    /// </summary>
    public string CacheControl { get; set; } = "no-store";
}
