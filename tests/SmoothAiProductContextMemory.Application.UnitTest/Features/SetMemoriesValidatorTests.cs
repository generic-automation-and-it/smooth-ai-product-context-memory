using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class SetMemoriesValidatorTests
{
    private readonly SetMemories.Validator _validator = new();

    [Theory]
    [InlineData("document_approval", 1, true)]
    [InlineData("approved_document", 1, false)]
    [InlineData("document_approval", 2, false)]
    public void Document_approval_uses_the_explicit_shape_one_evidence_contract(string category, int shape, bool accepted)
    {
        var evidence = new EvidenceDocument { V = shape, Category = category };
        var item = new SetMemories.MemoryWrite(null, "Document approval", "Design document", "The design document is approved.", "Document approval", "decision", null, null, "proposed", 50, "Synthetic source", [new SourceInput("conversation", "synthetic://document-approval", null, evidence)], DateTimeOffset.UtcNow, null, null, null);
        var result = _validator.TestValidate(new SetMemories.Request(Guid.NewGuid(), [item], null, null, DryRun: true));
        if (accepted) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor("Items[0].Sources[0].Evidence");
    }

    [Fact]
    public void Rejects_empty_group_and_blank_item()
    {
        var request = new SetMemories.Request(
            Guid.Empty,
            [
                new SetMemories.MemoryWrite(
                    null,
                    "",
                    "",
                    "",
                    "",
                    "",
                    null,
                    null,
                    "nope",
                    200,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null)
            ],
            null,
            null);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.GroupUuid);
        result.ShouldHaveValidationErrorFor("Items[0].Name");
        result.ShouldHaveValidationErrorFor("Items[0].Status");
    }

    [Fact]
    public void Rejects_self_link()
    {
        Guid id = Guid.NewGuid();
        var request = new SetMemories.Request(
            Guid.NewGuid(),
            [
                new SetMemories.MemoryWrite(
                    null,
                    "n",
                    "d",
                    "s",
                    "c",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null)
            ],
            [new SetMemories.LinkWrite(id, id, MemoryRelation.RelatesTo, "loop")],
            null);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Links[0]");
    }

    /// <summary>An empty batch is a caller bug, not a no-op write.</summary>
    [Fact]
    public void Rejects_empty_item_list()
    {
        var request = new SetMemories.Request(Guid.NewGuid(), [], null, null);

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Rejects_overlong_relation_and_inverted_validity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var request = new SetMemories.Request(
            Guid.NewGuid(),
            [
                new SetMemories.MemoryWrite(
                    null,
                    "n",
                    "d",
                    "s",
                    "c",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    null,
                    null,
                    now,
                    now.AddDays(-1),
                    null,
                    null)
            ],
            [new SetMemories.LinkWrite(Guid.NewGuid(), Guid.NewGuid(), new string('x', 33), "why")],
            null);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Items[0].ValidUntil");
        result.ShouldHaveValidationErrorFor("Links[0].Relation");
    }

    [Fact]
    public void Accepts_valid_write()
    {
        var request = new SetMemories.Request(
            Guid.NewGuid(),
            [
                new SetMemories.MemoryWrite(
                    null,
                    "Name",
                    "Subject",
                    "Claim",
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    ["architecture"],
                    ["tag"],
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    "body",
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    "model",
                    "v1")
            ],
            null,
            null);

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Rejects_overlong_labels_proposed()
    {
        var request = new SetMemories.Request(
            Guid.NewGuid(),
            [
                new SetMemories.MemoryWrite(
                    null,
                    "Name",
                    "Subject",
                    "Claim",
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null)
            ],
            null,
            [new string('x', 101)]);

        _validator.TestValidate(request).ShouldHaveValidationErrorFor("LabelsProposed[0]");
    }

    [Fact]
    public void Rejects_letter_free_description()
    {
        var request = new SetMemories.Request(
            Guid.NewGuid(),
            [
                new SetMemories.MemoryWrite(
                    null,
                    "Name",
                    "!!! ###",
                    "Claim",
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null)
            ],
            null,
            null);

        _validator.TestValidate(request).ShouldHaveValidationErrorFor("Items[0].Description");
    }

    [Fact]
    public void Rejects_version_and_create_identity_together()
    {
        SetMemories.Request request = ValidRequest();
        request = request with
        {
            Items = [request.Items[0] with { Uuid = Guid.NewGuid(), CreateUuid = Guid.NewGuid() }],
        };

        _validator.TestValidate(request).ShouldHaveValidationErrorFor("Items[0]");
    }

    [Fact]
    public void Rejects_empty_create_identity_and_overlong_link_reason()
    {
        SetMemories.Request request = ValidRequest();
        request = request with
        {
            Items = [request.Items[0] with { CreateUuid = Guid.Empty }],
            Links = [new(Guid.NewGuid(), Guid.NewGuid(), MemoryRelation.RelatesTo, new string('x', 4001))],
        };

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Items[0].CreateUuid");
        result.ShouldHaveValidationErrorFor("Links[0].Reason");
    }

    /// <summary>The vocabulary of kinds is open: the understanding kind and any unregistered kind both validate.</summary>
    [Fact]
    public void Accepts_understanding_and_unregistered_kinds()
    {
        SetMemories.Request baseRequest = ValidRequest();
        SetMemories.MemoryWrite baseItem = baseRequest.Items[0];

        _validator.TestValidate(baseRequest with { Items = [baseItem with { Kind = "understanding" }] })
            .ShouldNotHaveAnyValidationErrors();

        _validator.TestValidate(baseRequest with { Items = [baseItem with { Kind = "never-registered-kind" }] })
            .ShouldNotHaveAnyValidationErrors();
    }

    private static SetMemories.Request ValidRequest() =>
        new(
            Guid.NewGuid(),
            [
                new SetMemories.MemoryWrite(
                    null,
                    "Name",
                    "Subject",
                    "Claim",
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null)
            ],
            null,
            null);
}
