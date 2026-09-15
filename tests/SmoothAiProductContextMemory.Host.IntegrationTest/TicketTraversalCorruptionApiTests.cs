using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

public sealed class TicketTraversalCorruptionApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stored_corruption_returns_fixed_500_but_hidden_corruption_does_not_change_response()
    {
        var anchor = new TicketIdentity("jira", $"root-{Guid.NewGuid():N}");
        var child = new TicketIdentity("jira", $"child-{Guid.NewGuid():N}");
        await CreateGroup(anchor);
        Guid childGroup = await CreateGroup(child);
        var query = new { anchor, maxDepth = 3, scopeDimension = "product", pathLimit = 1, memoryLimit = 1 };
        using HttpResponseMessage baseline = await fixture.HttpClient.PostAsJsonAsync("/api/context/tickets/paths", query, Ct);
        baseline.StatusCode.ShouldBe(HttpStatusCode.OK);
        string before = await baseline.Content.ReadAsStringAsync(Ct);

        using HttpResponseMessage declaration = await fixture.HttpClient.PutAsJsonAsync("/api/context/tickets/parent", new
        {
            child, parent = anchor, reason = "private-declaration-marker", source = "private-source-marker",
        }, Ct);
        declaration.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage valid = await fixture.HttpClient.PostAsJsonAsync("/api/context/tickets/paths", query, Ct);
        valid.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await valid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("paths").GetArrayLength().ShouldBe(1);

        // Corrupt persisted metadata, not the HTTP request, on this test's declaration only.
        await using (NpgsqlCommand command = fixture.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            UPDATE memory_graph."TICKET_PARENT"
            SET properties = jsonb_set(properties::text::jsonb, '{recordedAt}', '"corrupt-stored-marker"'::jsonb)::text::ag_catalog.agtype
            WHERE end_id IN (SELECT id FROM memory_graph."Ticket" WHERE properties::text::jsonb->>'key' = @key)
            """))
        {
            command.Parameters.AddWithValue("key", child.Key);
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        using HttpResponseMessage corrupt = await fixture.HttpClient.PostAsJsonAsync("/api/context/tickets/paths", query, Ct);
        corrupt.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        corrupt.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        string failure = await corrupt.Content.ReadAsStringAsync(Ct);
        using JsonDocument problem = JsonDocument.Parse(failure);
        problem.RootElement.GetProperty("detail").GetString().ShouldBe("An unexpected error occurred.");
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Server error");
        foreach (string marker in new[] { anchor.Key, child.Key, "private-declaration-marker", "private-source-marker", "corrupt-stored-marker", "recordedAt", "JsonException", "BytePositionInLine" })
            failure.ShouldNotContain(marker);

        using HttpResponseMessage hidden = await fixture.HttpClient.PatchAsJsonAsync($"/api/context/groups/{childGroup}",
            new { scopeDimension = "program", scopeIdentifier = "corruption-test" }, Ct);
        hidden.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage gated = await fixture.HttpClient.PostAsJsonAsync("/api/context/tickets/paths", query, Ct);
        gated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await gated.Content.ReadAsStringAsync(Ct)).ShouldBe(before);
    }

    private async Task<Guid> CreateGroup(TicketIdentity ticket)
    {
        using HttpResponseMessage response = await fixture.HttpClient.PostAsJsonAsync("/api/context/groups/resolve", new
        {
            tickets = new[] { new { ticket.Provider, ticket.Key, url = "" } }, scopeDimension = "product",
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("uuid").GetGuid();
    }
}
