using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TUnit.Mocks;
using TUnit.Mocks.Exceptions;
using TUnit.Mocks.Http;

namespace TUnit.Mocks.Http.Tests;

/// <summary>
/// Tests for the request journal (<see cref="MockHttpHandler.DescribeRequests"/>),
/// using a subscriber-profile lookup against a 5G UDM-style API as the scenario.
/// All subscriber data is synthetic: the IMSI is in the 001-01 test network and the
/// MSISDN is in the 555-0100 to 555-0199 range reserved for fictional use.
/// </summary>
public class RequestJournalTests
{
    private const string Imsi = "001010000000001";
    private const string Msisdn = "+12065550123";
    private const string AccessToken = "test-token-not-a-real-credential";
    private const string AmDataPath = $"/nudm-sdm/v2/imsi-{Imsi}/am-data";

    [Test]
    public async Task DescribeRequests_WhenNoRequests_SaysSo()
    {
        using var client = Mock.HttpClient("http://localhost");

        await Assert.That(client.Handler.DescribeRequests()).IsEqualTo("(no requests captured)");
    }

    [Test]
    public async Task DescribeRequests_IncludesMethodUriStatusAndHeaders()
    {
        using var client = Mock.HttpClient("http://localhost");
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "corr-42");
        client.Handler
            .OnRequest(r => r.Method(HttpMethod.Patch).Path(AmDataPath))
            .Respond(HttpStatusCode.NoContent);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, AmDataPath)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/merge-patch+json")
        });

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).StartsWith("#1 [");
        await Assert.That(journal).Contains($"PATCH http://localhost{AmDataPath} (matched)");
        await Assert.That(journal).Contains("X-Correlation-Id: corr-42");
        await Assert.That(journal).Contains("Content-Type: application/merge-patch+json; charset=utf-8");
    }

    [Test]
    public async Task DescribeRequests_UsesInvariantUtcTimestamp()
    {
        using var client = Mock.HttpClient("http://localhost");

        await client.GetAsync(AmDataPath);

        var request = client.Handler.Requests[0];
        var expected = request.Timestamp.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        await Assert.That(client.Handler.DescribeRequests()).StartsWith($"#1 [{expected}] ");
    }

    [Test]
    [Arguments("Authorization")]
    [Arguments("Proxy-Authorization")]
    [Arguments("Cookie")]
    [Arguments("X-Api-Key")]
    [Arguments("X-Auth")]
    [Arguments("X-Auth-Token")]
    [Arguments("X-Session-Id")]
    public async Task DescribeRequests_RedactsCredentialHeaders(string headerName)
    {
        using var client = Mock.HttpClient("http://localhost");
        using var request = new HttpRequestMessage(HttpMethod.Get, AmDataPath);
        request.Headers.TryAddWithoutValidation(headerName, AccessToken);

        await client.SendAsync(request);

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains($"{headerName}: [REDACTED]");
        await Assert.That(journal).DoesNotContain(AccessToken);
    }

    [Test]
    public async Task DescribeRequests_RedactsBearerToken()
    {
        using var client = Mock.HttpClient("http://localhost");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        await client.GetAsync(AmDataPath);

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains("Authorization: [REDACTED]");
        await Assert.That(journal).DoesNotContain(AccessToken);
    }

    [Test]
    public async Task DescribeRequests_RedactsCredentialQueryParameters()
    {
        using var client = Mock.HttpClient("http://localhost");

        await client.GetAsync($"{AmDataPath}?api_key={AccessToken}&supported-features=1a&access_token={AccessToken}#top");

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains(
            $"GET http://localhost{AmDataPath}?api_key=[REDACTED]&supported-features=1a&access_token=[REDACTED]#top");
        await Assert.That(journal).DoesNotContain(AccessToken);
    }

    [Test]
    public async Task DescribeRequests_RedactsUserInfoAndFragmentCredentials()
    {
        using var client = Mock.HttpClient();

        await client.GetAsync($"http://bss-user:{AccessToken}@localhost{AmDataPath}#access_token={AccessToken}&view=full");

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains(
            $"GET http://[REDACTED]@localhost{AmDataPath}#access_token=[REDACTED]&view=full");
        await Assert.That(journal).DoesNotContain(AccessToken);
        await Assert.That(journal).DoesNotContain("bss-user");
    }

    [Test]
    public async Task DescribeRequests_RedactsCredentialsInsideUriValuedHeaders()
    {
        using var client = Mock.HttpClient("http://localhost");
        using var request = new HttpRequestMessage(HttpMethod.Get, AmDataPath);
        request.Headers.Referrer = new Uri($"https://portal.test/callback?access_token={AccessToken}&step=2");

        await client.SendAsync(request);

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains("Referer: https://portal.test/callback?access_token=[REDACTED]&step=2");
        await Assert.That(journal).DoesNotContain(AccessToken);
    }

    [Test]
    public async Task VerifyNoUnmatchedRequests_UnmatchedListRedactsQueryCredentials()
    {
        using var client = Mock.HttpClient("http://localhost");

        await client.GetAsync($"{AmDataPath}?token={AccessToken}");

        var exception = Assert.Throws<MockVerificationException>(() => client.Handler.VerifyNoUnmatchedRequests());

        await Assert.That(exception.Message).Contains($"  - GET http://localhost{AmDataPath}?token=[REDACTED]");
        await Assert.That(exception.Message).DoesNotContain(AccessToken);
    }

    [Test]
    public async Task ThrowOnUnmatched_MessageRedactsQueryCredentials()
    {
        using var client = Mock.HttpClient("http://localhost");
        client.Handler.ThrowOnUnmatched();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await client.GetAsync($"{AmDataPath}?sig={AccessToken}"));

        await Assert.That(exception!.Message).Contains("?sig=[REDACTED]");
        await Assert.That(exception.Message).DoesNotContain(AccessToken);
    }

    [Test]
    public async Task DescribeRequests_OmitsBodiesByDefault()
    {
        using var client = Mock.HttpClient("http://localhost");
        var body = $$"""{"msisdn":"{{Msisdn}}"}""";

        await client.PostAsync(AmDataPath, new StringContent(body));

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains($"Body: <{body.Length} chars omitted>");
        await Assert.That(journal).DoesNotContain(Msisdn);
    }

    [Test]
    public async Task DescribeRequests_IncludesBodiesWhenEnabled()
    {
        using var client = Mock.HttpClient("http://localhost");
        client.Handler.IncludeBodiesInDiagnostics();

        await client.PostAsync(AmDataPath, new StringContent($$"""{"msisdn":"{{Msisdn}}"}"""));

        await Assert.That(client.Handler.DescribeRequests()).Contains($"Body: {{\"msisdn\":\"{Msisdn}\"}}");
    }

    [Test]
    public async Task DescribeRequests_TruncatesLongBodies()
    {
        using var client = Mock.HttpClient("http://localhost");
        client.Handler.IncludeBodiesInDiagnostics(maxLength: 16);

        await client.PostAsync("/cdr/batch", new StringContent(new string('x', 100)));

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains("Body: " + new string('x', 16) + "...");
        await Assert.That(journal).DoesNotContain(new string('x', 17));
    }

    [Test]
    public async Task IncludeBodiesInDiagnostics_RejectsNegativeLength()
    {
        using var client = Mock.HttpClient("http://localhost");

        Assert.Throws<ArgumentOutOfRangeException>(() => client.Handler.IncludeBodiesInDiagnostics(-1));

        await Assert.That(client.Handler.Requests).IsEmpty();
    }

    [Test]
    public async Task DescribeRequests_MarksUnmatchedRequests()
    {
        using var client = Mock.HttpClient("http://localhost");
        client.Handler.OnGet(AmDataPath).RespondWithJson("{}");

        await client.GetAsync(AmDataPath);
        await client.GetAsync($"/nudm-sdm/v2/imsi-{Imsi}/sms-data");

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains("am-data (matched)");
        await Assert.That(journal).Contains("\n#2 [");
        await Assert.That(journal).Contains("sms-data (UNMATCHED)");
    }

    [Test]
    public async Task VerifyNoUnmatchedRequests_FailureMessageIncludesJournal()
    {
        using var client = Mock.HttpClient("http://localhost");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        await client.PostAsync(
            $"/nudm-uecm/v1/imsi-{Imsi}/registrations/amf-3gpp-access",
            new StringContent("""{"amfInstanceId":"amf-test-01"}""", Encoding.UTF8, "application/json"));

        var exception = Assert.Throws<MockVerificationException>(() => client.Handler.VerifyNoUnmatchedRequests());

        await Assert.That(exception.Message).Contains("Request journal:");
        await Assert.That(exception.Message).Contains("amf-3gpp-access (UNMATCHED)");
        await Assert.That(exception.Message).Contains("Authorization: [REDACTED]");
        await Assert.That(exception.Message).DoesNotContain(AccessToken);
        await Assert.That(exception.Message).DoesNotContain("amf-test-01");
    }
}
