using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

public sealed class CorpusCommitApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Document_approval_metadata_survives_host_write_and_read_without_intent_promotion()
    {
        using var groupResponse = await fixture.HttpClient.PostAsJsonAsync("/api/context/groups/resolve", new { repo = "synthetic/document-contract/" + Guid.NewGuid().ToString("N") }, Ct);
        groupResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var group = await groupResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var request = new
        {
            groupUuid = group.GetProperty("uuid").GetGuid(),
            items = new[] { new { name = "Design acceptance", description = "Document contract", statement = "The design document is approved; its implementation proposal remains Draft.", contentSummary = "Document approval only", kind = "decision", status = "proposed", confidence = 50, content = "Synthetic evidence: approval is of the document, not implementation.", sources = new[] { new { kind = "conversation", reference = "synthetic://document-contract", evidence = new { v = 1, category = "document_approval", applicability = "Design document only" } } } } }
        };
        using var created = await fixture.HttpClient.PostAsJsonAsync("/api/context/memories", request, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Guid uuid = result.GetProperty("items")[0].GetProperty("uuid").GetGuid();
        var versions = await fixture.HttpClient.GetFromJsonAsync<JsonElement>($"/api/context/memories/{uuid}/versions", Ct);
        var version = versions.GetProperty("items")[0];
        version.GetProperty("status").GetString().ShouldBe("proposed");
        version.GetProperty("sources")[0].GetProperty("evidence").GetProperty("category").GetString().ShouldBe("document_approval");
    }

    [Fact]
    public async Task Group_replay_and_operation_lookup_use_the_existing_authenticated_boundary()
    {
        HttpClient client = fixture.HttpClient;
        JsonElement state = await client.GetFromJsonAsync<JsonElement>("/api/context/corpus-state", Ct);
        string key = "http-group-" + Guid.NewGuid().ToString("N");
        string repo = "synthetic/core-api/" + key;
        var request = new { repo, operationKey = key, expectedCorpusEpoch = state.GetProperty("epoch").GetGuid(),
            expectedCorpusRevision = state.GetProperty("revision").GetInt64() };
        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/context/groups/resolve", request, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        string result = await created.Content.ReadAsStringAsync(Ct);
        using HttpResponseMessage replay = await client.PostAsJsonAsync("/api/context/groups/resolve", request, Ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(result);
        JsonElement receipt = await client.GetFromJsonAsync<JsonElement>("/api/context/operations/" + key, Ct);
        receipt.GetProperty("operationType").GetString().ShouldBe("group_resolve");
        receipt.GetProperty("payloadHash").GetString().ShouldNotBeNull().Length.ShouldBe(64);
        using HttpResponseMessage changed = await client.PostAsJsonAsync("/api/context/groups/resolve", new
        {
            repo = repo + "/changed", operationKey = key, expectedCorpusEpoch = state.GetProperty("epoch").GetGuid(),
        }, Ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using HttpResponseMessage lookup = await client.PostAsJsonAsync("/api/context/groups/lookup", new { repo }, Ct);
        lookup.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await lookup.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Keyed_operations_require_an_epoch_and_read_lookup_creates_nothing()
    {
        using HttpResponseMessage invalid = await fixture.HttpClient.PostAsJsonAsync("/api/context/groups/resolve",
            new { operationKey = "missing-epoch" }, Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using HttpClient read = fixture.CreateClient();
        read.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostWebAppFixture.ReadToken);
        using HttpResponseMessage lookup = await read.PostAsJsonAsync("/api/context/groups/lookup",
            new { repo = "synthetic/absent/" + Guid.NewGuid().ToString("N") }, Ct);
        lookup.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await lookup.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await read.GetAsync("/api/context/corpus-state", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await read.PostAsJsonAsync("/api/context/groups/resolve", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
