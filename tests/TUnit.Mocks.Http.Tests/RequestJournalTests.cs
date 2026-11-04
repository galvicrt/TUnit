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
/// </summary>
public class RequestJournalTests
{
    private const string Imsi = "310260987654321";
    private const string Msisdn = "+12065551234";
    private const string AccessToken = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJic3MtcHJvdmlzaW9uaW5nIn0.k3Yf9QeZp2mXrT8vLwN4";

    [Test]
    public async Task DescribeRequests_WhenNoRequests_SaysSo()
    {
        using var client = Mock.HttpClient("http://localhost");

        await Assert.That(client.Handler.DescribeRequests()).IsEqualTo("(no requests captured)");
    }

    [Test]
    public async Task DescribeRequests_IncludesMethodPathHeadersAndBody()
    {
        using var client = Mock.HttpClient("http://udm.core.local:8080");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        client.Handler
            .OnRequest(r => r.Method(HttpMethod.Patch).Path($"/nudm-sdm/v2/imsi-{Imsi}/am-data"))
            .Respond(HttpStatusCode.NoContent);

        var patch = """{"msisdn":"+12065551234","subscribedUeAmbr":{"uplink":"100 Mbps","downlink":"1 Gbps"}}""";
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/nudm-sdm/v2/imsi-{Imsi}/am-data")
        {
            Content = new StringContent(patch, Encoding.UTF8, "application/merge-patch+json")
        });

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains("#1 [");
        await Assert.That(journal).Contains($"PATCH http://udm.core.local:8080/nudm-sdm/v2/imsi-{Imsi}/am-data (matched)");
        await Assert.That(journal).Contains($"Authorization: Bearer {AccessToken}");
        await Assert.That(journal).Contains("Content-Type: application/merge-patch+json; charset=utf-8");
        await Assert.That(journal).Contains(Msisdn);
    }

    [Test]
    public async Task DescribeRequests_MarksUnmatchedRequests()
    {
        using var client = Mock.HttpClient("http://udm.core.local:8080");
        client.Handler.OnGet($"/nudm-sdm/v2/imsi-{Imsi}/am-data").RespondWithJson("{}");

        await client.GetAsync($"/nudm-sdm/v2/imsi-{Imsi}/am-data");
        await client.GetAsync($"/nudm-sdm/v2/imsi-{Imsi}/sms-data");

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains("#1 [");
        await Assert.That(journal).Contains("am-data (matched)");
        await Assert.That(journal).Contains("#2 [");
        await Assert.That(journal).Contains("sms-data (UNMATCHED)");
    }

    [Test]
    public async Task DescribeRequests_TruncatesLongBodies()
    {
        CapturedRequest.MaxDiagnosticBodyLength = 16;

        using var client = Mock.HttpClient("http://localhost");
        client.Handler.OnAnyRequest().Respond();

        await client.PostAsync("/cdr/batch", new StringContent(new string('x', 100)));

        var journal = client.Handler.DescribeRequests();

        await Assert.That(journal).Contains("Body: " + new string('x', 16) + "...");
        await Assert.That(journal).DoesNotContain(new string('x', 17));
    }

    [Test]
    public async Task VerifyNoUnmatchedRequests_FailureMessageIncludesJournal()
    {
        using var client = Mock.HttpClient("http://udm.core.local:8080");

        await client.PostAsync(
            $"/nudm-uecm/v1/imsi-{Imsi}/registrations/amf-3gpp-access",
            new StringContent("""{"amfInstanceId":"amf-west-01"}""", Encoding.UTF8, "application/json"));

        var exception = Assert.Throws<MockVerificationException>(() => client.Handler.VerifyNoUnmatchedRequests());

        await Assert.That(exception.Message).Contains("Request journal:");
        await Assert.That(exception.Message).Contains("amf-3gpp-access (UNMATCHED)");
        await Assert.That(exception.Message).Contains("amf-west-01");
    }
}
