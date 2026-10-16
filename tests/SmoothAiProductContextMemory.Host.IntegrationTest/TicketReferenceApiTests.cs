using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// Happy-path L2: a ticket search returns the owner group's memories <b>and</b> the memories that
/// reference the ticket from a second initiative (HLD-003 LADR-09). Everything below is over HTTP —
/// capture the owner's memory, capture a referencing memory under another initiative, query by ticket,
/// narrow by initiative, and add a reference to an existing memory on the standalone route.
/// </summary>
public sealed class TicketReferenceApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private readonly HttpClient _http = fixture.HttpClient;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ticket_query_returns_owner_and_referencing_memories_and_narrows_by_initiative()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string ownerInitiative = $"tr-owner-{suffix}";
        string copyInitiative = $"tr-copy-{suffix}";
        await CreateInitiative(ownerInitiative);
        await CreateInitiative(copyInitiative);

        var ticket = new TicketIdentity("jira", $"TR-{suffix}");

        // Group A owns the ticket.
        Guid ownerGroup = await ResolveGroup(ticket, ownerInitiative, null);
        Guid ownerMemory = await Capture(ownerGroup, "Owner memory");

        // Group B, a different initiative, captures a WET copy that references the same ticket.
        Guid copyGroup = await ResolveGroup(null, copyInitiative, null);
        Guid copyMemory = await Capture(copyGroup, "Referencing memory", reference: ticket);

        // A ticket query returns the union, each item marked with how it matched.
        JsonElement union = await Query(ticket.Provider, ticket.Key, initiativeName: null);
        JsonElement[] items = [.. union.GetProperty("items").EnumerateArray()];
        items.Select(i => i.GetProperty("uuid").GetGuid()).ShouldBe([ownerMemory, copyMemory], ignoreOrder: true);
        items.Single(i => i.GetProperty("uuid").GetGuid() == ownerMemory)
            .GetProperty("ticketMatch").GetString().ShouldBe("owner");
        items.Single(i => i.GetProperty("uuid").GetGuid() == copyMemory)
            .GetProperty("ticketMatch").GetString().ShouldBe("reference");

        // Every other filter is conjunctive over the union, so an initiative narrows to one branch.
        JsonElement ownerOnly = await Query(ticket.Provider, ticket.Key, initiativeName: ownerInitiative);
        ownerOnly.GetProperty("items").EnumerateArray().Single()
            .GetProperty("uuid").GetGuid().ShouldBe(ownerMemory);

        JsonElement copyOnly = await Query(ticket.Provider, ticket.Key, initiativeName: copyInitiative);
        copyOnly.GetProperty("items").EnumerateArray().Single()
            .GetProperty("uuid").GetGuid().ShouldBe(copyMemory);

        // A query with no ticket carries a null ticketMatch.
        JsonElement unticketed = await Query(null, null, initiativeName: copyInitiative);
        unticketed.GetProperty("items").EnumerateArray().Single()
            .GetProperty("ticketMatch").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Ticket_references_route_adds_an_about_edge_to_an_existing_memory()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string ownerInitiative = $"tr2-owner-{suffix}";
        string copyInitiative = $"tr2-copy-{suffix}";
        await CreateInitiative(ownerInitiative);
        await CreateInitiative(copyInitiative);

        var ticket = new TicketIdentity("jira", $"TR2-{suffix}");
        Guid ownerGroup = await ResolveGroup(ticket, ownerInitiative, null);
        await Capture(ownerGroup, "Owner memory");

        // A memory captured without a reference, then pointed at the ticket on the standalone route.
        Guid copyGroup = await ResolveGroup(null, copyInitiative, null);
        Guid copyMemory = await Capture(copyGroup, "Late referencing memory");

        using HttpResponseMessage added = await _http.PostAsJsonAsync(
            "/api/context/memories/ticket-references",
            new
            {
                uuids = new[] { copyMemory },
                tickets = new[] { new { ticket.Provider, ticket.Key, reason = "shared ticket" } },
                reason = "cross-initiative copy",
            },
            Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));
        JsonElement receipt = await added.Content.ReadFromJsonAsync<JsonElement>(Ct);
        receipt.GetProperty("created").GetInt32().ShouldBe(1);

        JsonElement union = await Query(ticket.Provider, ticket.Key, initiativeName: null);
        union.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("uuid").GetGuid() == copyMemory)
            .GetProperty("ticketMatch").GetString().ShouldBe("reference");
    }

    private async Task CreateInitiative(string name)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/api/context/initiatives",
            new { name, description = "integration test", status = "active" },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<Guid> ResolveGroup(TicketIdentity? ticket, string initiativeName, string? scope)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/groups/resolve", new
        {
            tickets = ticket is null
                ? Array.Empty<object>()
                : [new { ticket.Provider, ticket.Key, url = "" }],
            initiativeName,
            scopeDimension = scope,
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("uuid").GetGuid();
    }

    private async Task<Guid> Capture(Guid group, string description, TicketIdentity? reference = null)
    {
        object[] references = reference is null
            ? []
            : [new { reference.Provider, reference.Key, reason = "WET copy" }];
        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/memories", new
        {
            groupUuid = group,
            ticketReferences = references.Length == 0 ? null : references,
            items = new[]
            {
                new
                {
                    name = description, description, statement = "Captured claim",
                    contentSummary = "Summary", kind = "decision", facets = new[] { "architecture" },
                    tags = Array.Empty<string>(), status = "approved", confidence = 80,
                    validFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    sources = Array.Empty<object>(), summaryModel = "test-model", summaryPromptVersion = "1",
                },
            },
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("items")[0].GetProperty("uuid").GetGuid();
    }

    private async Task<JsonElement> Query(string? provider, string? key, string? initiativeName)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/context/query", new
        {
            ticketProvider = provider,
            ticketKey = key,
            initiativeName,
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
