using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace SmoothAiProductContextMemory.TestFramework.Aspire;

internal static class DistributedApplicationBuilderExtensions
{
    private const string DockerDesktopGroupName = "project";
    private const string BlobContainerName = "project-test-blob";
    private const string BlobAccessKey = "minioadmin";
    private const int BlobPort = 9002;
    private const int BlobConsolePort = 19092;
    // Registry-qualified so Podman never has to resolve a short image name.
    // docker.io/minio/minio is no longer publicly pullable (upstream removed Hub images);
    // quay.io hosts the last community server releases.
    private const string BlobImage = "quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z";
    private const string WireMockImage = "docker.io/wiremock/wiremock";
    private const string BlobSecretKey = "LocalMachineAccessNoInterestingDataTestDev#Passw0rd!FirewallNotExposed";
    // Aspire 13.3.0 defaults to library/postgres:17.6. AGE's PG17 image keeps the same major.
    // Pairing recorded in HLD 003 / NFR-04.
    private const string PostgresImageRegistry = "docker.io";
    private const string PostgresImage = "apache/age";
    private const string PostgresImageTag = "release_PG17_1.7.0";

    internal static void AddSmoothAiProductContextMemoryTestDependencies(this IDistributedApplicationBuilder builder)
    {
        var postgresPassword = builder.AddParameter(
            "postgres-password",
            "LocalMachineAccessNoInterestingDataTestDev#Passw0rd!FirewallNotExposed",
            secret: true);

        builder.AddPostgresDependency(postgresPassword);
        builder.AddRedisDependency();
        builder.AddWireMockDependency();
        builder.AddBlobDependency();
    }

    private static void AddBlobDependency(this IDistributedApplicationBuilder builder)
    {
        builder.AddContainer("blob", BlobImage)
            .WithArgs("server", "/data", "--console-address", ":9001")
            .WithHttpEndpoint(port: BlobPort, targetPort: 9000, name: "s3")
            .WithHttpEndpoint(port: BlobConsolePort, targetPort: 9001, name: "console")
            .WithEnvironment("MINIO_ROOT_USER", BlobAccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", BlobSecretKey)
            .WithContainerName(BlobContainerName)
            .WithContainerRuntimeArgs(
                "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                "--label", $"com.docker.compose.service={BlobContainerName}")
            .WithLifetime(ContainerLifetime.Persistent);
    }

    private static void AddPostgresDependency(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> postgresPassword)
    {
        var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: 15432)
            .WithImage(PostgresImage, PostgresImageTag)
            .WithImageRegistry(PostgresImageRegistry)
            .WithContainerName("project-test-postgres")
            .WithContainerRuntimeArgs(
                "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                "--label", "com.docker.compose.service=project-test-postgres")
            .WithLifetime(ContainerLifetime.Persistent);

        postgres.AddDatabase("app-component");
        postgres.AddDatabase("app-integration");
        postgres.AddDatabase("infra-component");
        postgres.AddDatabase("infra-integration");
        postgres.AddDatabase("host-integration");
    }

    private static void AddRedisDependency(this IDistributedApplicationBuilder builder)
    {
        builder.AddRedis("redis", port: 16379)
            .WithContainerName("project-test-redis")
            .WithContainerRuntimeArgs(
                "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                "--label", "com.docker.compose.service=project-test-redis")
            .WithLifetime(ContainerLifetime.Persistent);
    }

    private static void AddWireMockDependency(this IDistributedApplicationBuilder builder)
    {
        builder.AddContainer("wiremock", WireMockImage)
            .WithHttpEndpoint(port: 19091, targetPort: 8080)
            .WithContainerName("project-test-wiremock")
            .WithContainerRuntimeArgs(
                "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                "--label", "com.docker.compose.service=project-test-wiremock")
            .WithLifetime(ContainerLifetime.Persistent);
    }
}
