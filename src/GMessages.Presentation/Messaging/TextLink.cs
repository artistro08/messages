namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds a web link in the text of a message, as <see cref="LinkPreviewer.FindLinks"/> finds it.
/// </summary>
/// <param name="Start">The index in the text where the link starts.</param>
/// <param name="Length">The number of characters that the link covers, without the punctuation after it.</param>
/// <param name="Url">
/// The <c>http</c> or <c>https</c> address that the link opens. A bare <c>www.</c> host opens over <c>https</c>.
/// </param>
public readonly record struct TextLink(int Start, int Length, Uri Url);
