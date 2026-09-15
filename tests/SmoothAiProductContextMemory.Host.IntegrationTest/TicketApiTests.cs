using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

public sealed class TicketApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private readonly HttpClient _http = fixture.HttpClient;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"}}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"},"maxDepth":0}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"},"maxDepth":6}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"},"maxDepth":"two"}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"},"maxDepth":"2"}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"},"maxDepth":2,"direction":"sideways"}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"},"maxDepth":2,"pathLimit":201}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"},"maxDepth":2,"memoryLimit":0}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1"},"maxDepth":2,"groupUuid":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"anchor":{"provider":"jira","key":"APP-1","url":"not-an-identity"},"maxDepth":2}""")]
    [InlineData("""{"anchor":null,"maxDepth":2}""")]
    public async Task Invalid_ticket_paths_return_problem_details(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.PostAsync("/api/context/tickets/paths", content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData("""{"child":{"provider":"jira","key":"APP-2"},"reason":"remove","source":"user"}""")]
    [InlineData("""{"child":{"provider":"jira","key":"APP-2"},"parent":{"provider":"jira","key":"APP-2"},"reason":"self","source":"user"}""")]
    [InlineData("""{"child":null,"parent":null,"reason":"remove","source":"user"}""")]
    [InlineData("""{"child":{"provider":"jira","key":"APP-2"},"parent":null,"reason":"","source":"user"}""")]
    [InlineData("""{"child":{"provider":"jira","key":"APP-2"},"parent":null,"reason":"remove","source":"user","recordedAt":"2026-09-14T00:00:00Z"}""")]
    public async Task Invalid_parent_changes_return_problem_details(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.PutAsync("/api/context/tickets/parent", content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Parent_set_reparent_remove_and_stale_expectation_round_trip()
    {
        (TicketIdentity parent, _) = await CreateTicket();
        (TicketIdentity replacement, _) = await CreateTicket();
        (TicketIdentity child, _) = await CreateTicket();

        (await ChangeParent(child, parent, null)).GetProperty("changed").GetBoolean().ShouldBeTrue();
        JsonElement initial = await Paths(parent);
        initial.GetProperty("paths").GetArrayLength().ShouldBe(1);
        JsonElement hop = initial.GetProperty("paths")[0].GetProperty("hops")[0];
        hop.GetProperty("parent").GetProperty("key").GetString().ShouldBe(parent.Key);
        hop.GetProperty("child").GetProperty("key").GetString().ShouldBe(child.Key);
        hop.GetProperty("reason").GetString().ShouldBe("Declared by practitioner");
        hop.GetProperty("source").GetString().ShouldBe("HTTP test");
        hop.GetProperty("recordedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.MinValue);

        using HttpResponseMessage stale = await _http.PutAsJsonAsync("/api/context/tickets/parent",
            new { child, parent = replacement, reason = "stale", source = "HTTP test" }, Ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        stale.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        (await ChangeParent(child, replacement, parent)).GetProperty("changed").GetBoolean().ShouldBeTrue();
        (await Paths(parent)).GetProperty("paths").GetArrayLength().ShouldBe(0);
        (await Paths(replacement)).GetProperty("paths").GetArrayLength().ShouldBe(1);
        (await ChangeParent(child, null, replacement)).GetProperty("changed").GetBoolean().ShouldBeTrue();
        (await Paths(replacement)).GetProperty("paths").GetArrayLength().ShouldBe(0);
        (await ChangeParent(child, null, null)).GetProperty("changed").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("product")]
    public async Task Hidden_anchor_is_empty_without_ticket_scope_consent(string? scope)
    {
        (TicketIdentity anchor, Guid group) = await CreateTicket("program");
        Guid memory = await Capture(group);

        JsonElement hidden = await Paths(anchor, scope);
        hidden.GetProperty("paths").GetArrayLength().ShouldBe(0);
        hidden.GetProperty("items").GetArrayLength().ShouldBe(0);
        hidden.ToString().ShouldNotContain(anchor.Key);
        hidden.ToString().ShouldNotContain(memory.ToString());
        JsonElement disclosure = hidden.GetProperty("disclosure");
        disclosure.GetProperty("hierarchyCoverage").GetString()!.ShouldContain("upstream freshness and completeness are unverified");
        disclosure.GetProperty("depthLimitReached").GetBoolean().ShouldBeFalse();
        disclosure.GetProperty("pathLimitReached").GetBoolean().ShouldBeFalse();
        disclosure.GetProperty("memoryLimitReached").GetBoolean().ShouldBeFalse();

        JsonElement visible = await Paths(anchor, "program");
        visible.GetProperty("items").EnumerateArray().Single().GetProperty("uuid").GetGuid().ShouldBe(memory);
    }

    [Fact]
    public async Task Hidden_intermediate_drops_whole_paths_without_changing_visible_cap_flags()
    {
        (TicketIdentity anchor, _) = await CreateTicket();
        (TicketIdentity hidden, _) = await CreateTicket("program");
        (TicketIdentity endpoint, _) = await CreateTicket();
        JsonElement before = await Paths(anchor, "product");
        await ChangeParent(hidden, anchor, null);
        await ChangeParent(endpoint, hidden, null);

        JsonElement after = await Paths(anchor, "product");
        after.ToString().ShouldBe(before.ToString());
        after.ToString().ShouldNotContain(hidden.Key);
        after.ToString().ShouldNotContain(endpoint.Key);
        after.ToString().ShouldNotContain("Declared by practitioner");
        (await Paths(anchor, "program")).GetProperty("paths").GetArrayLength().ShouldBe(2);
    }

    private async Task<(TicketIdentity Ticket, Guid Group)> CreateTicket(string scope = "product")
    {
        var ticket = new TicketIdentity("jira", $"HTTP-{Guid.NewGuid():N}");
        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/groups/resolve", new
        {
            tickets = new[] { new { ticket.Provider, ticket.Key, url = "" } },
            scopeDimension = scope,
            scopeIdentifier = scope == "program" ? "ticket-test" : null,
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (ticket, body.GetProperty("uuid").GetGuid());
    }

    private async Task<Guid> Capture(Guid group)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/memories", new
        {
            groupUuid = group,
            items = new[]
            {
                new
                {
                    name = "Ticket memory", description = "Ticket anchor memory", statement = "Captured claim",
                    contentSummary = "Summary", kind = "decision", facets = new[] { "architecture" },
                    tags = Array.Empty<string>(), status = "approved", confidence = 80,
                    validFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    sources = Array.Empty<object>(), summaryModel = "test-model", summaryPromptVersion = "1",
                },
            },
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items")[0].GetProperty("uuid").GetGuid();
    }

    private async Task<JsonElement> ChangeParent(TicketIdentity child, TicketIdentity? parent, TicketIdentity? expectedParent)
    {
        using HttpResponseMessage response = await _http.PutAsJsonAsync("/api/context/tickets/parent", new
        {
            child, parent, expectedParent, reason = "Declared by practitioner", source = "HTTP test",
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<JsonElement> Paths(TicketIdentity anchor, string? scope = null)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/tickets/paths", new
        {
            anchor, maxDepth = 3, scopeDimension = scope, pathLimit = 50, memoryLimit = 50,
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
