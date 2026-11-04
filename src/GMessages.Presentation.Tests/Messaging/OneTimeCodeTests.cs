using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class OneTimeCodeTests
{
    /// <summary>
    /// A message that says it carries a code gives the code's digits (the number nearest the word); any other message
    /// gives none.
    /// </summary>
    [TestMethod]
    [DataRow("Your verification code is 482913", "482913")]
    [DataRow("G-482913 is your Google verification code.", "482913")]
    [DataRow("Use 4829 as your one-time passcode", "4829")]
    [DataRow("Your Bank login code: 123-456. Don't share it.", "123456")]
    [DataRow("Your OTP is 12345678", "12345678")]
    [DataRow("Chase acct 98765432: your verification code is 482913", "482913")]
    [DataRow("Call 555-1234 if you didn't request this code 482913", "482913")]
    [DataRow("$1234 charge, code 482913", "482913")]
    [DataRow("See you at 5, call me at 555-1234", null)]
    [DataRow("Call 555 1234 to verify", null)]
    [DataRow("Code expires 11/03/2026", null)]
    [DataRow("Can you pin the address? 1234 Main St", null)]
    [DataRow("Your order 48291 has shipped", null)]
    [DataRow("Your code is ready", null)]
    [DataRow("Your code costs $1,234.56", null)]
    [DataRow("Code 123456789 is too long", null)]
    [DataRow("رمز ٤٨٢٩١٣ code", null)]
    public void Find(string text, string? expected) => Assert.AreEqual(expected, OneTimeCode.Find(text));
}
