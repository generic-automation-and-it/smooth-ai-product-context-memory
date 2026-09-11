using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Memories;
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
            [new SetMemories.LinkWrite(id, id, MemoryLink.RelationValue.RelatesTo, "loop")],
            null);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Links[0]");
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
}
