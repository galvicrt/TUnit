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

    private static readonly HashSet<string> UriValuedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Referer",
        "Origin",
        "Location",
        "Content-Location",
    };

    private static readonly string[] SensitiveNameFragments =
    [
        "auth",
        "token",
        "secret",
        "api-key",
        "apikey",
        "password",
        "session",
        "signature",
        "sig",
        "key",
        "code",
    ];

    /// <summary>
    /// Returns a multi-line description of the request for test failure diagnostics:
    /// the UTC timestamp (ISO 8601), method, URI, match status and headers.
    /// Values of credential-bearing headers and URI parameters (for example
    /// <c>Authorization</c>, <c>Cookie</c>, or any name containing <c>auth</c>,
    /// <c>token</c> or <c>key</c>) and URI user information are replaced with
    /// <c>[REDACTED]</c>, including inside URI-valued headers such as <c>Referer</c>.
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
        builder.Append(Method).Append(' ').Append(FormatUri(RequestUri));
        builder.Append(Matched ? " (matched)" : " (UNMATCHED)");

        foreach (var header in Headers)
        {
            builder.Append('\n')
                .Append("    ")
                .Append(header.Key)
                .Append(": ")
                .Append(FormatHeaderValue(header.Key, header.Value));
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

    private static bool IsSensitiveName(string name)
    {
        if (SensitiveHeaderNames.Contains(name))
        {
            return true;
        }

        foreach (var fragment in SensitiveNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Formats a request URI for diagnostics. User information (<c>user:password@</c>)
    /// is replaced with <c>[REDACTED]</c>, as are the values of query and fragment
    /// parameters whose names look credential-bearing (for example <c>api_key</c>,
    /// <c>access_token</c>, <c>sig</c> or <c>code</c>).
    /// </summary>
    internal static string FormatUri(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        if (!uri.IsAbsoluteUri)
        {
            return uri.OriginalString;
        }

        var builder = new StringBuilder();
        builder.Append(uri.Scheme).Append("://");
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            builder.Append(Redacted).Append('@');
        }

        builder.Append(uri.GetComponents(UriComponents.Host | UriComponents.Port | UriComponents.Path, UriFormat.UriEscaped));

        if (uri.Query.Length > 1)
        {
            builder.Append('?').Append(RedactParameters(uri.Query.Substring(1)));
        }

        if (uri.Fragment.Length > 1)
        {
            builder.Append('#').Append(RedactParameters(uri.Fragment.Substring(1)));
        }

        return builder.ToString();
    }

    private static string FormatHeaderValue(string name, IEnumerable<string> values)
    {
        if (IsSensitiveName(name))
        {
            return Redacted;
        }

        if (UriValuedHeaderNames.Contains(name))
        {
            return string.Join(", ", values.Select(v => Uri.TryCreate(v, UriKind.Absolute, out var uri) ? FormatUri(uri) : v));
        }

        return string.Join(", ", values);
    }

    private static string RedactParameters(string parameters)
    {
        var parts = parameters.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var name = parts[i].Substring(0, separator);
            if (IsSensitiveName(Uri.UnescapeDataString(name)))
            {
                parts[i] = name + "=" + Redacted;
            }
        }

        return string.Join("&", parts);
    }

    /// <summary>
    /// Returns the method and URI. Credential-bearing query parameter values are redacted.
    /// </summary>
    public override string ToString()
        => $"{Method} {FormatUri(RequestUri)}";
}
