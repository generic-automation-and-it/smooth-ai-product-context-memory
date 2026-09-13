using System.IO;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace SmoothAiProductContextMemory.AppHost;

internal static class DistributedApplicationBuilderExtensions
{
    private const int DefaultPostgresPort = 5432;
    private const int DefaultBlobPort = 9000;
    private const int DefaultBlobConsolePort = 9001;
    // docker.io/minio/minio is no longer publicly pullable; quay.io hosts the last community releases.
    private const string BlobImage = "quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z";
    private const int DefaultSeqPort = 5341;
    private const string HostConnectionStringName = "SmoothAiProductContextMemory";
    private const string HostReadinessPath = "/health";
    private const int DefaultHostPort = 5141;
    private const string HostContainerName = "mimisbrunnr-host";
    private const string DefaultHostImage = "ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory:latest";
    // The Docker Desktop group is a compose *label*, so it carries the brand spelling with its
    // accent. Container and volume names cannot: Docker rejects them outright —
    // "Invalid container name (mímisbrunnr-…), only [a-zA-Z0-9][a-zA-Z0-9_.-] are allowed" — so the
    // artifacts are transliterated to ASCII. Same split the test fixture uses
    // (`smooth-mímisbrunnr-testing` group, `mimisbrunnr-testcontainer-*` containers).
    private const string DockerDesktopGroupName = "smooth-mímisbrunnr";
    private const string PostgresContainerName = "mimisbrunnr-postgres";
    private const string BlobContainerName = "mimisbrunnr-blob";
    private const string SeqContainerName = "mimisbrunnr-seq";
    private const string PostgresDataVolume = "mimisbrunnr-postgres-data";
    private const string BlobDataVolume = "mimisbrunnr-blob-data";
    private const string SeqDataVolume = "mimisbrunnr-seq-data";
    // S3 bucket names are DNS labels: lowercase ASCII, digits and hyphens only, so the brand is
    // transliterated here for the same reason container names are.
    private const string BlobBucketName = "smooth-mimisbrunnr-memory-well";
    // Aspire 13.3.0 defaults to library/postgres:17.6. AGE's PG17 image keeps the same major so the
    // persistent data volume stays compatible. Pairing recorded in HLD 003 / NFR-04.
    // Keep this pin identical to tests/SmoothAiProductContextMemory.TestFramework.Aspire.
    private const string PostgresImageRegistry = "docker.io";
    private const string PostgresImage = "apache/age";
    private const string PostgresImageTag = "release_PG17_1.7.0";

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

            string? hostImage = builder.Configuration["HostConfiguration:Image"];
            if (string.IsNullOrWhiteSpace(hostImage))
            {
                hostImage = DefaultHostImage;
            }

            return new AppHostConfiguration(
                PostgresPassword(),
                builder.Configuration.GetValue("PostgresConfiguration:Port", DefaultPostgresPort),
                builder.Configuration["BlobConfiguration:AccessKey"] ?? "smooth-local",
                builder.Configuration["BlobConfiguration:SecretKey"] ?? "LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed",
                builder.Configuration.GetValue("BlobConfiguration:Port", DefaultBlobPort),
                builder.Configuration.GetValue("BlobConfiguration:ConsolePort", DefaultBlobConsolePort),
                builder.Configuration.GetValue("SeqConfiguration:Port", DefaultSeqPort),
                builder.Configuration.GetValue("HostConfiguration:UseProject", false),
                hostImage,
                Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..")));
        }

        private IResourceBuilder<PostgresDatabaseResource> AddPostgresResource(AppHostConfiguration configuration)
        {
            var postgresPassword = builder.AddParameter(
                "postgres-password",
                configuration.PostgresPassword,
                secret: true);

            var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: configuration.PostgresPort)
                .WithImage(PostgresImage, PostgresImageTag)
                .WithImageRegistry(PostgresImageRegistry)
                .WithContainerName(PostgresContainerName)
                .WithDataVolume(PostgresDataVolume)
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={PostgresContainerName}")
                .WithLifetime(ContainerLifetime.Persistent);

