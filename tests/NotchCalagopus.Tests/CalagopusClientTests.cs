using System.Net;
using System.Text;
using NotchCalagopus.Api;

namespace NotchCalagopus.Tests;

public class CalagopusClientTests
{
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static CalagopusClient Client(Fake fake) => new(new Uri("https://panel.example/sub"), "key", fake);

    [Fact]
    public async Task ListsServersWithBearerKeyUnderTheBasePath()
    {
        var fake = new Fake(_ => Json("""{"servers":{"total":1,"data":[{"uuid":"u1","uuid_short":"u1abc","name":"Survival","node_uuid":"n1","node_name":"Node","limits":{"cpu":200,"memory":4096,"disk":10240}}]}}"""));
        using CalagopusClient client = Client(fake);

        List<ServerInfo> servers = await client.ListServersAsync(default);

        ServerInfo server = Assert.Single(servers);
        Assert.Equal("Survival", server.Name);
        Assert.Equal(4096, server.Limits!.Memory);
        Assert.Equal("Bearer key", fake.Requests[0].Headers.Authorization!.ToString());
        Assert.StartsWith("https://panel.example/sub/api/client/servers", fake.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task RejectedKeyIsReportedAsRejected()
    {
        using CalagopusClient client = Client(new Fake(_ => Json("""{"errors":["Invalid API key"]}""", HttpStatusCode.Unauthorized)));

        var error = await Assert.ThrowsAsync<CalagopusApiException>(() => client.ListServersAsync(default));

        Assert.True(error.IsRejected);
        Assert.Equal("Invalid API key", error.Message);
    }

    [Fact]
    public async Task HtmlReplyMeansTheAddressIsNotAPanel()
    {
        using CalagopusClient client = Client(new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") }));

        var error = await Assert.ThrowsAsync<CalagopusApiException>(() => client.ListServersAsync(default));

        Assert.Contains("panelUrl", error.Message);
    }

    [Fact]
    public async Task ServerWithoutDataGivesNullUsage()
    {
        using CalagopusClient client = Client(new Fake(_ => Json("{}", HttpStatusCode.NotFound)));

        Assert.Null(await client.GetServerResourcesAsync("u1", default));
    }
}
