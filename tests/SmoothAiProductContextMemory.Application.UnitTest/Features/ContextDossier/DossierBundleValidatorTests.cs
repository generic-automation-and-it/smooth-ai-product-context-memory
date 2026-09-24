using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.ContextDossier;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.ContextDossier;

public class DossierBundleValidatorTests
{
    private readonly CreateDossierBundle.Validator _bundleValidator = new();
    private readonly CreateDossierPreview.Validator _previewValidator = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MemoryTraversalDefaults.MaxDepth + 1)]
    public void Bundle_rejects_widen_depth_outside_the_bound(int widenDepth)
    {
        var result = _bundleValidator.TestValidate(Bundle() with { WidenDepth = widenDepth });
        result.ShouldHaveValidationErrorFor(x => x.WidenDepth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(MemoryTraversalDefaults.MaxDepth)]
    public void Bundle_accepts_widen_depth_within_the_bound(int widenDepth)
    {
        var result = _bundleValidator.TestValidate(Bundle() with { WidenDepth = widenDepth });
        result.ShouldNotHaveValidationErrorFor(x => x.WidenDepth);
    }

    [Fact]
    public void Bundle_rejects_absent_widen_depth()
    {
        // The default for an int is 0, which is the "absent" case — a caller that never stated a
        // bound gets a refusal, never a server-side default (HLD-005 LADR-03 / NFR-03).
        var result = _bundleValidator.TestValidate(Bundle() with { WidenDepth = default });
        result.ShouldHaveValidationErrorFor(x => x.WidenDepth);
    }

    [Fact]
    public void Bundle_rejects_ticket_key_without_provider()
    {
        var result = _bundleValidator.TestValidate(Bundle() with { TicketKey = "ACM-1" });
        result.ShouldHaveValidationErrorFor(x => x.TicketProvider);
    }

    [Fact]
    public void Bundle_rejects_ticket_provider_without_key()
    {
        var result = _bundleValidator.TestValidate(Bundle() with { TicketProvider = "jira" });
        result.ShouldHaveValidationErrorFor(x => x.TicketKey);
    }

    [Fact]
    public void Bundle_accepts_a_bounded_request()
    {
        var result = _bundleValidator.TestValidate(Bundle());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Preview_rejects_widen_depth_outside_the_bound(int widenDepth)
    {
        var result = _previewValidator.TestValidate(Preview() with { WidenDepth = widenDepth });
        result.ShouldHaveValidationErrorFor(x => x.WidenDepth);
    }

    [Fact]
    public void Preview_accepts_a_bounded_request()
    {
        var result = _previewValidator.TestValidate(Preview());
        result.ShouldNotHaveAnyValidationErrors();
    }

    private static CreateDossierBundle.Request Bundle() =>
        new(
            Repo: null,
            InitiativeName: null,
            TicketProvider: null,
            TicketKey: null,
            Tags: null,
            Kind: null,
            Status: null,
            ScopeDimension: null,
            IncludeHistory: false,
            AsOf: null,
            WidenDepth: 3);

    private static CreateDossierPreview.Request Preview() =>
        new(
            Repo: null,
            InitiativeName: null,
            TicketProvider: null,
            TicketKey: null,
            Tags: null,
            Kind: null,
            Status: null,
            ScopeDimension: null,
            IncludeHistory: false,
            AsOf: null,
            WidenDepth: 3);
}
