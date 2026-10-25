using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

public sealed class RecallAttributionApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Attribution_filters_and_grouped_callers_are_exposed_without_polluting_direct_metrics()
    {
        HttpClient client = fixture.HttpClient;
        DateTimeOffset from = DateTimeOffset.UtcNow.AddSeconds(-1);
        Guid caller = Guid.NewGuid();
        string repo = "synthetic/attribution/" + Guid.NewGuid().ToString("N");
        foreach (string purpose in new[] { "service_retrieval", "service_retrieval", "capture_comparison", "direct_retrieval" })
        {
            using var response = await client.PostAsJsonAsync("/api/context/query", new { repo, recallPurpose = purpose, callerRequestId = caller }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").GetArrayLength().ShouldBe(0);
        }
        string window = $"/api/context/recall-feedback/miss-rate?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddSeconds(1).ToString("O"))}";
        var direct = await client.GetFromJsonAsync<JsonElement>(window, Ct);
        direct.GetProperty("retrievals").GetInt32().ShouldBe(1);
        direct.GetProperty("callerRequests").GetInt32().ShouldBe(1);
        var service = await client.GetFromJsonAsync<JsonElement>(window + "&recallPurpose=service_retrieval", Ct);
        service.GetProperty("retrievals").GetInt32().ShouldBe(2);
        service.GetProperty("misses").GetInt32().ShouldBe(2);
        service.GetProperty("callerRequests").GetInt32().ShouldBe(1);
        service.GetProperty("callerMisses").GetInt32().ShouldBe(1);
        var all = await client.GetFromJsonAsync<JsonElement>(window + "&recallPurpose=all", Ct);
        all.GetProperty("retrievals").GetInt32().ShouldBe(4);
        all.GetProperty("callerRequests").GetInt32().ShouldBe(3); // one observed caller per purpose
        (await client.GetAsync(window + "&recallPurpose=private-query-text", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/context/query", new { recallPurpose = "all" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
