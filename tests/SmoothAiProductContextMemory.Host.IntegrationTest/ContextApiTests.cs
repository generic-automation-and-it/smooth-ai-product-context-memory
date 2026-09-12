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

        using HttpResponseMessage query = await _http.PostAsJsonAsync("/api/context/query", new { limit = 200 }, Ct);
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

    /// <summary>
    /// The blob proxy carries the scope rule. Holding a uuid is not authority to read programme
    /// knowledge as product fact — otherwise the proxy would be a way around the query filter.
    /// </summary>
    [Fact]
    public async Task Program_blob_needs_an_explicit_scope()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Program);
        JsonElement created = await SetMemory(
            group,
            "Programme blob subject",
            "Programme claim",
            MemoryVersion.MemoryVersionStatus.Approved,
            content: "programme-body");
        Guid uuid = created.GetProperty("items")[0].GetProperty("uuid").GetGuid();

        using HttpResponseMessage blocked = await _http.GetAsync($"/api/context/memories/{uuid}/versions/1/blob", Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using HttpResponseMessage allowed = await _http.GetAsync(
            $"/api/context/memories/{uuid}/versions/1/blob?scope={MemoryGroup.ScopeDimensionValue.Program}",
            Ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await allowed.Content.ReadAsStringAsync(Ct)).ShouldBe("programme-body");
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
        digest.GetProperty("items")[0].GetProperty("uuid").ValueKind.ShouldBe(JsonValueKind.Null);

        using HttpResponseMessage query = await _http.PostAsJsonAsync("/api/context/query", new { query = "Dry subject" }, Ct);
        JsonElement items = (await query.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("items");
        items.GetArrayLength().ShouldBe(0);
    }

    /// <summary>
    /// A subject already present in the group is a version bump, not a second row. The unique index
    /// is the backstop; the plan reaches the same verdict on a dry run so the caller is told before
    /// it writes.
    /// </summary>
    [Fact]
    public async Task Duplicate_subject_returns_409_on_dry_run_and_write()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        await SetMemory(group, "Collision subject", "Claim", MemoryVersion.MemoryVersionStatus.Approved);

        object body = SetBody(group, "Collision subject", "Other claim", MemoryVersion.MemoryVersionStatus.Approved);

        using HttpResponseMessage dry = await _http.PostAsJsonAsync("/api/context/memories?dryRun=true", body, Ct);
        dry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        dry.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using HttpResponseMessage write = await _http.PostAsJsonAsync("/api/context/memories", body, Ct);
        write.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await write.Content.ReadAsStringAsync(Ct)).ShouldNotContain("IX_memory");
    }

    [Fact]
    public async Task As_of_and_limit_narrow_retrieval()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await SetMemory(
            group,
            "Retired policy",
            "No longer true",
            MemoryVersion.MemoryVersionStatus.Approved,
            validFrom: now.AddDays(-10),
            validUntil: now.AddDays(-5));
        await SetMemory(
            group,
            "Live policy",
            "Still true",
            MemoryVersion.MemoryVersionStatus.Approved,
            validFrom: now.AddDays(-1));

        using HttpResponseMessage asOf = await _http.PostAsJsonAsync(
            "/api/context/query",
            new { groupUuid = group, asOf = now, limit = 200 },
            Ct);
        JsonElement current = (await asOf.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("items");
        string[] live = [.. current.EnumerateArray().Select(i => i.GetProperty("description").GetString()!)];
        live.ShouldContain("Live policy");
        live.ShouldNotContain("Retired policy");

        using HttpResponseMessage capped = await _http.PostAsJsonAsync(
            "/api/context/query",
            new { groupUuid = group, limit = 1 },
            Ct);
        (await capped.Content.ReadFromJsonAsync<JsonElement>(Json, Ct))
            .GetProperty("items").GetArrayLength().ShouldBe(1);

        using HttpResponseMessage badLimit = await _http.PostAsJsonAsync(
            "/api/context/query",
            new { limit = 0 },
            Ct);
        badLimit.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Self_link_returns_400()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        JsonElement created = await SetMemory(group, "Link subject", "Claim", MemoryVersion.MemoryVersionStatus.Approved);
        Guid uuid = created.GetProperty("items")[0].GetProperty("uuid").GetGuid();

        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/links",
            new { sourceUuid = uuid, targetUuid = uuid, relation = MemoryLink.RelationValue.RelatesTo, reason = "loop" },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
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
        foreach (string route in ExpectedRoutes)
        {
            doc.ShouldContain(route);
        }
    }

    private static readonly string[] ExpectedRoutes =
    [
        "/api/context/preflight",
        "/api/context/memories",
        "/api/context/memories/{uuid}/versions",
        "/api/context/memories/{uuid}/versions/{version}/blob",
        "/api/context/query",
        "/api/context/groups/resolve",
        "/api/context/groups/{uuid}",
        "/api/context/groups/{uuid}/descriptions",
        "/api/context/links",
        "/api/context/labels",
        "/api/context/initiatives",
    ];

    [Fact]
    public async Task Propose_label_is_draft()
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/labels", new { name = "new-facet" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        body.GetProperty("status").GetString().ShouldBe(Label.LabelStatus.Draft);
    }

    /// <summary>
    /// Usage is derived from the facets actually in use, so a facet nobody registered still has to
    /// appear — that drift is what the endpoint exists to show. Its status is null.
    /// </summary>
    [Fact]
    public async Task Labels_include_unregistered_facets_in_use()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        await SetMemory(
            group,
            "Facet usage subject",
            "Claim",
            MemoryVersion.MemoryVersionStatus.Approved,
            facets: ["wt2-unregistered-facet"]);

        await _http.PostAsJsonAsync("/api/context/labels", new { name = "new-facet-never-used" }, Ct);

        using HttpResponseMessage response = await _http.GetAsync("/api/context/labels", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement items = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("items");

        JsonElement unregistered = items.EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "wt2-unregistered-facet");
        unregistered.GetProperty("uses").GetInt64().ShouldBeGreaterThan(0);
        unregistered.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);

        // A registered label with no use is still listed, with its registry status.
        JsonElement registered = items.EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "new-facet-never-used");
        registered.GetProperty("status").GetString().ShouldBe(Label.LabelStatus.Draft);
        registered.GetProperty("uses").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task Initiative_upsert_then_list()
    {
        using HttpResponseMessage created = await _http.PostAsJsonAsync(
            "/api/context/initiatives",
            new { name = "wt2-initiative", description = "Created by the API test." },
            Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        createdBody.GetProperty("created").GetBoolean().ShouldBeTrue();

        using HttpResponseMessage again = await _http.PostAsJsonAsync(
            "/api/context/initiatives",
            new { name = "wt2-initiative", status = Initiative.InitiativeStatus.Archived },
            Ct);
        JsonElement againBody = await again.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        againBody.GetProperty("created").GetBoolean().ShouldBeFalse();
        againBody.GetProperty("status").GetString().ShouldBe(Initiative.InitiativeStatus.Archived);
        againBody.GetProperty("description").GetString().ShouldBe("Created by the API test.");

        using HttpResponseMessage list = await _http.GetAsync("/api/context/initiatives", Ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement items = (await list.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("items");
        items.EnumerateArray().Select(i => i.GetProperty("name").GetString())
            .ShouldContain("wt2-initiative");
    }

    [Fact]
    public async Task Group_patch_sets_repo_initiative_and_scope()
    {
        await _http.PostAsJsonAsync("/api/context/initiatives", new { name = "wt2-patch-initiative" }, Ct);
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);

        using HttpResponseMessage patched = await _http.PatchAsJsonAsync(
            $"/api/context/groups/{group}",
            new { repo = "kingstown", initiativeName = "wt2-patch-initiative" },
            Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await patched.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        body.GetProperty("repo").GetString().ShouldBe("kingstown");
        body.GetProperty("initiativeName").GetString().ShouldBe("wt2-patch-initiative");
        body.GetProperty("scopeDimension").GetString().ShouldBe(MemoryGroup.ScopeDimensionValue.Product);

        // A scoped dimension without its identifier is rejected against stored state, not silently kept.
        using HttpResponseMessage invalid = await _http.PatchAsJsonAsync(
            $"/api/context/groups/{group}",
            new { scopeDimension = MemoryGroup.ScopeDimensionValue.Program },
            Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using HttpResponseMessage missing = await _http.PatchAsJsonAsync(
            $"/api/context/groups/{Guid.NewGuid()}",
            new { repo = "nope" },
            Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
        string? content = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        string[]? facets = null)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/memories",
            SetBody(groupUuid, description, statement, status, uuid, content, validFrom, validUntil, facets),
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
        string? content = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        string[]? facets = null) => new
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
                facets = facets ?? ["architecture"],
                tags = Array.Empty<string>(),
                status,
                confidence = (short)80,
                content,
                sources = Array.Empty<object>(),
                validFrom = validFrom ?? DateTimeOffset.UtcNow.AddDays(-1),
                validUntil,
                summaryModel = "test-model",
                summaryPromptVersion = "1",
            }
        },
        links = (object?)null,
        labelsProposed = (object?)null,
    };
}
