using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Memories;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class GetMemoryBlobValidatorTests
{
    private readonly GetMemoryBlob.Validator _validator = new();

    [Fact]
    public void Requires_nonempty_uuid()
    {
        _validator.TestValidate(new GetMemoryBlob.Request(Guid.Empty, 1))
            .ShouldHaveValidationErrorFor(x => x.Uuid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Requires_positive_version(int version)
    {
        _validator.TestValidate(new GetMemoryBlob.Request(Guid.NewGuid(), version))
            .ShouldHaveValidationErrorFor(x => x.Version);
    }

    [Fact]
    public void Accepts_valid_request()
    {
        _validator.TestValidate(new GetMemoryBlob.Request(Guid.NewGuid(), 1, "product"))
            .ShouldNotHaveAnyValidationErrors();
    }
}

public class GetMemoryVersionsValidatorTests
{
    private readonly GetMemoryVersions.Validator _validator = new();

    [Fact]
    public void Requires_nonempty_uuid()
    {
        _validator.TestValidate(new GetMemoryVersions.Request(Guid.Empty))
            .ShouldHaveValidationErrorFor(x => x.Uuid);
    }

    [Fact]
    public void Accepts_valid_request()
    {
        _validator.TestValidate(new GetMemoryVersions.Request(Guid.NewGuid(), "product"))
            .ShouldNotHaveAnyValidationErrors();
    }
}
