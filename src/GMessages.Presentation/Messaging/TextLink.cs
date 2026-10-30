namespace GMessages.Presentation.Messaging;

/// <summary>
/// A web link inside a message's text, found by <see cref="LinkPreviewer.FindLinks"/>.
/// </summary>
/// <param name="Start">Where the link starts in the text.</param>
/// <param name="Length">How many characters of the text it covers (sentence punctuation after it is left out).</param>
/// <param name="Url">The <c>http</c> or <c>https</c> address it opens; a bare <c>www.</c> host opens over <c>https</c>.</param>
public readonly record struct TextLink(int Start, int Length, Uri Url);
