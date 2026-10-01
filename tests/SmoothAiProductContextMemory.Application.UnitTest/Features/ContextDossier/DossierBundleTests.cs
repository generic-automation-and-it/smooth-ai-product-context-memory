using System.Reflection;
using System.Text.Json;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.ContextDossier;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.ContextDossier;

public class DossierBundleTests
{
    private static readonly DateTimeOffset ValidFrom = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedOn = new(2024, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bundle_payload_contains_no_generation_timestamp()
    {
        // A generation time is variance and would break byte-equality on the first run (NFR-02). The
        // only clock values allowed are stored times (business validity / capture) and the caller's
        // as-of input — never when the bundle was produced.
        Type[] types =
        [
            typeof(DossierBundle), typeof(DossierManifest), typeof(DossierSelectionPlan), typeof(DossierMemory),
            typeof(DossierReach), typeof(DossierVolume), typeof(DossierOmittedItem), typeof(DossierEdge),
            typeof(DossierCostEstimate),
        ];

        foreach (Type type in types)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                property.Name.ShouldNotMatch("(?i)^(generated|exported|produced|assembled)",
                    $"Type {type.Name} must not carry a generation-time field '{property.Name}'.");
            }
        }
    }

    [Fact]
    public void Bounded_omission_reasons_are_fixed_and_reject_unknown()
    {
        DossierOmissionReason.IsValid(DossierOmissionReason.CapReached).ShouldBeTrue();
        DossierOmissionReason.IsValid(DossierOmissionReason.DepthReached).ShouldBeTrue();
        DossierOmissionReason.IsValid(DossierOmissionReason.UnreadableBody).ShouldBeTrue();
        DossierOmissionReason.IsValid(DossierOmissionReason.CollapsedIntoAnotherClaim).ShouldBeTrue();
        DossierOmissionReason.IsValid(DossierOmissionReason.HiddenByScope).ShouldBeTrue();

        // An unknown value fails — free text is forbidden (NFR-04).
        DossierOmissionReason.IsValid("unknown").ShouldBeFalse();
        DossierOmissionReason.IsValid("").ShouldBeFalse();
    }

    [Fact]
    public void Ordering_tiebreak_is_validity_then_capture_then_identity()
    {
        Guid a = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid b = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        // Same validity and capture time -> identity tiebreak.
        CheapMemory first = Memory(a, ValidFrom, CreatedOn);
        CheapMemory second = Memory(b, ValidFrom, CreatedOn);
        DossierSelection.CompareByProvenance(first, second).ShouldBeLessThan(0);
        DossierSelection.CompareByProvenance(second, first).ShouldBeGreaterThan(0);
        DossierSelection.CompareByProvenance(first, first).ShouldBe(0);

        // Later validity wins before capture time.
        CheapMemory laterValidity = Memory(a, ValidFrom.AddDays(1), CreatedOn);
        DossierSelection.CompareByProvenance(first, laterValidity).ShouldBeLessThan(0);

        // Same validity -> earlier capture wins.
        CheapMemory laterCapture = Memory(b, ValidFrom, CreatedOn.AddDays(1));
        DossierSelection.CompareByProvenance(Memory(b, ValidFrom, CreatedOn), laterCapture).ShouldBeLessThan(0);
    }

    [Fact]
    public void Kind_understanding_orders_and_carries_like_any_kind()
    {
        // LADR-15: an understanding is a kind like any other — the tiebreak ignores kind, so changing
        // only the kind on identical provenance yields the same order (no kind-specific rule).
        CheapMemory baseMemory = Memory(Guid.NewGuid(), ValidFrom, CreatedOn);
        CheapMemory understanding = baseMemory with { Kind = MemoryVersion.KindValue.Understanding };
        CheapMemory decision = baseMemory with { Kind = MemoryVersion.KindValue.Decision };

        DossierSelection.CompareByProvenance(understanding, decision).ShouldBe(0);
        DossierSelection.CompareByProvenance(decision, understanding).ShouldBe(0);
    }

    [Fact]
    public void Mechanical_collapse_merges_same_blob_and_reports_the_collapsed()
    {
        var items = new List<DossierMemory>
        {
            MemoryItem(Guid.NewGuid(), Guid.NewGuid(), "the shared hypothesis", isCollapsed: false),
            MemoryItem(Guid.NewGuid(), Guid.NewGuid(), "the shared hypothesis", isCollapsed: false),
            MemoryItem(Guid.NewGuid(), Guid.NewGuid(), "a distinct claim", isCollapsed: false),
        };
        var omitted = new List<DossierOmittedItem>();

        (List<DossierMemory> kept, List<DossierOmittedItem> reported) =
            CreateDossierBundle.Handler.CollapseSameBlob(items, omitted);

        kept.Count.ShouldBe(2);
        reported.Count.ShouldBe(1);
        reported[0].Reason.ShouldBe(DossierOmissionReason.CollapsedIntoAnotherClaim);
        kept.Count(i => i.BodyText == "the shared hypothesis").ShouldBe(1);
    }

    [Fact]
    public void Manifest_count_equals_bundle_item_count()
    {
        // NFR-04: the reconciliation starts from a trustworthy left-hand side — the manifest's own
        // selected count equals the items present plus the items omitted with a reason.
        var items = new List<DossierMemory> { MemoryItem(Guid.NewGuid(), Guid.NewGuid(), "claim one", isCollapsed: false) };
        var omitted = new List<DossierOmittedItem>
        {
            new(Guid.NewGuid(), Version: 1, DossierOmissionReason.UnreadableBody),
        };

        DossierBundle bundle = new(
            items,
            Edges: [],
            omitted,
            Manifest: new DossierManifest(
                Selection: Plan(),
                SelectedCount: items.Count + omitted.Count,
                Reach: Reach(),
                LimitsHit: [],
                NoMatch: false));

        bundle.Manifest.SelectedCount.ShouldBe(bundle.Items.Count + bundle.Omitted.Count);
    }

    [Fact]
    public void Bundle_payload_is_byte_identical_for_identical_data()
    {
        // Two structurally identical bundles serialize to the same bytes — no hidden variance from a
        // generation timestamp or run-dependent ordering (NFR-02).
        DossierBundle left = SampleBundle();
        DossierBundle right = SampleBundle();

        byte[] leftBytes = JsonSerializer.SerializeToUtf8Bytes(left, JsonOptions);
        byte[] rightBytes = JsonSerializer.SerializeToUtf8Bytes(right, JsonOptions);
        leftBytes.ShouldBe(rightBytes);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static DossierBundle SampleBundle()
    {
        // Fixed identities so two independently-constructed bundles are byte-identical.
        Guid uuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid groupUuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        return new DossierBundle(
            [MemoryItem(uuid, groupUuid, "a claim", isCollapsed: false)],
            Edges: [],
            Omitted: [],
            Manifest: new DossierManifest(
                Selection: Plan(),
                SelectedCount: 1,
                Reach: Reach(),
                LimitsHit: [],
                NoMatch: false));
    }

    private static DossierSelectionPlan Plan() =>
        new(
            Repo: null, InitiativeName: null, TicketProvider: null, TicketKey: null, Tags: [],
            Kind: null, Status: null, ScopeDimension: null, IncludeHistory: false, AsOf: null,
            WidenDepth: 3, CombinationRule: DossierCombinationRule.Value,
            HistoryPolicy: "current-only", RetrievalPolicy: "current-only");

    [Theory]
    [InlineData(null, "current-only, proposed-excluded")]
    [InlineData("", "current-only, proposed-excluded")]
    [InlineData("proposed", "current-only, status=proposed")]
    [InlineData("approved", "current-only, status=approved")]
    public void RetrievalPolicy_DescribesWhatTheAnchorActuallySelects(string? status, string expected)
    {
        // The manifest is the record of the effective selection, sufficient to repeat it (BR-20), so
        // its policy clause must name only capabilities the request can exercise. It read
        // "proposed-excluded unless status requested" while the traversal had no status field at all,
        // so `ticket + status=proposed` always returned noMatch and the manifest promised the
        // opposite. Derived from the anchor now, so the two cannot drift again.
        DossierAnchor anchor = new(
            Repo: null, InitiativeName: null, Ticket: null, Tags: [], Kind: null, Status: status,
            ScopeDimension: null, IncludeHistory: false, AsOf: null, WidenDepth: 3, ItemLimit: 200);

        DossierSelection.BuildPlan(anchor).RetrievalPolicy.ShouldBe(expected);
    }

    [Fact]
    public void RetrievalPolicy_NeverPromisesAnOptInThatWasNotRequested()
    {
        // The regression this replaces, stated as an assertion: with no status in the anchor, the
        // clause must not mention requesting one.
        DossierAnchor anchor = new(
            Repo: null, InitiativeName: null, Ticket: null, Tags: [], Kind: null, Status: null,
            ScopeDimension: null, IncludeHistory: false, AsOf: null, WidenDepth: 3, ItemLimit: 200);

        DossierSelection.BuildPlan(anchor).RetrievalPolicy
            .ShouldNotContain("unless status requested");
    }


    private static DossierReach Reach() =>
        new(WidenDepth: 3, Anchors: 1, Widened: 0, Selected: 1, Edges: 0, HiddenPathDropped: false);

    private static DossierMemory MemoryItem(Guid uuid, Guid groupUuid, string body, bool isCollapsed) =>
        new(
            Uuid: uuid,
            GroupUuid: groupUuid,
            Name: "name",
            Description: "description",
            Statement: body,
            ContentSummary: "summary",
            Kind: MemoryVersion.KindValue.Decision,
            Status: MemoryVersion.MemoryVersionStatus.Approved,
            Confidence: 80,
            ScopeDimension: MemoryGroup.ScopeDimensionValue.Product,
            ScopeIdentifier: null,
            ValidFrom: ValidFrom,
            ValidUntil: null,
            Version: 1,
            IsCurrent: true,
            CreatedOn: CreatedOn,
            Sources: [],
            BodyText: body,
            BodyState: DossierBodyState.Inlined,
            ReachedVia: ["anchor"]);

    private static CheapMemory Memory(Guid uuid, DateTimeOffset validFrom, DateTimeOffset createdOn) =>
        new(
            Uuid: uuid,
            GroupUuid: Guid.NewGuid(),
            Name: "name",
            Description: "description",
            Statement: "statement",
            ContentSummary: "summary",
            Kind: MemoryVersion.KindValue.Decision,
            Facets: [],
            Tags: [],
            Status: MemoryVersion.MemoryVersionStatus.Approved,
            Confidence: 80,
            ScopeDimension: MemoryGroup.ScopeDimensionValue.Product,
            ScopeIdentifier: null,
            ValidFrom: validFrom,
            ValidUntil: null,
            Version: 1,
            IsCurrent: true,
            Sources: [],
            CreatedOn: createdOn);

    [Fact]
    public void BuildLimitsHit_reports_cap_when_selection_fills_the_fetch_ceiling()
    {
        // The anchor search fills the fetch ceiling without raising the widen/ticket limit flag, so a
        // selection truncated to that ceiling must still report CapReached rather than present as
        // unbounded. Both slices compute it from Selected.Count, so the convergence is preserved.
        DossierAnchor anchor = Anchor(DossierDefaults.ItemLimit);
        DossierSelectionResult selection = Selection(count: DossierDefaults.ItemLimit, limitReached: false);

        DossierSelection.BuildLimitsHit(selection, anchor)
            .ShouldContain(h => h.Limit == DossierOmissionReason.CapReached);
    }

    [Fact]
    public void BuildLimitsHit_reports_no_cap_below_the_ceiling()
    {
        DossierAnchor anchor = Anchor(DossierDefaults.ItemLimit);
        DossierSelectionResult selection = Selection(count: 5, limitReached: false);

        DossierSelection.BuildLimitsHit(selection, anchor).ShouldBeEmpty();
    }

    [Fact]
    public void BuildLimitsHit_reports_cap_when_the_widen_limit_flag_is_raised()
    {
        DossierAnchor anchor = Anchor(DossierDefaults.ItemLimit);
        DossierSelectionResult selection = Selection(count: 5, limitReached: true);

        DossierSelection.BuildLimitsHit(selection, anchor)
            .ShouldContain(h => h.Limit == DossierOmissionReason.CapReached);
    }

    private static DossierAnchor Anchor(int itemLimit) =>
        new(null, null, null, [], null, null, null, false, null, 3, itemLimit);

    private static DossierSelectionResult Selection(int count, bool limitReached) =>
        new(
            Selected: [.. Enumerable.Range(0, count).Select(i =>
                new SelectedClaim(Memory(Guid.NewGuid(), ValidFrom, CreatedOn), []))],
            AnchorCount: count,
            WidenedCount: 0,
            EdgeCount: 0,
            DepthLimitReached: false,
            HiddenPathDropped: false,
            LimitReached: limitReached,
            Edges: []);
}
