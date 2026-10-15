using NodaTime;
using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class SetMemoriesValidatorTests
{
    private readonly SetMemories.Validator _validator = new();

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
                    SystemClock.Instance.GetCurrentInstant(),
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
                    MemoryVersion.MemoryVersionStatus.Proposed,
                    80,
                    null,
                    null,
                    SystemClock.Instance.GetCurrentInstant(),
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
        Instant now = SystemClock.Instance.GetCurrentInstant();
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
                    MemoryVersion.MemoryVersionStatus.Proposed,
                    80,
                    null,
                    null,
                    now,
                    now.Plus(Duration.FromDays(-1)),
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
                    MemoryVersion.MemoryVersionStatus.Proposed,
                    80,
                    "body",
                    null,
                    SystemClock.Instance.GetCurrentInstant(),
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
                    MemoryVersion.MemoryVersionStatus.Proposed,
                    80,
                    null,
                    null,
                    SystemClock.Instance.GetCurrentInstant(),
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
                    MemoryVersion.MemoryVersionStatus.Proposed,
                    80,
                    null,
                    null,
                    SystemClock.Instance.GetCurrentInstant(),
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

    /// <summary>LADR-11: Status=approved is a transition, not a create. The validator requires Uuid and forbids CreateUuid when Status=approved.</summary>
    [Fact]
    public void Rejects_approved_without_uuid()
    {
        SetMemories.Request request = ValidRequest() with
        {
            Items = [ValidRequest().Items[0] with
            {
                Status = MemoryVersion.MemoryVersionStatus.Approved,
                Uuid = null,
            }],
        };

        _validator.TestValidate(request).ShouldHaveValidationErrorFor("Items[0]");
    }

    [Fact]
    public void Rejects_approved_with_create_uuid()
    {
        SetMemories.Request request = ValidRequest() with
        {
            Items = [ValidRequest().Items[0] with
            {
                Status = MemoryVersion.MemoryVersionStatus.Approved,
                Uuid = null,
                CreateUuid = Guid.NewGuid(),
            }],
        };

        _validator.TestValidate(request).ShouldHaveValidationErrorFor("Items[0]");
    }

    [Fact]
    public void Accepts_approved_with_uuid()
    {
        SetMemories.Request request = ValidRequest() with
        {
            Items = [ValidRequest().Items[0] with
            {
                Status = MemoryVersion.MemoryVersionStatus.Approved,
                Uuid = Guid.NewGuid(),
                CreateUuid = null,
            }],
        };

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
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
                    MemoryVersion.MemoryVersionStatus.Proposed,
                    80,
                    null,
                    null,
                    SystemClock.Instance.GetCurrentInstant(),
                    null,
                    null,
                    null)
            ],
            null,
            null);
}
