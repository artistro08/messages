namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds the preview card of a link. <see cref="LinkPreviewer"/> fetches it and keeps it only in memory.
/// </summary>
/// <param name="Url">The link that the card opens.</param>
/// <param name="Title">The title of the page as plain text: its <c>og:title</c>, else its <c>&lt;title&gt;</c>.</param>
/// <param name="Site">The real host of the page after redirects, without <c>www.</c>.</param>
/// <param name="Image">The bytes of the card image from its <c>og:image</c>, or null for none.</param>
public sealed record LinkPreview(Uri Url, string Title, string Site, byte[]? Image);
