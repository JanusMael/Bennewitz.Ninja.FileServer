using Microsoft.AspNetCore.Html;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// One row in a directory listing.
/// </summary>
/// <param name="Name">File or directory name, unencoded.</param>
/// <param name="Url">Absolute URL for the entry, already <c>PathBase</c>-aware and encoded.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="Length">Size in bytes; <c>null</c> for directories.</param>
/// <param name="LastModified">Last write time, or <c>null</c> when unavailable.</param>
public sealed record FileServerEntry(
    string Name,
    string Url,
    bool IsDirectory,
    long? Length,
    DateTimeOffset? LastModified);

/// <summary>
/// One segment of the breadcrumb trail above a listing or document.
/// </summary>
/// <param name="Name">Display text for the segment.</param>
/// <param name="Url">Absolute URL the segment links to.</param>
public sealed record FileServerCrumb(string Name, string Url);

/// <summary>
/// Shared view state: everything a view needs that is not specific to listings or documents.
/// </summary>
public abstract class FileServerViewModel
{
    /// <summary>Title for the page.</summary>
    public required string Title { get; init; }

    /// <summary>Breadcrumb trail from the mount root to the current location.</summary>
    public required IReadOnlyList<FileServerCrumb> Breadcrumbs { get; init; }

    /// <summary>
    /// URL of the component's stylesheet, or <c>null</c> when the host has opted out via
    /// <see cref="FileServerMountOptions.IncludeDefaultStyles"/>. Views reference it from the
    /// page body rather than a layout section, so the component styles itself correctly under
    /// a host layout that provides no CSS — and under one that renders no sections at all.
    /// </summary>
    public string? StylesheetUrl { get; init; }

    /// <summary>
    /// URL of the script backing the colour-scheme toggle, or <c>null</c> when the host has
    /// opted out via <see cref="FileServerMountOptions.IncludeDefaultStyles"/> — the toggle
    /// exists to override the component's own stylesheets, so it goes when they do.
    /// </summary>
    /// <remarks>
    /// Present on listings as well as documents: the choice is remembered under one key, so a
    /// scheme pinned on either is honoured by both rather than changing as you navigate.
    /// </remarks>
    public string? ScriptUrl { get; init; }
}

/// <summary>
/// View state for a directory listing.
/// </summary>
public sealed class FileServerDirectoryViewModel : FileServerViewModel
{
    /// <summary>Entries in the directory, directories first then files, each alphabetical.</summary>
    public required IReadOnlyList<FileServerEntry> Entries { get; init; }

    /// <summary>URL of the parent directory, or <c>null</c> at the mount root.</summary>
    public string? ParentUrl { get; init; }
}

/// <summary>
/// View state for a rendered Markdown document.
/// </summary>
public sealed class FileServerMarkdownViewModel : FileServerViewModel
{
    /// <summary>The document rendered to HTML.</summary>
    public required HtmlString Content { get; init; }

    /// <summary>Name of the source file.</summary>
    public required string FileName { get; init; }

    /// <summary>URL serving the unrendered source.</summary>
    public required string RawUrl { get; init; }

    /// <summary>URL of the stylesheet used for rendered Markdown, when styles are enabled.</summary>
    public string? MarkdownStylesheetUrl { get; init; }
}