            // The resource name is the connection-string key Aspire injects, and the Host reads
            // `ConnectionStrings:SmoothAiProductContextMemory`. Naming the resource "app" published
            // `ConnectionStrings__app`, which nothing consumed — the Host died on start-up. The
            // physical database stays "app" so the persistent data volume keeps its dev data.
            return postgres.AddDatabase(HostConnectionStringName, databaseName: "app");
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
            // Seq is retained for persistence: the Aspire dashboard's telemetry store is in-memory,
            // capacity-bounded and cleared when the AppHost stops, so an intermittent failure looked at
            // tomorrow is already gone. That argument only holds with a data volume — without one Seq's
            // persistence claim was false beyond container removal, which is why one is attached here
            // alongside the Postgres and blob volumes.
            return builder.AddSeq("seq", port: configuration.SeqPort)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithDataVolume(SeqDataVolume)
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
            if (!configuration.UseProject)
            {
                builder.AddHostContainer(postgres, blob, seq, configuration);
                return;
            }

            builder.AddProject<Projects.SmoothAiProductContextMemory_Host>("host")
                .WithReference(postgres, connectionName: "SmoothAiProductContextMemory")
                .WithReference(seq)
                .WithEnvironment("BlobStorage__Endpoint", blob.GetEndpoint("s3"))
                .WithEnvironment("BlobStorage__AccessKey", configuration.BlobAccessKey)
                .WithEnvironment("BlobStorage__SecretKey", configuration.BlobSecretKey)
                .WithEnvironment("BlobStorage__Bucket", BlobBucketName)
                .WithHttpHealthCheck(HostReadinessPath)
                .WaitFor(postgres)
                .WaitFor(blob)
                .WaitFor(seq);
        }

        private void AddHostContainer(
            IResourceBuilder<PostgresDatabaseResource> postgres,
            IResourceBuilder<ContainerResource> blob,
            IResourceBuilder<IResourceWithConnectionString> seq,
            AppHostConfiguration configuration)
        {
            (string image, string? tag) = SplitImageReference(configuration.HostImage);
            IResourceBuilder<ContainerResource> host = tag is null
                ? builder.AddContainer("host", image)
                : builder.AddContainer("host", image, tag);

            if (configuration.HostImage.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase))
            {
                host = host.WithImagePullPolicy(ImagePullPolicy.Always);
            }

            host
                .WithHttpEndpoint(port: DefaultHostPort, targetPort: DefaultHostPort, name: "http")
                .WithContainerName(HostContainerName)
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={HostContainerName}")
                .WithReference(postgres, connectionName: HostConnectionStringName)
                .WithReference(seq)
                .WithEnvironment("BlobStorage__Endpoint", blob.GetEndpoint("s3"))
                .WithEnvironment("BlobStorage__AccessKey", configuration.BlobAccessKey)
                .WithEnvironment("BlobStorage__SecretKey", configuration.BlobSecretKey)
                .WithEnvironment("BlobStorage__Bucket", BlobBucketName)
                .WithHttpHealthCheck(HostReadinessPath)
                .WithOtlpExporter()
                .WaitFor(postgres)
                .WaitFor(blob)
                .WaitFor(seq);
        }
    }

    private static (string Image, string? Tag) SplitImageReference(string reference)
    {
        int slash = reference.LastIndexOf('/');
        int colon = reference.LastIndexOf(':');
        if (colon > slash)
        {
            return (reference[..colon], reference[(colon + 1)..]);
        }

        return (reference, null);
    }

    private sealed record AppHostConfiguration(
        string PostgresPassword,
        int PostgresPort,
        string BlobAccessKey,
        string BlobSecretKey,
        int BlobPort,
        int BlobConsolePort,
        int SeqPort,
        bool UseProject,
        string HostImage,
        string RepoRoot);
}
