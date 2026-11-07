using System.Net;

namespace GMessages.Protocol.Tests.Fakes;

/// <summary>
/// A request as the fake handler saw it.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Url">The absolute URL.</param>
/// <param name="Headers">The request and content headers, case-insensitive. The dictionary does not include Content-Length.</param>
/// <param name="Body">The request body bytes. The array is empty when the request has no body.</param>
/// <param name="At">The request time from the handler clock.</param>
internal sealed record RecordedRequest(HttpMethod Method, string Url, Dictionary<string, string> Headers, byte[] Body, DateTimeOffset At);

/// <summary>
/// An <see cref="HttpMessageHandler"/> that records every request and answers from a script.
/// </summary>
/// <remarks>
/// <see cref="Respond"/> receives the zero-based request index and the recorded request. A test can script
/// "503, 503, 200" or branch on the URL.
/// </remarks>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    /// <summary>Gets the requests received so far. Lock the list to read it while requests can still arrive.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Gets or sets the function that builds the answer for a request. The default answer is an empty 200.</summary>
    public Func<int, RecordedRequest, HttpResponseMessage> Respond { get; set; } = (_, _) => Response(HttpStatusCode.OK);

    /// <summary>Gets or sets a filter. The handler records a matching request but never answers it. The request waits until canceled, like a dead socket.</summary>
    public Func<RecordedRequest, bool>? Hang { get; set; }

    /// <summary>Gets or sets the clock that sets <see cref="RecordedRequest.At"/>.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    /// Builds a canned response.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <param name="contentType">The Content-Type value, or null for none.</param>
    /// <param name="body">The body bytes, or null for empty.</param>
    /// <param name="setCookies">The raw <c>Set-Cookie</c> header values.</param>
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
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // NonValidated keeps the raw header strings, as they go on the wire.
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

        var recorded = new RecordedRequest(request.Method, request.RequestUri!.AbsoluteUri, headers, body, Time.GetUtcNow());
        int index;
        lock (Requests)
        {
            index = Requests.Count;
            Requests.Add(recorded);
        }

        if (Hang?.Invoke(recorded) == true)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return Respond(index, recorded);
    }
}
