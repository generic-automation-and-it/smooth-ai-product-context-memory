using System.IO;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace SmoothAiProductContextMemory.AppHost;

internal static class DistributedApplicationBuilderExtensions
{
    private const int DefaultPostgresPort = 5432;
    private const int DefaultBlobPort = 9000;
    private const int DefaultBlobConsolePort = 9001;
    private const string BlobImage = "docker.io/minio/minio";
    private const int DefaultSeqPort = 5341;
    private const string DockerDesktopGroupName = "smooth-project-memory";
    private const string PostgresContainerName = "smooth-project-memory-dev-postgres";
    private const string BlobContainerName = "smooth-project-memory-dev-blob";
    private const string SeqContainerName = "smooth-project-memory-dev-seq";
    private const string PostgresDataVolume = "smooth-project-memory-postgres-data";
    private const string BlobDataVolume = "smooth-project-memory-blob-data";

    extension(IDistributedApplicationBuilder builder)
    {
        internal IDistributedApplicationBuilder AddSmoothAiProductContextMemoryAppHostResources()
        {
            AppHostConfiguration configuration = builder.GetAppHostConfiguration();
            var postgres = builder.AddPostgresResource(configuration);
            var blob = builder.AddBlobResource(configuration);
            var seq = builder.AddSeqResource(configuration);

            builder.AddHostProject(postgres, blob, seq, configuration);

            return builder;
        }

        internal IDistributedApplicationBuilder WriteDashboardStartupHint()
        {
            string? dashboardUrls = builder.Configuration["ASPNETCORE_URLS"];
            string? dashboardUrl = dashboardUrls?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(dashboardUrl))
            {
                Console.WriteLine($"Aspire dashboard UI: {dashboardUrl}");
                Console.WriteLine("If the dashboard asks for login, use the /login?t=... URL that Aspire prints after startup.");
            }

            return builder;
        }

        private AppHostConfiguration GetAppHostConfiguration()
        {
            string PostgresPassword()
            {
                string value = builder.Configuration["PostgresConfiguration:Password"] ?? string.Empty;
                return string.IsNullOrWhiteSpace(value)
                    ? "LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed"
                    : value;
            }

            return new AppHostConfiguration(
                PostgresPassword(),
                builder.Configuration.GetValue("PostgresConfiguration:Port", DefaultPostgresPort),
                builder.Configuration["BlobConfiguration:AccessKey"] ?? "smooth-local",
                builder.Configuration["BlobConfiguration:SecretKey"] ?? "LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed",
                builder.Configuration.GetValue("BlobConfiguration:Port", DefaultBlobPort),
                builder.Configuration.GetValue("BlobConfiguration:ConsolePort", DefaultBlobConsolePort),
                builder.Configuration.GetValue("SeqConfiguration:Port", DefaultSeqPort),
                Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..")));
        }

        private IResourceBuilder<PostgresDatabaseResource> AddPostgresResource(AppHostConfiguration configuration)
        {
            var postgresPassword = builder.AddParameter(
                "postgres-password",
                configuration.PostgresPassword,
                secret: true);

            var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: configuration.PostgresPort)
                .WithContainerName(PostgresContainerName)
                .WithDataVolume(PostgresDataVolume)
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={PostgresContainerName}")
                .WithLifetime(ContainerLifetime.Persistent);

            return postgres.AddDatabase("app");
        }

        private IResourceBuilder<ContainerResource> AddBlobResource(AppHostConfiguration configuration)
        {
            // MinIO (S3-compatible object store), registered as a plain container because no
            // first-party Aspire integration exists. Fixed env-var credentials make bucket creation
            // and .NET client auth fully deterministic. SeaweedFS was rejected: its S3 gateway needs
            // a JSON credentials file and an admin JWT to create buckets, which is impractical to
            // automate inside an Aspire container (see ADR).
            // The image is registry-qualified so Podman never has to resolve a short name.
            return builder.AddContainer("blob", BlobImage)
                .WithArgs("server", "/data", "--console-address", ":9001")
                .WithHttpEndpoint(port: configuration.BlobPort, targetPort: 9000, name: "s3")
                .WithHttpEndpoint(port: configuration.BlobConsolePort, targetPort: 9001, name: "console")
                .WithEnvironment("MINIO_ROOT_USER", configuration.BlobAccessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", configuration.BlobSecretKey)
                .WithVolume(BlobDataVolume, "/data")
                .WithContainerName(BlobContainerName)
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={BlobContainerName}")
                .WithLifetime(ContainerLifetime.Persistent);
        }

        private IResourceBuilder<IResourceWithConnectionString> AddSeqResource(AppHostConfiguration configuration)
        {
            return builder.AddSeq("seq", port: configuration.SeqPort)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithContainerName(SeqContainerName)
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={SeqContainerName}")
                .WithLifetime(ContainerLifetime.Persistent);
        }

        private void AddHostProject(
            IResourceBuilder<PostgresDatabaseResource> postgres,
            IResourceBuilder<ContainerResource> blob,
            IResourceBuilder<IResourceWithConnectionString> seq,
            AppHostConfiguration configuration)
        {
            builder.AddProject<Projects.SmoothAiProductContextMemory_Host>("host")
                .WithReference(postgres)
                .WithReference(seq)
                .WithEnvironment("BlobStorage__Endpoint", blob.GetEndpoint("s3"))
                .WithEnvironment("BlobStorage__AccessKey", configuration.BlobAccessKey)
                .WithEnvironment("BlobStorage__SecretKey", configuration.BlobSecretKey)
                .WithEnvironment("BlobStorage__Bucket", "smooth-project-memory")
                .WaitFor(postgres)
                .WaitFor(blob)
                .WaitFor(seq);
        }
    }

    private sealed record AppHostConfiguration(
        string PostgresPassword,
        int PostgresPort,
        string BlobAccessKey,
        string BlobSecretKey,
        int BlobPort,
        int BlobConsolePort,
        int SeqPort,
        string RepoRoot);
}
