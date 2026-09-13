using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Export;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.Export;

public class ExportStoreValidatorTests
{
    private readonly ExportStore.Validator _validator = new();

    [Fact]
    public void Rejects_empty_output_directory()
    {
        _validator.TestValidate(new ExportStore.Request("")).ShouldHaveValidationErrorFor(x => x.OutputDirectory);
    }

    [Fact]
    public void Accepts_output_directory()
    {
        _validator.TestValidate(new ExportStore.Request(".context/export")).ShouldNotHaveValidationErrorFor(x => x.OutputDirectory);
    }
}
