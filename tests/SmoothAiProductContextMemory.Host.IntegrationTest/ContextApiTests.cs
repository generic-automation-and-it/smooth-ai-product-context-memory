using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

public sealed class ContextApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = fixture.HttpClient;
    private readonly HostWebAppFixture _fixture = fixture;
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
    public async Task Read_token_can_read_but_cannot_mutate()
    {
        using HttpClient read = _fixture.CreateClient();
        read.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostWebAppFixture.ReadToken);

        (await read.PostAsJsonAsync("/api/context/query", new { limit = 1 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await read.PostAsJsonAsync("/api/context/preflight", new { candidates = Array.Empty<object>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.PostAsJsonAsync("/api/context/memories", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.PostAsJsonAsync("/api/context/groups/resolve", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.PatchAsJsonAsync($"/api/context/groups/{Guid.NewGuid()}", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.PostAsJsonAsync($"/api/context/groups/{Guid.NewGuid()}/descriptions", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.PostAsJsonAsync("/api/context/links", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.PutAsJsonAsync("/api/context/tickets/parent", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.PostAsJsonAsync("/api/context/labels", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.PostAsJsonAsync("/api/context/initiatives", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.GetAsync($"/api/context/recall-feedback/miss-rate?from={DateTimeOffset.UtcNow:yyyy-MM-dd}&to={DateTimeOffset.UtcNow:yyyy-MM-dd}", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await read.PostAsJsonAsync("/api/context/recall-feedback/reset", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Context_routes_reject_missing_token_while_openapi_stays_public()
    {
        using HttpClient anonymous = _fixture.CreateClient();

        foreach ((HttpMethod method, string path) in new[]
        {
            (HttpMethod.Post, "/api/context/preflight"),
            (HttpMethod.Post, "/api/context/memories"),
            (HttpMethod.Post, "/api/context/query"),
            (HttpMethod.Get, $"/api/context/memories/{Guid.NewGuid()}/versions"),
            (HttpMethod.Get, $"/api/context/memories/{Guid.NewGuid()}/versions/1/blob"),
            (HttpMethod.Post, "/api/context/groups/resolve"),
            (HttpMethod.Patch, $"/api/context/groups/{Guid.NewGuid()}"),
            (HttpMethod.Post, $"/api/context/groups/{Guid.NewGuid()}/descriptions"),
            (HttpMethod.Post, "/api/context/links"),
            (HttpMethod.Post, "/api/context/paths"),
            (HttpMethod.Put, "/api/context/tickets/parent"),
            (HttpMethod.Post, "/api/context/tickets/paths"),
            (HttpMethod.Get, "/api/context/labels"),
            (HttpMethod.Post, "/api/context/labels"),
            (HttpMethod.Get, "/api/context/initiatives"),
            (HttpMethod.Post, "/api/context/initiatives"),
            (HttpMethod.Get, $"/api/context/recall-feedback/never-recalled?asOf={DateTimeOffset.UtcNow:yyyy-MM-dd}"),
            (HttpMethod.Get, $"/api/context/recall-feedback/miss-rate?from={DateTimeOffset.UtcNow:yyyy-MM-dd}&to={DateTimeOffset.UtcNow:yyyy-MM-dd}"),
            (HttpMethod.Post, "/api/context/recall-feedback/reset"),
        })
        {
            using var request = new HttpRequestMessage(method, path);
            if (method is not null && method != HttpMethod.Get)
            {
                request.Content = JsonContent.Create(new { });
            }
            using HttpResponseMessage response = await anonymous.SendAsync(request, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
        }
        (await anonymous.GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
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

    /// <summary>
    /// Version history is a read path too, so it carries the same scope rule as the blob drill-down.
    /// </summary>
    [Fact]
    public async Task Program_versions_need_an_explicit_scope()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Program);
        JsonElement created = await SetMemory(
            group,
            "Programme versions subject",
            "Programme claim",
            MemoryVersion.MemoryVersionStatus.Approved);
        Guid uuid = created.GetProperty("items")[0].GetProperty("uuid").GetGuid();

        using HttpResponseMessage blocked = await _http.GetAsync($"/api/context/memories/{uuid}/versions", Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using HttpResponseMessage allowed = await _http.GetAsync(
            $"/api/context/memories/{uuid}/versions?scope={MemoryGroup.ScopeDimensionValue.Program}",
            Ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
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

    [Fact]
    public async Task Set_creates_and_links_new_memories_with_stable_dry_run_identities()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        object body = new
        {
            groupUuid = group,
            items = new[]
            {
                SetItem("Linked create A", "A", first),
                SetItem("Linked create B", "B", second),
            },
            links = new[]
            {
                new { sourceUuid = first, targetUuid = second, relation = MemoryRelation.Contradicts, reason = "conflicting evidence" },
            },
            labelsProposed = Array.Empty<string>(),
        };

        JsonElement dry = await PostJson("/api/context/memories?dryRun=true", body);
        dry.GetProperty("items")[0].GetProperty("uuid").GetGuid().ShouldBe(first);
        dry.GetProperty("linked").GetInt32().ShouldBe(1);

        JsonElement written = await PostJson("/api/context/memories", body);
        written.GetProperty("items")[1].GetProperty("uuid").GetGuid().ShouldBe(second);
        written.GetProperty("linked").GetInt32().ShouldBe(1);

        using HttpResponseMessage paths = await _http.PostAsJsonAsync(
            "/api/context/paths",
            new { sourceUuid = first, targetUuid = second, maxDepth = 1 },
            Ct);
        paths.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await paths.Content.ReadFromJsonAsync<JsonElement>(Json, Ct))
            .GetProperty("paths").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Set_rejects_create_and_version_identity_together()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        object body = new
        {
            groupUuid = group,
            items = new[]
            {
                new
                {
                    uuid = Guid.NewGuid(),
                    createUuid = Guid.NewGuid(),
                    name = "Invalid identity",
                    description = "Invalid identity combination",
                    statement = "Cannot be create and version.",
                    contentSummary = "Invalid",
                    kind = MemoryVersion.KindValue.Decision,
                    facets = Array.Empty<string>(),
                    tags = Array.Empty<string>(),
                    status = MemoryVersion.MemoryVersionStatus.Approved,
                    confidence = (short)80,
                    content = (string?)null,
                    sources = Array.Empty<object>(),
                    validFrom = DateTimeOffset.UtcNow,
                    validUntil = (DateTimeOffset?)null,
                    summaryModel = "test-model",
                    summaryPromptVersion = "1",
                },
            },
        };

        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/memories", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("CreateUuid");
    }

    [Fact]
    public async Task Set_orders_repeated_version_targets_in_one_transaction()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        Guid uuid = await Capture(group, "Authority subject", "Existing winner");
        JsonElement losing = JsonSerializer.SerializeToElement(SetBody(
                group,
                "Authority subject",
                "Losing candidate",
                MemoryVersion.MemoryVersionStatus.Approved,
                uuid), Json)
            .GetProperty("items")[0];
        JsonElement winner = JsonSerializer.SerializeToElement(SetBody(
                group,
                "Authority subject",
                "Existing winner",
                MemoryVersion.MemoryVersionStatus.Approved,
                uuid), Json)
            .GetProperty("items")[0];
        object body = new
        {
            groupUuid = group,
            items = new object[]
            {
                losing,
                winner,
            },
            links = Array.Empty<object>(),
            labelsProposed = Array.Empty<string>(),
        };

        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/memories", body, Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);

        using HttpResponseMessage history = await _http.GetAsync($"/api/context/memories/{uuid}/versions", Ct);
        JsonElement versions = (await history.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("items");
        versions.GetArrayLength().ShouldBe(3);
        versions[1].GetProperty("statement").GetString().ShouldBe("Losing candidate");
        versions[2].GetProperty("statement").GetString().ShouldBe("Existing winner");
        versions[2].GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// A body-supplied dryRun is a dry run too — it must not be silently overwritten by the absent
    /// query default and turned into a real write.
    /// </summary>
    [Fact]
    public async Task Body_dry_run_is_honoured()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        using HttpResponseMessage dry = await _http.PostAsJsonAsync(
            "/api/context/memories",
            new
            {
                groupUuid = group,
                items = new[]
                {
                    new
                    {
                        uuid = (Guid?)null,
                        name = "Name",
                        description = "Body dry subject",
                        statement = "Body dry claim",
                        contentSummary = "Summary",
                        kind = MemoryVersion.KindValue.Decision,
                        facets = new[] { "architecture" },
                        tags = Array.Empty<string>(),
                        status = MemoryVersion.MemoryVersionStatus.Approved,
                        confidence = (short)80,
                        content = (string?)null,
                        sources = Array.Empty<object>(),
                        validFrom = DateTimeOffset.UtcNow.AddDays(-1),
                        validUntil = (DateTimeOffset?)null,
                        summaryModel = "test-model",
                        summaryPromptVersion = "1",
                    }
                },
                links = (object?)null,
                labelsProposed = (object?)null,
                dryRun = true,
            },
            Ct);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement digest = await dry.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        digest.GetProperty("created").GetInt32().ShouldBe(1);
        digest.GetProperty("items")[0].GetProperty("blobAddress").ValueKind.ShouldBe(JsonValueKind.Null);

        using HttpResponseMessage query = await _http.PostAsJsonAsync("/api/context/query", new { query = "Body dry subject" }, Ct);
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
            new { sourceUuid = uuid, targetUuid = uuid, relation = MemoryRelation.RelatesTo, reason = "loop" },
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

        object body = new { sourceUuid = a, targetUuid = b, relation = MemoryRelation.DependsOn, reason = "need" };
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
        doc.ShouldContain("bearer");
        doc.ShouldContain("Requires write capability");
    }

    /// <summary>
    /// A rejected body has to name the field that was rejected. The wrapper exception says only
    /// "Failed to read parameter", which leaves the caller guessing; the inner JsonException carries
    /// the member and its JSON path, and that is what makes the 400 actionable.
    /// </summary>
    [Fact]
    public async Task Unknown_body_property_returns_400_naming_the_property()
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/preflight",
            new { candidates = new[] { new { description = "Subject", statement = "Not a preflight field" } } },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        body.GetProperty("title").GetString().ShouldBe("Invalid request body");
        body.GetProperty("detail").GetString()!.ShouldContain("statement");
        // The path is a problem extension, not prose spliced into the detail: it says which element
        // of the batch carried the rejected member, and a caller can read it without parsing English.
        body.GetProperty("path").GetString().ShouldBe("$.candidates[0].statement");
    }

    /// <summary>
    /// Every optional member of a request contract must be optional in the schema too. A caller that
    /// trusts an over-declared <c>required</c> list sends fields the endpoint does not accept, and
    /// <c>JsonUnmappedMemberHandling.Disallow</c> then rejects the whole body.
    /// </summary>
    [Fact]
    public async Task Openapi_marks_only_genuinely_required_preflight_members_as_required()
    {
        using HttpResponseMessage openapi = await _http.GetAsync("/openapi/v1.json", Ct);
        openapi.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement doc = await openapi.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        JsonElement schemas = doc.GetProperty("components").GetProperty("schemas");
        JsonElement candidate = schemas.EnumerateObject()
            .Single(schema => schema.Name.EndsWith("Preflight.Candidate", StringComparison.Ordinal))
            .Value;
        string[] required = candidate.TryGetProperty("required", out JsonElement req)
            ? [.. req.EnumerateArray().Select(e => e.GetString()!)]
            : [];
        required.ShouldBe(["description"]);

        string[] properties = [.. candidate.GetProperty("properties").EnumerateObject().Select(m => m.Name)];
        properties.ShouldContain("groupUuid");

        JsonElement memoryWrite = schemas.EnumerateObject()
            .Single(schema => schema.Name.EndsWith("SetMemories.MemoryWrite", StringComparison.Ordinal))
            .Value;
        string[] memoryProperties = [.. memoryWrite.GetProperty("properties").EnumerateObject().Select(m => m.Name)];
        memoryProperties.ShouldContain("uuid");
        memoryProperties.ShouldContain("createUuid");
    }

    [Fact]
    public async Task Recall_feedback_tuning_surfaces_round_trip()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        JsonElement created = await SetMemory(group, "Recalled fact", "Claim", MemoryVersion.MemoryVersionStatus.Approved);
        Guid memoryUuid = created.GetProperty("items")[0].GetProperty("uuid").GetGuid();

        // A second memory that no query in this test matches, so never-recalled has something to return.
        JsonElement untouched = await SetMemory(
            group, "Unretrieved subject", "Never queried claim", MemoryVersion.MemoryVersionStatus.Approved);
        Guid untouchedUuid = untouched.GetProperty("items")[0].GetProperty("uuid").GetGuid();

        // A hit query writes a record for the returned memory; an empty query writes a miss record.
        (await _http.PostAsJsonAsync("/api/context/query", new { query = "Recalled fact", limit = 50 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.PostAsJsonAsync("/api/context/query", new { query = "zz-no-such-memory-xyz", limit = 50 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // never-recalled excludes anything captured within 7 days of asOf (NpgsqlRecallFeedbackQuery
        // RecentlyCapturedGraceDays), and both memories above were captured now. Asking as of a date
        // past that window is what puts them in scope — with asOf=today every assertion below passes
        // vacuously, because the grace filter alone removes them whether or not a feedback row exists.
        string asOf = $"{DateTimeOffset.UtcNow.AddDays(8):yyyy-MM-dd}";
        using HttpResponseMessage never = await _http.GetAsync(
            $"/api/context/recall-feedback/never-recalled?asOf={asOf}&limit=500", Ct);
        never.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement neverBody = await never.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        JsonElement[] neverItems = [.. neverBody.GetProperty("items").EnumerateArray()];

        // In scope and never queried, so present; in scope but recalled, so filtered by its feedback row.
        neverItems.ShouldContain(i => i.GetProperty("memoryUuid").GetGuid() == untouchedUuid);
        neverItems.ShouldNotContain(i => i.GetProperty("memoryUuid").GetGuid() == memoryUuid);

        // Identity + capture time only, asserted on a row known to be present.
        JsonElement untouchedRow = neverItems.Single(i => i.GetProperty("memoryUuid").GetGuid() == untouchedUuid);
        untouchedRow.GetProperty("capturedOn").GetDateTimeOffset().ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(1));

        // miss-rate is derivable over the window from the hit + miss just recorded.
        DateTimeOffset to = DateTimeOffset.UtcNow;
        using HttpResponseMessage rate = await _http.GetAsync(
            $"/api/context/recall-feedback/miss-rate?from={WebUtility.UrlEncode(to.AddHours(-1).ToString("O"))}&to={WebUtility.UrlEncode(to.ToString("O"))}", Ct);
        string ratePayload = await rate.Content.ReadAsStringAsync(Ct);
        rate.StatusCode.ShouldBe(HttpStatusCode.OK, ratePayload);
        JsonElement rateBody = JsonSerializer.Deserialize<JsonElement>(ratePayload, Json);
        rateBody.GetProperty("retrievals").GetInt32().ShouldBeGreaterThanOrEqualTo(2);
        rateBody.GetProperty("misses").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        rateBody.GetProperty("missRate").GetDouble().ShouldBeGreaterThan(0);

        // reset (write capability) clears the baseline and returns the deleted count.
        using HttpResponseMessage reset = await _http.PostAsJsonAsync("/api/context/recall-feedback/reset", new { }, Ct);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement resetBody = await reset.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        resetBody.GetProperty("recordsDeleted").GetInt32().ShouldBeGreaterThan(0);
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
        "/api/context/paths",
        "/api/context/tickets/parent",
        "/api/context/tickets/paths",
        "/api/context/labels",
        "/api/context/initiatives",
        "/api/context/recall-feedback/never-recalled",
        "/api/context/recall-feedback/miss-rate",
        "/api/context/recall-feedback/reset",
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

    [Fact]
    public async Task Resolve_rejects_an_unknown_field_instead_of_defaulting()
    {
        // A misspelled `initiative` used to be absorbed silently and the group created with the
        // default `to-be-decided` — reported as success. The unknown field must now be a 400.
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/groups/resolve",
            new { initiative = "atlas", scopeDimension = MemoryGroup.ScopeDimensionValue.Product },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Group_patch_rejects_an_unknown_field()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);

        using HttpResponseMessage response = await _http.PatchAsJsonAsync(
            $"/api/context/groups/{group}",
            new { repoUrlx = "https://example.com" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Traversal_returns_the_chain_and_the_endpoint_fields()
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        Guid measurement = await Capture(group, "Traversal measurement subject", "Latency was 42 ms");
        Guid finding = await Capture(group, "Traversal finding subject", "Latency exceeds the target");
        Guid decision = await Capture(group, "Traversal decision subject", "Adopt the cache");

        await Link(measurement, finding, MemoryRelation.DependsOn, "the measurement produced the finding");
        await Link(finding, decision, MemoryRelation.DependsOn, "the finding justified the decision");

        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/paths",
            new { sourceUuid = measurement, targetUuid = decision, maxDepth = 3 },
            Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);

        JsonElement path = JsonSerializer.Deserialize<JsonElement>(payload, Json)
            .GetProperty("paths").EnumerateArray().Single();
        path.GetProperty("depth").GetInt32().ShouldBe(2);

        JsonElement[] hops = [.. path.GetProperty("hops").EnumerateArray()];
        hops.Length.ShouldBe(2);
        hops[0].GetProperty("sourceUuid").GetGuid().ShouldBe(measurement);
        hops[0].GetProperty("reason").GetString().ShouldBe("the measurement produced the finding");
        hops[1].GetProperty("targetUuid").GetGuid().ShouldBe(decision);
        hops[1].GetProperty("relation").GetString().ShouldBe(MemoryRelation.DependsOn);

        // Descriptive fields come from the relational rows, not from the graph.
        path.GetProperty("endpoint").GetProperty("uuid").GetGuid().ShouldBe(decision);
        path.GetProperty("endpoint").GetProperty("statement").GetString().ShouldBe("Adopt the cache");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MemoryTraversalDefaults.MaxDepth + 1)]
    public async Task Traversal_without_a_usable_bound_returns_400(int maxDepth)
    {
        Guid group = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        Guid source = await Capture(group, $"Unbounded subject {maxDepth}", "Claim");

        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/paths",
            new { sourceUuid = source, maxDepth },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Traversal_from_a_programme_memory_requires_the_declared_scope()
    {
        Guid programme = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Program);
        Guid source = await Capture(programme, "Programme traversal subject", "Programme claim");

        using HttpResponseMessage undeclared = await _http.PostAsJsonAsync(
            "/api/context/paths",
            new { sourceUuid = source, maxDepth = 2 },
            Ct);
        undeclared.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using HttpResponseMessage declared = await _http.PostAsJsonAsync(
            "/api/context/paths",
            new { sourceUuid = source, maxDepth = 2, scopeDimension = MemoryGroup.ScopeDimensionValue.Program },
            Ct);
        declared.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Traversal_declaring_product_does_not_disclose_programme_intermediates()
    {
        // Pins the handler's hop gate end to end: HiddenDimensions("product") hides programme hops,
        // whereas a regression to Plan().ExcludedDimensions (empty for every explicit dimension)
        // would disclose them while the store-level tests stay green.
        Guid product = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Product);
        Guid programme = await ResolveGroup(MemoryGroup.ScopeDimensionValue.Program);
        Guid source = await Capture(product, "Cross-scope source subject", "Product claim");
        Guid hidden = await Capture(programme, "Programme intermediate subject", "Programme claim");
        Guid endpoint = await Capture(product, "Cross-scope endpoint subject", "Product decision");

        await Link(source, hidden, MemoryRelation.DependsOn, "programme rationale");
        await Link(hidden, endpoint, MemoryRelation.DependsOn, "leads to the decision");

        using HttpResponseMessage declared = await _http.PostAsJsonAsync(
            "/api/context/paths",
            new
            {
                sourceUuid = source,
                targetUuid = endpoint,
                maxDepth = 3,
                scopeDimension = MemoryGroup.ScopeDimensionValue.Product,
            },
            Ct);
        string payload = await declared.Content.ReadAsStringAsync(Ct);
        declared.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        payload.ShouldNotContain(hidden.ToString());
        payload.ShouldNotContain("programme rationale");
    }

    [Fact]
    public async Task Traversal_from_an_unknown_memory_returns_404()
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/paths",
            new { sourceUuid = Guid.NewGuid(), maxDepth = 2 },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<Guid> Capture(Guid groupUuid, string description, string statement) =>
        (await SetMemory(groupUuid, description, statement, MemoryVersion.MemoryVersionStatus.Approved))
            .GetProperty("items")[0].GetProperty("uuid").GetGuid();

    private async Task Link(Guid source, Guid target, string relation, string reason)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/links",
            new { sourceUuid = source, targetUuid = target, relation, reason },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
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

    private static object SetItem(string description, string statement, Guid createUuid) => new
    {
        uuid = (Guid?)null,
        createUuid,
        name = "Name",
        description,
        statement,
        contentSummary = "Summary",
        kind = MemoryVersion.KindValue.Decision,
        facets = new[] { "architecture" },
        tags = Array.Empty<string>(),
        status = MemoryVersion.MemoryVersionStatus.Approved,
        confidence = (short)80,
        content = (string?)null,
        sources = Array.Empty<object>(),
        validFrom = DateTimeOffset.UtcNow.AddDays(-1),
        validUntil = (DateTimeOffset?)null,
        summaryModel = "test-model",
        summaryPromptVersion = "1",
    };

    private async Task<JsonElement> PostJson(string route, object body)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(route, body, Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        return JsonSerializer.Deserialize<JsonElement>(payload, Json);
    }
}
