using Microsoft.Extensions.Configuration;
using SmoothAiProductContextMemory.AppHost;

namespace SmoothAiProductContextMemory.AppHost.UnitTest;

public class AppHostConfigurationTests
{
    private const string DigestImage = "ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Development_mode_preserves_existing_defaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        AppHostConfiguration result = AppHostConfiguration.Create(configuration);

        result.Mode.ShouldBe(AppHostMode.Development);
        result.UseProject.ShouldBeTrue();
        result.PostgresContainerName.ShouldBe("mimisbrunnr-postgres");
        result.PostgresDataVolume.ShouldBe("mimisbrunnr-postgres-data");
        result.EngineKind.ShouldBe("docker");
        result.EngineHostAddress.ShouldBe("host.docker.internal");
    }

    [Fact]
    public void Release_mode_requires_image_mode()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AppHostConfiguration:Mode"] = "Release",
        });

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            AppHostConfiguration.Create(configuration));

        exception.Message.ShouldContain("UseProject=false");
    }

    [Fact]
    public void Release_mode_requires_digest_pinned_host_image()
    {
        Dictionary<string, string?> values = ReleaseValues();
        values["HostConfiguration:Image"] = "ghcr.io/example/api:latest";

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            AppHostConfiguration.Create(BuildConfiguration(values)));

        exception.Message.ShouldContain("sha256 digest");
    }

    [Theory]
    [InlineData("PostgresConfiguration:Password")]
    [InlineData("BlobConfiguration:AccessKey")]
    [InlineData("BlobConfiguration:SecretKey")]
    public void Release_mode_requires_explicit_credentials(string key)
    {
        Dictionary<string, string?> values = ReleaseValues();
        values.Remove(key);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            AppHostConfiguration.Create(BuildConfiguration(values)));

        exception.Message.ShouldContain(key);
    }

    [Fact]
    public void Release_mode_scopes_names_to_installation()
    {
        Dictionary<string, string?> values = ReleaseValues();
        values["InstallationConfiguration:Id"] = "team-a";

        AppHostConfiguration result = AppHostConfiguration.Create(BuildConfiguration(values));

        result.IsRelease.ShouldBeTrue();
        result.PostgresContainerName.ShouldBe("mimisbrunnr-team-a-postgres");
        result.BlobDataVolume.ShouldBe("mimisbrunnr-team-a-blob-well-data");
        result.DockerDesktopGroupName.ShouldBe("smooth-mímisbrunnr-release-team-a");
    }

    [Theory]
    [InlineData("Uppercase")]
    [InlineData("has_underscore")]
    [InlineData("-leading")]
    public void Installation_id_rejects_unsafe_values(string installationId)
    {
        Dictionary<string, string?> values = ReleaseValues();
        values["InstallationConfiguration:Id"] = installationId;

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            AppHostConfiguration.Create(BuildConfiguration(values)));

        exception.Message.ShouldContain("InstallationConfiguration:Id");
    }

    [Fact]
    public void Podman_defaults_to_its_container_host_alias()
    {
        Dictionary<string, string?> values = ReleaseValues();
        values["EngineConfiguration:Kind"] = "podman";

        AppHostConfiguration result = AppHostConfiguration.Create(BuildConfiguration(values));

        result.EngineKind.ShouldBe("podman");
        result.EngineHostAddress.ShouldBe("host.containers.internal");
    }

    [Theory]
    [InlineData("containerd")]
    [InlineData("Docker")]
    public void Engine_kind_rejects_unsupported_values(string engineKind)
    {
        Dictionary<string, string?> values = ReleaseValues();
        values["EngineConfiguration:Kind"] = engineKind;

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            AppHostConfiguration.Create(BuildConfiguration(values)));

        exception.Message.ShouldContain("EngineConfiguration:Kind");
    }

    [Fact]
    public void Engine_host_address_rejects_a_uri()
    {
        Dictionary<string, string?> values = ReleaseValues();
        values["EngineConfiguration:HostAddress"] = "http://host.docker.internal";

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            AppHostConfiguration.Create(BuildConfiguration(values)));

        exception.Message.ShouldContain("HostAddress");
    }

    private static Dictionary<string, string?> ReleaseValues() => new()
    {
        ["AppHostConfiguration:Mode"] = "Release",
        ["HostConfiguration:UseProject"] = "false",
        ["HostConfiguration:Image"] = DigestImage,
        ["PostgresConfiguration:Password"] = "postgres-secret",
        ["BlobConfiguration:AccessKey"] = "blob-access",
        ["BlobConfiguration:SecretKey"] = "blob-secret",
    };

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
