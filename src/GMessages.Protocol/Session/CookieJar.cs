using System.Security.Cryptography;
using System.Text;

namespace GMessages.Protocol.Session;

/// <summary>
/// Holds the Google account cookies. It adds them to outgoing requests and takes the rotated cookies from responses.
/// </summary>
/// <remarks>
/// Ported from the cookie methods of <c>AuthData</c> in
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/client.go">client.go</see>.
/// A jar with no map means "no cookies", as for QR-paired sessions. It adds nothing to requests and ignores
/// <c>Set-Cookie</c>. After a set, each request gets a <c>Cookie</c> header. When a <c>SAPISID</c> cookie exists, the
/// request also gets an <c>Authorization: SAPISIDHASH</c> header. The <c>Set-Cookie</c> values of a response replace
/// cookies by name. Google rotates <c>SIDCC</c> and other cookies in this way. All members are thread-safe. Cookie
/// values are secrets and never go in a log.
/// </remarks>
public sealed class CookieJar
{
    private readonly Lock _lock = new();
    private Dictionary<string, string>? _cookies;

    /// <summary>
    /// Occurs after a response changes 1 or more cookies, so the owner can save the session again.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets whether a cookie map is set. An empty map also counts.
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
    /// <remarks>The method does not raise <see cref="Changed"/>, because the owner loads or resets its own state.</remarks>
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
    /// <returns>A snapshot. It is empty when no cookies are set.</returns>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_lock)
        {
            return _cookies is null ? [] : new Dictionary<string, string>(_cookies);
        }
    }

    /// <summary>
    /// Adds the <c>Cookie</c> header to a request. When <c>SAPISID</c> exists, it also adds the <c>Authorization</c> header.
    /// </summary>
    /// <remarks>
    /// The Authorization hash changes with the time. Thus apply it for each attempt, not 1 time for each call.
    /// </remarks>
    /// <param name="req">The request to change.</param>
    /// <param name="unixSeconds">The current time in Unix seconds for the SAPISIDHASH.</param>
    /// <exception cref="ArgumentNullException"><paramref name="req"/> is null.</exception>
    public void ApplyTo(HttpRequestMessage req, long unixSeconds)
    {
        ArgumentNullException.ThrowIfNull(req);

        string cookieHeader;
        string? sapisid;
        lock (_lock)
        {
            if (_cookies is null)
            {
                return;
            }

            cookieHeader = string.Join("; ", _cookies.Select(c => c.Key + "=" + c.Value));
            _cookies.TryGetValue("SAPISID", out sapisid);
        }

        if (cookieHeader.Length > 0)
        {
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (sapisid is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", SapisidHash("https://messages.google.com", sapisid, unixSeconds));
        }
    }

    /// <summary>
    /// Applies the <c>Set-Cookie</c> headers of a response. They replace cookies by name.
    /// </summary>
    /// <remarks>
    /// As in Go, the method uses only the name and the value. It ignores attributes such as Path and Expires. It skips
    /// malformed cookies. It does nothing when no cookie map is set. When a value is new or changed,
    /// <see cref="Changed"/> occurs 1 time, after the method releases the lock.
    /// </remarks>
    /// <param name="res">The response to read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="res"/> is null.</exception>
    public void UpdateFrom(HttpResponseMessage res)
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
                if (!TryParseSetCookie(line, out string name, out string value))
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
    /// Builds the Authorization header value that the Google web app sends.
    /// </summary>
    /// <remarks>
    /// The value is <c>SAPISIDHASH {ts}_{sha1hex}</c>. The hash covers <c>"{ts} {sapisid} {origin}"</c>. The Google
    /// protocol requires SHA-1 here. This app does not choose the hash. Ported from <c>SAPISIDHash</c> in
    /// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/http.go">http.go</see>.
    /// </remarks>
    /// <param name="origin">The origin string, for example <c>https://messages.google.com</c>.</param>
    /// <param name="sapisid">The SAPISID cookie value.</param>
    /// <param name="unixSeconds">The timestamp in Unix seconds.</param>
    /// <returns>The header value.</returns>
    internal static string SapisidHash(string origin, string sapisid, long unixSeconds)
    {
#pragma warning disable CA5350 // The Google SAPISIDHASH scheme requires SHA-1.
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{unixSeconds} {sapisid} {origin}"));
#pragma warning restore CA5350

        return $"SAPISIDHASH {unixSeconds}_{Convert.ToHexStringLower(hash)}";
    }

    // As in Go's readSetCookies, the method reads name=value before the first ';' of 1 Set-Cookie line.
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
