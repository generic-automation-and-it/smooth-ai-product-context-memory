using System.Net;
using System.Net.Http.Json;
using Shouldly;
using SmoothAiProductContextMemory.Knowledge;
using SmoothAiProductContextMemory.KnowledgeHost;
using Xunit;

namespace SmoothAiProductContextMemory.Knowledge.UnitTest;

public sealed class CoreCompatibilityTests
{
    [Fact]
    public async Task Reader_preserves_product_and_ticket_metadata_from_main()
    {
        var uuid = Guid.NewGuid();
        using var http = new HttpClient(new ResponseHandler(new
        {
            items = new[] { new
            {
                uuid, groupUuid = Guid.NewGuid(), version = 1, name = "Rule", description = "rule",
                statement = "A rule", contentSummary = "A rule", kind = "rule", status = "approved",
                confidence = 80, scopeDimension = "product", validFrom = "2026-10-24T00:00:00Z",
                createdOn = "2026-10-24T00:00:00Z", isCurrent = true,
                facets = Array.Empty<string>(), tags = Array.Empty<string>(), sources = Array.Empty<Source>(),
                hasBody = false, productMatch = "initiative", sharedInitiative = true,
                ticketMatch = "reference", tickets = new[] { new { provider = "local", key = "T-1", match = "reference" } },
                repository = "synthetic/repo", initiativeName = "Compatibility"
            } }
        }))
        { BaseAddress = new Uri("http://test.invalid/") };
        var evidence = (await new CoreReader(http).SearchAsync("rule", new Selectors(), DateTimeOffset.UtcNow, false, TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        evidence.Uuid.ShouldBe(uuid);
        evidence.ProductMatch.ShouldBe("initiative");
        evidence.SharedInitiative.ShouldBeTrue();
        evidence.Tickets!.ShouldHaveSingleItem().Match.ShouldBe("reference");
        evidence.Repository.ShouldBe("synthetic/repo");
    }

    [Fact]
    public void Unchanged_commit_receipts_roundtrip_without_losing_the_noop_result()
    {
        var result = new CommitResult(0, 0, 0, 0, 0, 0,
            [new CommitItem(Guid.NewGuid(), null, false, true)], Unchanged: 1);
        var restored = KnowledgeJson.Deserialize<CommitResult>(KnowledgeJson.Serialize(result));
        restored.Unchanged.ShouldBe(1);
        restored.Items.ShouldHaveSingleItem().Unchanged.ShouldBeTrue();
        restored.Created.ShouldBe(0);
    }

    private sealed class ResponseHandler(object body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
    }
}
