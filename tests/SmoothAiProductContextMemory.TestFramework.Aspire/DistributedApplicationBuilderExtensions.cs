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
    private const string BlobImage = "docker.io/minio/minio";
    private const string WireMockImage = "docker.io/wiremock/wiremock";
    private const string BlobSecretKey = "LocalMachineAccessNoInterestingDataTestDev#Passw0rd!FirewallNotExposed";

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
