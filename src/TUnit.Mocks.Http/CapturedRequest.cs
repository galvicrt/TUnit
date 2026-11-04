using System.Text;

namespace TUnit.Mocks.Http;

/// <summary>
/// A captured HTTP request with its body content.
/// </summary>
public sealed class CapturedRequest
{
    /// <summary>The HTTP method of the request.</summary>
    public HttpMethod Method { get; }

    /// <summary>The request URI.</summary>
    public Uri? RequestUri { get; }

    /// <summary>The request body content as a string, or null if no body.</summary>
    public string? Body { get; }

    /// <summary>The request headers.</summary>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

    /// <summary>Whether this request matched a setup.</summary>
    public bool Matched
    {
        get => Volatile.Read(ref _matched);
        internal set => Volatile.Write(ref _matched, value);
    }
    private bool _matched;

    /// <summary>The timestamp when this request was captured.</summary>
    public DateTimeOffset Timestamp { get; }

    internal CapturedRequest(HttpRequestMessage request, string? body)
    {
        Method = request.Method;
        RequestUri = request.RequestUri;
        Body = body;
        Timestamp = DateTimeOffset.UtcNow;

        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            headers[h.Key] = h.Value.ToList();
        if (request.Content != null)
        {
            foreach (var h in request.Content.Headers)
                headers[h.Key] = h.Value.ToList();
        }
        Headers = headers;
    }

    /// <summary>
    /// Maximum number of body characters included by <see cref="ToDiagnosticString"/>.
    /// Longer bodies are truncated. Default is 2048.
    /// </summary>
    public static int MaxDiagnosticBodyLength = 2048;

    /// <summary>
    /// Returns a multi-line description of the request, including its timestamp,
    /// match status, headers and body. Intended for test failure diagnostics.
    /// </summary>
    public string ToDiagnosticString()
    {
        var builder = new StringBuilder();
        builder.Append('[').Append(Timestamp.ToLocalTime().ToString()).Append("] ");
        builder.Append(Method).Append(' ').Append(RequestUri);
        builder.Append(Matched ? " (matched)" : " (UNMATCHED)");

        foreach (var header in Headers)
        {
            builder.AppendLine()
                .Append("    ")
                .Append(header.Key)
                .Append(": ")
                .Append(string.Join(", ", header.Value));
        }

        if (!string.IsNullOrEmpty(Body))
        {
            var body = Body.Length > MaxDiagnosticBodyLength
                ? Body.Substring(0, MaxDiagnosticBodyLength) + "..."
                : Body;

            builder.AppendLine().Append("    Body: ").Append(body);
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public override string ToString()
        => $"{Method} {RequestUri}";
}
