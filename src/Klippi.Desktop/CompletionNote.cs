namespace Klippi.Desktop;

/// <summary>A finished download or upload worth telling the user about: "Download finished" / the title, "Link ready" / the file.
/// An update notice also carries a link ("See what's new").</summary>
public sealed record CompletionNote(string Heading, string Detail, Uri? Link = null, string? LinkText = null)
{
    public bool HasLink => Link is not null;
}
