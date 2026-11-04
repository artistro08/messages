using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class OneTimeCodeTests
{
    /// <summary>
    /// A message that says it carries a code gives the code's digits; any other message gives none.
    /// </summary>
    [TestMethod]
    [DataRow("Your verification code is 482913", "482913")]
    [DataRow("G-482913 is your Google verification code.", "482913")]
    [DataRow("Use 4829 as your one-time passcode", "4829")]
    [DataRow("Your Bank login code: 123-456. Don't share it.", "123456")]
    [DataRow("Your OTP is 12345678", "12345678")]
    [DataRow("PIN 0042", "0042")]
    [DataRow("See you at 5, call me at 555-1234", null)]
    [DataRow("Your order 48291 has shipped", null)]
    [DataRow("Your code is ready", null)]
    [DataRow("Your code costs $1,234.56", null)]
    [DataRow("Code 123456789 is too long", null)]
    public void Find(string text, string? expected) => Assert.AreEqual(expected, OneTimeCode.Find(text));
}
