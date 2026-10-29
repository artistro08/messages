using System.Security.Cryptography;
using System.Text;

namespace GMessages.Protocol.Session;

/// <summary>
/// Holds the Google account cookies, stamps them on outgoing requests and absorbs rotated ones from responses.
/// </summary>
/// <remarks>
/// Mirrors the cookie methods of <c>AuthData</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// A jar that was never given a map ("no cookies", for QR-paired sessions) adds nothing to requests and ignores
/// <c>Set-Cookie</c>. Once set, a request gets a <c>Cookie</c> header, plus an <c>Authorization: SAPISIDHASH</c> header
/// when a <c>SAPISID</c> cookie exists. A response's <c>Set-Cookie</c> values overwrite cookies by name, which is how
/// Google rotates <c>SIDCC</c> and friends. All members are thread-safe. Cookie values are secrets and are never logged.
/// </remarks>
public sealed class CookieJar
{
    private readonly Lock _lock = new();
    private Dictionary<string, string>? _cookies;

    /// <summary>
    /// Raised after a response changed at least one cookie, so the owner can save the session again.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Whether a cookie map has been set (even an empty one).
    /// </summary>
    public bool HasCookies
    {
        get
        {
            lock (_lock)
            {
                return _cookies is not null;
            }
        }
    }

    /// <summary>
    /// Replaces all cookies with a copy of the given ones.
    /// </summary>
    /// <remarks>Does not raise <see cref="Changed"/>; this is the owner loading or resetting its own state.</remarks>
    /// <param name="cookies">Cookie names and values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cookies"/> is null.</exception>
    public void Set(IReadOnlyDictionary<string, string> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        lock (_lock)
        {
            _cookies = new Dictionary<string, string>(cookies);
        }
    }

    /// <summary>
    /// Copies the current cookies.
    /// </summary>
    /// <returns>A snapshot; empty when no cookies are set.</returns>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_lock)
        {
            return _cookies is null ? new Dictionary<string, string>() : new Dictionary<string, string>(_cookies);
        }
    }

    /// <summary>
    /// Adds the <c>Cookie</c> and (when <c>SAPISID</c> is present) <c>Authorization</c> headers to a request.
    /// </summary>
    /// <remarks>
    /// The Authorization hash depends on the time, so it must be applied for every attempt, not once per call.
    /// </remarks>
    /// <param name="req">The request to modify.</param>
    /// <param name="unixSeconds">The current time in Unix seconds, used for the SAPISIDHASH.</param>
    /// <param name="withAuthorization">False to send only the cookies, as a browser does outside Messages (cookie rotation).</param>
    /// <exception cref="ArgumentNullException"><paramref name="req"/> is null.</exception>
    public void ApplyTo(HttpRequestMessage req, long unixSeconds, bool withAuthorization = true)
    {
        ArgumentNullException.ThrowIfNull(req);

        string cookie_header;
        string? sapisid;
        lock (_lock)
        {
            if (_cookies is null)
            {
                return;
            }

            cookie_header = string.Join("; ", _cookies.Select(c => c.Key + "=" + c.Value));
            _cookies.TryGetValue("SAPISID", out sapisid);
        }

        // Cookie Header
        if (cookie_header.Length > 0)
        {
            req.Headers.TryAddWithoutValidation("Cookie", cookie_header);
        }

        // Authorization Header
        if (withAuthorization && sapisid is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", SapisidHash("https://messages.google.com", sapisid, unixSeconds));
        }
    }

    /// <summary>
    /// Applies the <c>Set-Cookie</c> headers of a response, overwriting cookies by name.
    /// </summary>
    /// <remarks>
    /// Only the name and value are used; attributes such as Path and Expires are ignored, as in Go. Malformed cookies
    /// are skipped. Does nothing when no cookie map is set. <see cref="Changed"/> is raised once, after the lock is
    /// released, if any value was added or changed.
    /// </remarks>
    /// <param name="res">The response to read.</param>
    /// <param name="accept">Keeps only the raw <c>Set-Cookie</c> lines it returns true for; null keeps all.</param>
    /// <exception cref="ArgumentNullException"><paramref name="res"/> is null.</exception>
    public void UpdateFrom(HttpResponseMessage res, Func<string, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(res);

        if (!res.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? lines))
        {
            return;
        }

        bool changed = false;
        lock (_lock)
        {
            if (_cookies is null)
            {
                return;
            }

            foreach (string line in lines)
            {
                if ((accept is not null && !accept(line)) || !TryParseSetCookie(line, out string name, out string value))
                {
                    continue;
                }

                if (!_cookies.TryGetValue(name, out string? old) || old != value)
                {
                    changed = true;
                }

                _cookies[name] = value;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Builds the Authorization header value Google's web app sends.
    /// </summary>
    /// <remarks>
    /// <c>SAPISIDHASH {ts}_{sha1hex}</c>, where the hash covers <c>"{ts} {sapisid} {origin}"</c>. SHA-1 is required by
    /// Google's protocol; it is not used for anything security-sensitive on our side.
    /// Ported from <c>SAPISIDHash</c> in
    /// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/http.go">http.go</see>.
    /// </remarks>
    /// <param name="origin">The origin string, for example <c>https://messages.google.com</c>.</param>
    /// <param name="sapisid">The SAPISID cookie value.</param>
    /// <param name="unixSeconds">The timestamp in Unix seconds.</param>
    /// <returns>The header value.</returns>
    internal static string SapisidHash(string origin, string sapisid, long unixSeconds)
    {
#pragma warning disable CA5350 // SHA-1 is mandated by Google's SAPISIDHASH scheme
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{unixSeconds} {sapisid} {origin}"));
#pragma warning restore CA5350

        return $"SAPISIDHASH {unixSeconds}_{Convert.ToHexStringLower(hash)}";
    }

    // Parse One Set-Cookie Line (Name=Value Before The First ';'), Like Go's readSetCookies
    private static bool TryParseSetCookie(string line, out string name, out string value)
    {
        name  = "";
        value = "";

        string pair = line.Split(';', 2)[0].Trim();
        int eq      = pair.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        name = pair[..eq].Trim();
        string raw = pair[(eq + 1)..];

        // Strip Surrounding Quotes
        if (raw.Length > 1 && raw[0] == '"' && raw[^1] == '"')
        {
            raw = raw[1..^1];
        }

        if (name.Length == 0 || !name.All(IsTokenChar) || !raw.All(IsValueChar))
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static bool IsTokenChar(char c) => c > ' ' && c < 0x7f && !"()<>@,;:\\\"/[]?={}".Contains(c);

    private static bool IsValueChar(char c) => c >= ' ' && c < 0x7f && c != '"' && c != ';' && c != '\\';
}
