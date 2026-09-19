using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.RecallFeedback;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.RecallFeedback;

public class RecallFeedbackRequestValidatorTests
{
    private readonly GetMissRate.Validator _missRate = new();
    private readonly GetNeverRecalledMemories.Validator _neverRecalled = new();

    [Fact]
    public void Miss_rate_rejects_to_before_from()
    {
        var from = DateTimeOffset.UtcNow;
        _missRate.TestValidate(new GetMissRate.Request(from, from.AddDays(-1)))
            .ShouldHaveValidationErrorFor(x => x.To);
        _missRate.TestValidate(new GetMissRate.Request(from, from.AddDays(1)))
            .ShouldNotHaveValidationErrorFor(x => x.To);
    }

    [Fact]
    public void Never_recalled_rejects_limit_outside_the_range()
    {
        _neverRecalled.TestValidate(new GetNeverRecalledMemories.Request(DateTimeOffset.UtcNow, 0))
            .ShouldHaveValidationErrorFor(x => x.Limit);
        _neverRecalled.TestValidate(new GetNeverRecalledMemories.Request(DateTimeOffset.UtcNow, 5001))
            .ShouldHaveValidationErrorFor(x => x.Limit);
        _neverRecalled.TestValidate(new GetNeverRecalledMemories.Request(DateTimeOffset.UtcNow, null))
            .ShouldNotHaveValidationErrorFor(x => x.Limit);
    }
}
