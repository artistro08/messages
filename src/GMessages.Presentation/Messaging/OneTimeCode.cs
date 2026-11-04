using System.Text.RegularExpressions;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Finds the one-time passcode in a received message ("Your verification code is 482913"), so it can be copied for you.
/// </summary>
/// <remarks>
/// A message counts only when it says what it is (code, passcode, OTP, verification, verify, one-time or 2FA), so a
/// phone number, an amount or an order number in an ordinary text is never taken. The code is the group of 4 to 8 ASCII
/// digits standing on its own that sits closest to one of those words, so an account number or an amount earlier in the
/// message doesn't win; "123-456" and "123 456" are read as 123456.
/// </remarks>
public static partial class OneTimeCode
{
    /// <summary>
    /// Gives the message's one-time passcode.
    /// </summary>
    /// <param name="text">The message text.</param>
    /// <returns>The code's digits, or null when the message has none.</returns>
    public static string? Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int[] keywords = [.. Keyword().Matches(text).Select(k => k.Index)];
        if (keywords.Length == 0)
        {
            return null;
        }

        // The Number Nearest A Keyword
        Match? code = Code().Matches(text).MinBy(m => keywords.Min(k => Math.Abs(m.Index - k)));
        return code is null ? null : string.Concat(code.Value.Where(char.IsAsciiDigit));
    }

    [GeneratedRegex(@"\b(?:code|passcode|otp|verification|verify|one[- ]time|2fa)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Keyword();

    // ASCII Digits Not Touching Other Digits, Letters, A Decimal Point, A Currency Sign, A Date's Slash, Or A Number Before
    // A Dash Or Space (A Phone Number's Last Part): 3+3 Grouped, Else 4 To 8 In A Row
    [GeneratedRegex(@"(?<![\w.,/$€£]|\d[- ])(?:[0-9]{3}[- ][0-9]{3}|[0-9]{4,8})(?![\w/]|[.,][0-9])")]
    private static partial Regex Code();
}
