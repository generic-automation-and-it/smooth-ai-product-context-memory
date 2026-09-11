using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

public sealed class ContextApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = fixture.HttpClient;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Empty_query_returns_200_empty_array()
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/query",
            new { query = "zz-no-such-memory-xyz" },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Capture_retrieve_version_history_and_scope()
    {
        Guid productGroup = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        Guid programGroup = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Program);
        Guid selfGroup = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Self);

        JsonElement created = await SetMemory(productGroup, "Product fact", "Claim v1", MemoryVersion.MemoryVersionStatus.Approved, content: "blob-body");
        Guid memoryUuid = created.GetProperty("items")[0].GetProperty("uuid").GetGuid();
        created.GetProperty("created").GetInt32().ShouldBe(1);

        await SetMemory(productGroup, "Product fact", "Claim v2", MemoryVersion.MemoryVersionStatus.Approved, uuid: memoryUuid);
        await SetMemory(programGroup, "Program fact", "Programme claim", MemoryVersion.MemoryVersionStatus.Approved);
        await SetMemory(selfGroup, "Self fact", "Preference", MemoryVersion.MemoryVersionStatus.Approved);
        await SetMemory(productGroup, "Proposed fact", "Not canon", MemoryVersion.MemoryVersionStatus.Proposed);

        using HttpResponseMessage query = await _http.PostAsJsonAsync("/api/context/query", new { }, Ct);
        query.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement items = (await query.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("items");
        string[] descriptions = [.. items.EnumerateArray().Select(i => i.GetProperty("description").GetString()!)];
        descriptions.ShouldContain("Product fact");
        descriptions.ShouldContain("Self fact");
        descriptions.ShouldNotContain("Program fact");
        descriptions.ShouldNotContain("Proposed fact");
        items.EnumerateArray().Single(i => i.GetProperty("description").GetString() == "Self fact")
            .GetProperty("scopeDimension").GetString().ShouldBe(MemoryGroup.ScopeDimensionValue.Self);
        items.EnumerateArray().Single(i => i.GetProperty("description").GetString() == "Product fact")
            .GetProperty("statement").GetString().ShouldBe("Claim v2");

        using HttpResponseMessage history = await _http.GetAsync($"/api/context/memories/{memoryUuid}/versions", Ct);
        history.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement versions = (await history.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("items");
        versions.GetArrayLength().ShouldBe(2);
        versions[0].GetProperty("statement").GetString().ShouldBe("Claim v1");
        versions[1].GetProperty("isCurrent").GetBoolean().ShouldBeTrue();

        using HttpResponseMessage blob = await _http.GetAsync($"/api/context/memories/{memoryUuid}/versions/1/blob", Ct);
        blob.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await blob.Content.ReadAsStringAsync(Ct)).ShouldBe("blob-body");
    }

    [Fact]
    public async Task Dry_run_persists_nothing()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        using HttpResponseMessage dry = await _http.PostAsJsonAsync(
            "/api/context/memories?dryRun=true",
            SetBody(group, "Dry subject", "Dry claim", MemoryVersion.MemoryVersionStatus.Approved, content: "secret-body"),
            Ct);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement digest = await dry.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        digest.GetProperty("created").GetInt32().ShouldBe(1);
        digest.GetProperty("items")[0].GetProperty("blobAddress").ValueKind.ShouldBe(JsonValueKind.Null);

        using HttpResponseMessage query = await _http.PostAsJsonAsync("/api/context/query", new { query = "Dry subject" }, Ct);
        JsonElement items = (await query.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("items");
        items.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Self_link_returns_409()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        JsonElement created = await SetMemory(group, "Link subject", "Claim", MemoryVersion.MemoryVersionStatus.Approved);
        Guid uuid = created.GetProperty("items")[0].GetProperty("uuid").GetGuid();

        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/links",
            new { sourceUuid = uuid, targetUuid = uuid, relation = MemoryLink.RelationValue.RelatesTo, reason = "loop" },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Duplicate_link_returns_409()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        Guid a = (await SetMemory(group, "A subject", "A", MemoryVersion.MemoryVersionStatus.Approved))
            .GetProperty("items")[0].GetProperty("uuid").GetGuid();
        Guid b = (await SetMemory(group, "B subject", "B", MemoryVersion.MemoryVersionStatus.Approved))
            .GetProperty("items")[0].GetProperty("uuid").GetGuid();

        object body = new { sourceUuid = a, targetUuid = b, relation = MemoryLink.RelationValue.DependsOn, reason = "need" };
        (await _http.PostAsJsonAsync("/api/context/links", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage dup = await _http.PostAsJsonAsync("/api/context/links", body, Ct);
        dup.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Scalar_and_openapi_list_routes()
    {
        using HttpResponseMessage scalar = await _http.GetAsync("/scalar/v1", Ct);
        scalar.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage openapi = await _http.GetAsync("/openapi/v1.json", Ct);
        openapi.StatusCode.ShouldBe(HttpStatusCode.OK);
        string doc = await openapi.Content.ReadAsStringAsync(Ct);
        doc.ShouldContain("/api/context/preflight");
        doc.ShouldContain("/api/context/memories");
        doc.ShouldContain("/api/context/query");
        doc.ShouldContain("/api/context/groups/resolve");
        doc.ShouldContain("/api/context/labels");
        doc.ShouldContain("/api/context/links");
    }

    [Fact]
    public async Task Propose_label_is_draft()
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/labels", new { name = "new-facet" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        body.GetProperty("status").GetString().ShouldBe(Label.LabelStatus.Draft);
    }

    private async Task<Guid> ResolveGroup(string scope)
    {
        object body = scope is MemoryGroup.ScopeDimensionValue.Program or MemoryGroup.ScopeDimensionValue.Customer
            ? new { scopeDimension = scope, scopeIdentifier = "wt-2", tickets = Array.Empty<object>() }
            : new { scopeDimension = scope, tickets = Array.Empty<object>() };

        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/groups/resolve", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return json.GetProperty("uuid").GetGuid();
    }

    private async Task<JsonElement> SetMemory(
        Guid groupUuid,
        string description,
        string statement,
        string status,
        Guid? uuid = null,
        string? content = null)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/memories",
            SetBody(groupUuid, description, statement, status, uuid, content),
            Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        return JsonSerializer.Deserialize<JsonElement>(payload, Json);
    }

    private static object SetBody(
        Guid groupUuid,
        string description,
        string statement,
        string status,
        Guid? uuid = null,
        string? content = null) => new
    {
        groupUuid,
        items = new[]
        {
            new
            {
                uuid,
                name = "Name",
                description,
                statement,
                contentSummary = "Summary",
                kind = MemoryVersion.KindValue.Decision,
                facets = new[] { "architecture" },
                tags = Array.Empty<string>(),
                status,
                confidence = (short)80,
                content,
                sources = Array.Empty<object>(),
                validFrom = DateTimeOffset.UtcNow.AddDays(-1),
                validUntil = (DateTimeOffset?)null,
                summaryModel = "test-model",
                summaryPromptVersion = "1",
            }
        },
        links = (object?)null,
        labelsProposed = (object?)null,
    };
}
