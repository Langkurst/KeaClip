namespace Klippi.Desktop;

/// <summary>A finished download or upload worth telling the user about: "Download finished" / the title, "Link ready" / the file.</summary>
public sealed record CompletionNote(string Heading, string Detail);
