using System.Globalization;
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

    private const string Redacted = "[REDACTED]";

    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
    };

    private static readonly string[] SensitiveHeaderNameFragments =
    [
        "token",
        "secret",
        "api-key",
        "apikey",
        "password",
        "session",
        "signature",
    ];

    /// <summary>
    /// Returns a multi-line description of the request for test failure diagnostics:
    /// the UTC timestamp (ISO 8601), method, URI, match status and headers.
    /// Values of credential-bearing headers (for example <c>Authorization</c>,
    /// <c>Cookie</c> or any header whose name contains <c>token</c> or <c>api-key</c>)
    /// are replaced with <c>[REDACTED]</c>.
    /// </summary>
    /// <param name="maxBodyLength">
    /// Maximum number of body characters to include. The default, <c>0</c>, omits the
    /// body and shows only its length, because bodies often carry personal data.
    /// Longer bodies are truncated.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBodyLength"/> is negative.</exception>
    public string ToDiagnosticString(int maxBodyLength = 0)
    {
        if (maxBodyLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBodyLength), maxBodyLength, "Must be zero or greater.");
        }

        var builder = new StringBuilder();
        builder.Append('[')
            .Append(Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            .Append("] ");
        builder.Append(Method).Append(' ').Append(RequestUri);
        builder.Append(Matched ? " (matched)" : " (UNMATCHED)");

        foreach (var header in Headers)
        {
            builder.Append('\n')
                .Append("    ")
                .Append(header.Key)
                .Append(": ")
                .Append(IsSensitiveHeader(header.Key) ? Redacted : string.Join(", ", header.Value));
        }

        if (!string.IsNullOrEmpty(Body))
        {
            builder.Append('\n').Append("    Body: ");

            if (maxBodyLength == 0)
            {
                builder.Append('<').Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append(" chars omitted>");
            }
            else if (Body.Length > maxBodyLength)
            {
                builder.Append(Body, 0, maxBodyLength).Append("...");
            }
            else
            {
                builder.Append(Body);
            }
        }

        return builder.ToString();
    }

    private static bool IsSensitiveHeader(string name)
    {
        if (SensitiveHeaderNames.Contains(name))
        {
            return true;
        }

        foreach (var fragment in SensitiveHeaderNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"{Method} {RequestUri}";
}
