namespace GMessages.Presentation.Messaging;

/// <summary>
/// A link's preview card, fetched by <see cref="LinkPreviewer"/> and kept in memory only.
/// </summary>
/// <param name="Url">The link the card opens.</param>
/// <param name="Title">The page title (its <c>og:title</c>, else its <c>&lt;title&gt;</c>), as plain text.</param>
/// <param name="Site">The page's real host (after redirects) without <c>www.</c>.</param>
/// <param name="Image">The card image's bytes (its <c>og:image</c>), or null for none.</param>
public sealed record LinkPreview(Uri Url, string Title, string Site, byte[]? Image);
