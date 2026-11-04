using System.Collections.Concurrent;
using System.Net;
using TUnit.Mocks.Exceptions;

namespace TUnit.Mocks.Http;

/// <summary>
/// A mock HTTP message handler for testing HTTP client interactions.
/// Provides fluent request matching and response configuration.
/// </summary>
public sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly List<RequestSetup> _setups = new();
    private readonly Lock _setupsLock = new();
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();
    private HttpStatusCode _defaultStatusCode = HttpStatusCode.NotFound;
    private bool _throwOnUnmatched;
    private int _diagnosticBodyLength;

    /// <summary>
    /// Creates a new <see cref="HttpClient"/> using this handler.
    /// </summary>
    public HttpClient CreateClient() => new(this);

    /// <summary>
    /// Creates a new <see cref="HttpClient"/> with a base address using this handler.
    /// </summary>
    public HttpClient CreateClient(string baseAddress) => new(this)
    {
        BaseAddress = new Uri(baseAddress)
    };

    /// <summary>
    /// Set the default status code for unmatched requests. Default is 404 NotFound.
    /// </summary>
    public MockHttpHandler WithDefaultStatus(HttpStatusCode statusCode)
    {
        _defaultStatusCode = statusCode;
        return this;
    }

    /// <summary>
    /// Throw an exception for unmatched requests instead of returning a default response.
    /// </summary>
    public MockHttpHandler ThrowOnUnmatched()
    {
        _throwOnUnmatched = true;
        return this;
    }

    /// <summary>
    /// Include up to <paramref name="maxLength"/> characters of each request body in
    /// <see cref="DescribeRequests"/> and verification failure messages. Bodies are
    /// omitted by default because they often carry personal data; enable this only
    /// for tests that use synthetic data.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
    public MockHttpHandler IncludeBodiesInDiagnostics(int maxLength = 2048)
    {
        if (maxLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "Must be zero or greater.");
        }

        Volatile.Write(ref _diagnosticBodyLength, maxLength);
        return this;
    }

    /// <summary>
    /// Configure a request-response setup using a fluent request matcher.
    /// </summary>
    public RequestSetup OnRequest(Action<RequestMatcher> configure)
    {
        var matcher = new RequestMatcher();
        configure(matcher);
        var setup = new RequestSetup(matcher);
        lock (_setupsLock)
        {
            _setups.Add(setup);
        }
        return setup;
    }

    /// <summary>
    /// Configure a setup matching any request.
    /// </summary>
    public RequestSetup OnAnyRequest()
    {
        return OnRequest(_ => { });
    }

    /// <summary>
    /// Configure a setup matching GET requests to the specified path.
    /// </summary>
    public RequestSetup OnGet(string path)
    {
        return OnRequest(r => r.Method(HttpMethod.Get).Path(path));
    }

    /// <summary>
    /// Configure a setup matching POST requests to the specified path.
    /// </summary>
    public RequestSetup OnPost(string path)
    {
        return OnRequest(r => r.Method(HttpMethod.Post).Path(path));
    }

    /// <summary>
    /// Configure a setup matching PUT requests to the specified path.
    /// </summary>
    public RequestSetup OnPut(string path)
    {
        return OnRequest(r => r.Method(HttpMethod.Put).Path(path));
    }

    /// <summary>
    /// Configure a setup matching DELETE requests to the specified path.
    /// </summary>
    public RequestSetup OnDelete(string path)
    {
        return OnRequest(r => r.Method(HttpMethod.Delete).Path(path));
    }

    /// <summary>All captured requests in order.</summary>
    public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

    /// <summary>Requests that did not match any setup.</summary>
    public IReadOnlyList<CapturedRequest> UnmatchedRequests
        => _requests.Where(r => !r.Matched).ToList();

    /// <summary>
    /// Verify a matching request was made the expected number of times.
    /// </summary>
    public void Verify(Action<RequestMatcher> configure, Times times)
    {
        var matcher = new RequestMatcher();
        configure(matcher);

        var matching = 0;
        foreach (var request in _requests)
        {
            if (matcher.Matches(request))
            {
                matching++;
            }
        }

        if (!times.Matches(matching))
        {
            throw new MockVerificationException(
                matcher.Describe(),
                times,
                matching,
                Requests.Select(r => r.ToDiagnosticString(Volatile.Read(ref _diagnosticBodyLength))).ToList(),
                null);
        }
    }

    /// <summary>
    /// Verify no unexpected requests were made (all requests matched a setup).
    /// </summary>
    public void VerifyNoUnmatchedRequests()
    {
        var unmatched = UnmatchedRequests;
        if (unmatched.Count > 0)
        {
            throw new MockVerificationException(
                $"VerifyNoUnmatchedRequests failed. {unmatched.Count} request(s) did not match any setup:\n" +
                string.Join("\n", unmatched.Select(r => $"  - {r}")) +
                "\n\nRequest journal:\n" + DescribeRequests());
        }
    }

    /// <summary>
    /// Returns a human-readable journal of every captured request, in the order
    /// it was received. Credential-bearing headers are redacted, and bodies are
    /// omitted unless <see cref="IncludeBodiesInDiagnostics"/> was called.
    /// Useful when a test fails and you need to see what the code under test sent.
    /// </summary>
    public string DescribeRequests()
    {
        var requests = Requests;
        if (requests.Count == 0)
        {
            return "(no requests captured)";
        }

        var maxBodyLength = Volatile.Read(ref _diagnosticBodyLength);
        return string.Join("\n", requests.Select((r, i) => $"#{i + 1} {r.ToDiagnosticString(maxBodyLength)}"));
    }

    /// <summary>Clears all setups and captured requests.</summary>
    public void Reset()
    {
        lock (_setupsLock)
        {
            _setups.Clear();
        }
        while (_requests.TryDequeue(out _)) { }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // ReadAsStringAsync buffers the original content, so response factories can
        // reread it without replacing its bytes, headers, or stream ownership.
        var bodyContent = request.Content != null
            ? await request.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
                cancellationToken
#endif
            ).ConfigureAwait(false)
            : null;

        var captured = new CapturedRequest(request, bodyContent);
        _requests.Enqueue(captured);

        List<RequestSetup> setupsSnapshot;
        lock (_setupsLock)
        {
            setupsSnapshot = new List<RequestSetup>(_setups);
        }

        // Iterate last-added-first to match MockEngine "last wins" semantics
        for (var i = setupsSnapshot.Count - 1; i >= 0; i--)
        {
            var setup = setupsSnapshot[i];
            if (setup.TryMatch(request, bodyContent))
            {
                var response = setup.GetNextResponse();
                if (response != null)
                {
                    captured.Matched = true;
                    if (response.Delay.HasValue)
                        await Task.Delay(response.Delay.Value, cancellationToken).ConfigureAwait(false);
                    return response.Build(request);
                }
            }
        }

        if (_throwOnUnmatched)
        {
            throw new HttpRequestException(
                $"No matching setup for {request.Method} {CapturedRequest.FormatUri(request.RequestUri)}. " +
                $"Configure a setup using OnRequest() or use WithDefaultStatus() instead of ThrowOnUnmatched().");
        }

        return new HttpResponseMessage(_defaultStatusCode) { RequestMessage = request };
    }
}
