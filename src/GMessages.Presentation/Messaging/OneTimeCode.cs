using System.Text.RegularExpressions;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Finds the one-time passcode in a received message, so the app can copy it. An example is "Your verification code
/// is 482913".
/// </summary>
/// <remarks>
/// A message counts only when it says what it is: code, passcode, OTP, verification, verify, one-time or 2FA. Thus the
/// method never takes a phone number, an amount or an order number in an ordinary text. The code is the group of 4 to
/// 8 ASCII digits, on its own, nearest to one of those words. Thus an account number or an amount earlier in the
/// message does not win. The method reads "123-456" and "123 456" as 123456.
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

        Match? code = Code().Matches(text).MinBy(m => keywords.Min(k => Math.Abs(m.Index - k)));
        return code is null ? null : string.Concat(code.Value.Where(char.IsAsciiDigit));
    }

    [GeneratedRegex(@"\b(?:code|passcode|otp|verification|verify|one[- ]time|2fa)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Keyword();

    // The pattern takes ASCII digits that touch no other digit, letter, decimal point or currency sign. They also
    // touch no slash of a date. A number and a dash or space must not come before them. That case is the last part of
    // a phone number. The digits are 2 groups of 3, else 4 to 8 in a row.
    [GeneratedRegex(@"(?<![\w.,/$€£]|\d[- ])(?:[0-9]{3}[- ][0-9]{3}|[0-9]{4,8})(?![\w/]|[.,][0-9])")]
    private static partial Regex Code();
}
