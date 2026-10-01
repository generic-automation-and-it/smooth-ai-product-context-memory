using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// The dossier read halves (HLD-005) over the real Host: state is written through the API with the
/// write token, then read back with the read token — the capability a dossier consumer holds.
/// </summary>
public sealed class DossierApiTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _write = fixture.HttpClient;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bundle_and_preview_return_the_seeded_anchor_and_its_widened_neighbour()
    {
        string key = Guid.NewGuid().ToString("N");
        string repo = $"dossier-{key}";
        string tag = $"dossier-tag-{key}";
        Guid anchorUuid = Guid.NewGuid();
        Guid neighbourUuid = Guid.NewGuid();
        string body = $"Dossier anchor body {key}";

        JsonElement group = await PostJson(_write, "/api/context/groups/resolve", new
        {
            tickets = Array.Empty<object>(),
            repo,
            scopeDimension = MemoryGroup.ScopeDimensionValue.Product,
        });
        await PostJson(_write, "/api/context/memories", new
        {
            groupUuid = group.GetProperty("uuid").GetGuid(),
            items = new[]
            {
                Item(anchorUuid, $"Dossier anchor {key}", "Anchor claim", [tag], body),
                Item(neighbourUuid, $"Dossier neighbour {key}", "Neighbour claim", [], null),
            },
            links = new[]
            {
                new { sourceUuid = anchorUuid, targetUuid = neighbourUuid, relation = MemoryRelation.DependsOn, reason = "needs it" },
            },
            labelsProposed = Array.Empty<string>(),
        });

        using HttpClient read = fixture.CreateClient();
        read.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", HostWebAppFixture.ReadToken);
        var anchor = new { repo, tags = new[] { tag }, includeHistory = false, widenDepth = 1 };

        JsonElement bundle = (await PostJson(read, "/api/context/dossier/bundle", anchor)).GetProperty("bundle");

        JsonElement[] items = [.. bundle.GetProperty("items").EnumerateArray()];
        items.Select(i => i.GetProperty("uuid").GetGuid()).ShouldBe([anchorUuid, neighbourUuid], ignoreOrder: true);
        JsonElement anchorItem = items.Single(i => i.GetProperty("uuid").GetGuid() == anchorUuid);
        anchorItem.GetProperty("statement").GetString().ShouldBe("Anchor claim");
        anchorItem.GetProperty("bodyText").GetString().ShouldBe(body);
        JsonElement edge = bundle.GetProperty("edges").EnumerateArray().Single();
        edge.GetProperty("sourceUuid").GetGuid().ShouldBe(anchorUuid);
        edge.GetProperty("targetUuid").GetGuid().ShouldBe(neighbourUuid);
        JsonElement manifest = bundle.GetProperty("manifest");
        manifest.GetProperty("noMatch").GetBoolean().ShouldBeFalse();
        manifest.GetProperty("selectedCount").GetInt32().ShouldBe(2);
        manifest.GetProperty("reach").GetProperty("anchors").GetInt32().ShouldBe(1);
        manifest.GetProperty("reach").GetProperty("widened").GetInt32().ShouldBe(1);

        JsonElement preview = await PostJson(read, "/api/context/dossier/preview", anchor);

        preview.GetProperty("noMatch").GetBoolean().ShouldBeFalse();
        preview.GetProperty("volume").GetProperty("selected").GetInt32().ShouldBe(2);
        preview.GetProperty("volume").GetProperty("edges").GetInt32().ShouldBe(1);
    }

    private static object Item(Guid createUuid, string description, string statement, string[] tags, string? content) => new
    {
        uuid = (Guid?)null,
        createUuid,
        name = description,
        description,
        statement,
        contentSummary = "Summary",
        kind = MemoryVersion.KindValue.Decision,
        facets = new[] { "architecture" },
        tags,
        status = MemoryVersion.MemoryVersionStatus.Approved,
        confidence = (short)80,
        content,
        sources = Array.Empty<object>(),
        validFrom = DateTimeOffset.UtcNow.AddDays(-1),
        validUntil = (DateTimeOffset?)null,
        summaryModel = "dossier-api-test-model",
        summaryPromptVersion = "1",
    };

    private async Task<JsonElement> PostJson(HttpClient client, string route, object body)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(route, body, Json, Ct);
        string payload = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, payload);
        return JsonSerializer.Deserialize<JsonElement>(payload, Json);
    }
}
