using SmoothAiProductContextMemory.AppHost;

namespace SmoothAiProductContextMemory.AppHost.UnitTest;

public class ContainerImageReferenceTests
{
    [Theory]
    [InlineData("postgres", "postgres", null)]
    [InlineData("postgres:17", "postgres", "17")]
    [InlineData("registry.example:5000/team/api:1.2.3", "registry.example:5000/team/api", "1.2.3")]
    public void Parse_supports_names_registries_and_tags(string value, string image, string? tag)
    {
        ContainerImageReference result = ContainerImageReference.Parse(value);

        result.Image.ShouldBe(image);
        result.Tag.ShouldBe(tag);
    }

    [Fact]
    public void Parse_preserves_digest_reference_as_image()
    {
        const string value = "ghcr.io/example/api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        ContainerImageReference result = ContainerImageReference.Parse(value);

        result.Image.ShouldBe(value);
        result.Tag.ShouldBeNull();
        ContainerImageReference.IsDigestPinned(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData("ghcr.io/example/api:latest")]
    [InlineData("ghcr.io/example/api@sha256:abc")]
    [InlineData("ghcr.io/example/api@sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void IsDigestPinned_rejects_mutable_or_invalid_references(string value)
    {
        ContainerImageReference.IsDigestPinned(value).ShouldBeFalse();
    }
}
