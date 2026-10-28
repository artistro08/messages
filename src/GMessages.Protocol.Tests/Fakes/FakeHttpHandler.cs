using System.Net;

namespace GMessages.Protocol.Tests.Fakes;

/// <summary>
/// A request as the fake handler saw it.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Url">The absolute URL.</param>
/// <param name="Headers">Request and content headers set by the code under test, case-insensitive. Content-Length is left out.</param>
/// <param name="Body">The request body bytes (empty when none).</param>
/// <param name="At">The time of the request according to the handler's clock.</param>
internal sealed record RecordedRequest(HttpMethod Method, string Url, Dictionary<string, string> Headers, byte[] Body, DateTimeOffset At);

/// <summary>
/// An <see cref="HttpMessageHandler"/> that records every request and answers from a script.
/// </summary>
/// <remarks>
/// Test-only. <see cref="Respond"/> gets the zero-based request index and the recorded request, so a test can script
/// "503, 503, 200" or branch on the URL.
/// </remarks>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    /// <summary>Requests received so far; lock on the list when reading it while requests may still arrive.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Builds the answer for a request; defaults to an empty 200.</summary>
    public Func<int, RecordedRequest, HttpResponseMessage> Respond { get; set; } = (_, _) => Response(HttpStatusCode.OK);

    /// <summary>The clock stamped on <see cref="RecordedRequest.At"/>.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    /// Builds a canned response.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <param name="contentType">The Content-Type value, or null for none.</param>
    /// <param name="body">The body bytes, or null for empty.</param>
    /// <param name="setCookies">Raw <c>Set-Cookie</c> header values.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Response(HttpStatusCode status, string? contentType = null, byte[]? body = null, params string[] setCookies)
    {
        var res = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
        if (contentType is not null)
        {
            res.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        foreach (string cookie in setCookies)
        {
            res.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        }

        return res;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Flatten Headers
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // NonValidated keeps the raw strings the code under test set, as they go on the wire
        foreach (var h in request.Headers.NonValidated)
        {
            headers[h.Key] = h.Value.ToString();
        }

        byte[] body = [];
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers.NonValidated.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                headers[h.Key] = h.Value.ToString();
            }

            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        // Record And Answer
        var recorded = new RecordedRequest(request.Method, request.RequestUri!.AbsoluteUri, headers, body, Time.GetUtcNow());
        int index;
        lock (Requests)
        {
            index = Requests.Count;
            Requests.Add(recorded);
        }

        return Respond(index, recorded);
    }
}
