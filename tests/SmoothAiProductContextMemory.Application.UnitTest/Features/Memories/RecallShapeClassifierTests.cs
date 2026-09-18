using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.Memories;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.Memories;

public class RecallShapeClassifierTests
{
    private static QueryMemories.Request Base() =>
        new(null, null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void Classifies_each_bounded_shape()
    {
        RecallShapeClassifier.Classify(Base() with { Query = "storage" })
            .ShouldBe(RetrievalShape.FreeText);

        RecallShapeClassifier.Classify(Base() with { Facets = ["storage"] })
            .ShouldBe(RetrievalShape.FacetOnly);

        RecallShapeClassifier.Classify(
                Base() with { TicketProvider = "jira", TicketKey = "ACM-1" })
            .ShouldBe(RetrievalShape.TicketScoped);

        RecallShapeClassifier.Classify(Base() with { GroupUuid = Guid.NewGuid() })
            .ShouldBe(RetrievalShape.GroupScoped);

        RecallShapeClassifier.Classify(Base())
            .ShouldBe(RetrievalShape.Unfiltered);
    }

    [Fact]
    public void Scope_wins_over_free_text()
    {
        RecallShapeClassifier.Classify(
                Base() with { Query = "storage", TicketProvider = "jira", TicketKey = "ACM-1" })
            .ShouldBe(RetrievalShape.TicketScoped);

        RecallShapeClassifier.Classify(
                Base() with { Query = "storage", GroupUuid = Guid.NewGuid() })
            .ShouldBe(RetrievalShape.GroupScoped);
    }

    [Fact]
    public void Facet_only_requires_no_other_filter()
    {
        RecallShapeClassifier.Classify(Base() with { Facets = ["storage"], Kind = "decision" })
            .ShouldBe(RetrievalShape.Unfiltered);
    }

    [Fact]
    public void Never_produces_an_out_of_set_value()
    {
        var requests = new[]
        {
            Base(),
            Base() with { Query = "a" },
            Base() with { Facets = ["a"] },
            Base() with { Tags = ["a"] },
            Base() with { Kind = "a" },
            Base() with { ScopeDimension = "a" },
            Base() with { GroupUuid = Guid.NewGuid() },
            Base() with { TicketProvider = "p", TicketKey = "k" },
        };

        foreach (QueryMemories.Request request in requests)
        {
            RetrievalShape.IsKnown(RecallShapeClassifier.Classify(request)).ShouldBeTrue(request.ToString());
        }
    }
}
