using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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

    /// <summary>
    /// Body field names that hold subscriber or device identifiers (3GPP and common
    /// telecom names), matched exactly and case-insensitively.
    /// </summary>
    private static readonly HashSet<string> SubscriberIdentifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "imsi",
        "supi",
        "suci",
        "msisdn",
        "gpsi",
        "imei",
        "imeisv",
        "pei",
        "iccid",
        "msin",
        "email",
        "phoneNumber",
        "phone",
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
    /// When positive, JSON and form-encoded bodies are shown with credential and
    /// subscriber-identifier fields (for example <c>password</c>, <c>imsi</c> or
    /// <c>msisdn</c>) replaced with <c>[REDACTED]</c>; bodies of any other content type,
    /// or that cannot be parsed, are still shown only as their length. The redacted
    /// body is truncated to this length.
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

            var body = maxBodyLength == 0 ? null : RedactBody(Body, GetContentType());

            if (body is null)
            {
                builder.Append('<').Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append(" chars omitted>");
            }
            else if (body.Length > maxBodyLength)
            {
                builder.Append(body, 0, maxBodyLength).Append("...");
            }
            else
            {
                builder.Append(body);
            }
        }

        return builder.ToString();
    }

    private string? GetContentType()
        => Headers.TryGetValue("Content-Type", out var values)
            && MediaTypeHeaderValue.TryParse(values.FirstOrDefault(), out var mediaType)
                ? mediaType.MediaType
                : null;

    /// <summary>
    /// Returns the body with sensitive field values replaced by <c>[REDACTED]</c>, or
    /// <see langword="null"/> when the body cannot be redacted safely (an unknown content
    /// type or malformed content), in which case only its length is shown.
    /// </summary>
    private static string? RedactBody(string body, string? mediaType)
    {
        if (mediaType is null)
        {
            return null;
        }

        if (mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return RedactParameters(body, IsSensitiveBodyField);
        }

        if (mediaType.EndsWith("/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return RedactJson(body);
        }

        return null;
    }

    private static string? RedactJson(string body)
    {
        try
        {
            var reader = new Utf8JsonReader(
                Encoding.UTF8.GetBytes(body),
                new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                var redactValue = false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var name = reader.GetString()!;
                        writer.WritePropertyName(name);
                        redactValue = IsSensitiveBodyField(name);
                        continue;
                    }

                    if (redactValue && reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
                    {
                        // Skip moves past a whole object or array value; for a scalar it is a no-op.
                        reader.Skip();
                        writer.WriteStringValue(Redacted);
                        redactValue = false;
                        continue;
                    }

                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject: writer.WriteStartObject(); break;
                        case JsonTokenType.EndObject: writer.WriteEndObject(); break;
                        case JsonTokenType.StartArray: writer.WriteStartArray(); break;
                        case JsonTokenType.EndArray: writer.WriteEndArray(); break;
                        case JsonTokenType.String: writer.WriteStringValue(reader.GetString()); break;
                        case JsonTokenType.Number: writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true); break;
                        case JsonTokenType.True: writer.WriteBooleanValue(true); break;
                        case JsonTokenType.False: writer.WriteBooleanValue(false); break;
                        case JsonTokenType.Null: writer.WriteNullValue(); break;
                    }
                }
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsSensitiveBodyField(string name)
        => IsSensitiveName(name) || SubscriberIdentifierNames.Contains(name);

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

        if (!uri.IsAbsoluteUri || uri.IsFile)
        {
            // Relative URIs (and, on Unix, rooted paths that Uri parses as file URIs)
            // have no user information, but may still carry credentials in parameters.
            return RedactRelativeUri(uri.OriginalString);
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

        // Any header can carry a URL (Referer, Location, or custom ones such as
        // X-Callback-Url), so redact URI credentials based on the value, not the name.
        return string.Join(", ", values.Select(value => LooksLikeUri(value) ? FormatUriString(value) : value));
    }

    private static bool LooksLikeUri(string value)
        => value.Contains("://", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.IndexOf('?') >= 0
            || value.IndexOf('#') >= 0;

    private static string FormatUriString(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile
            ? FormatUri(uri)
            : RedactRelativeUri(value);

    private static string RedactRelativeUri(string value)
    {
        value = RedactNetworkPathUserInfo(value);

        var fragment = string.Empty;
        var fragmentStart = value.IndexOf('#');
        if (fragmentStart >= 0)
        {
            fragment = "#" + RedactParameters(value.Substring(fragmentStart + 1));
            value = value.Substring(0, fragmentStart);
        }

        var queryStart = value.IndexOf('?');
        if (queryStart >= 0)
        {
            value = value.Substring(0, queryStart) + "?" + RedactParameters(value.Substring(queryStart + 1));
        }

        return value + fragment;
    }

    /// <summary>
    /// A network-path reference (<c>//user:password@host/path</c>, RFC 3986 section 4.2)
    /// has an authority but no scheme, so it can still carry user information.
    /// </summary>
    private static string RedactNetworkPathUserInfo(string value)
    {
        if (!value.StartsWith("//", StringComparison.Ordinal))
        {
            return value;
        }

        var authorityEnd = value.IndexOfAny(['/', '?', '#'], 2);
        if (authorityEnd < 0)
        {
            authorityEnd = value.Length;
        }

        var userInfoEnd = value.LastIndexOf('@', authorityEnd - 1, authorityEnd - 2);
        return userInfoEnd < 0
            ? value
            : "//" + Redacted + value.Substring(userInfoEnd);
    }

    private static string RedactParameters(string parameters)
        => RedactParameters(parameters, IsSensitiveName);

    private static string RedactParameters(string parameters, Func<string, bool> isSensitive)
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
            if (isSensitive(Uri.UnescapeDataString(name.Replace('+', ' '))))
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
