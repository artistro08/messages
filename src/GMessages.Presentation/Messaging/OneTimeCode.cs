using System.Text.RegularExpressions;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Finds the one-time passcode in a received message ("Your verification code is 482913"), so it can be copied for you.
/// </summary>
/// <remarks>
/// A message counts only when it says what it is (code, passcode, OTP, PIN, verification, verify, one-time, 2FA, sign-in or
/// login), so a phone number, an amount or an order number in an ordinary text is never taken. The code is the first
/// group of 4 to 8 digits standing on its own; "123-456" and "123 456" are read as 123456.
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

        if (!Keyword().IsMatch(text) || Code().Match(text) is not { Success: true } match)
        {
            return null;
        }

        return match.Value.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\b(?:code|passcode|otp|pin|verification|verify|one[- ]time|2fa|sign[- ]?in|log[- ]?in)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Keyword();

    // Digits Not Touching Other Digits, Letters Or A Decimal Point: 3+3 Grouped, Else 4 To 8 In A Row
    [GeneratedRegex(@"(?<![\w.,])(?:\d{3}[- ]\d{3}|\d{4,8})(?![\w]|[.,]\d)")]
    private static partial Regex Code();
}
