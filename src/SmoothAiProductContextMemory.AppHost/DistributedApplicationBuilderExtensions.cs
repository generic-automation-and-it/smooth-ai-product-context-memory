using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace SmoothAiProductContextMemory.AppHost;

internal static class DistributedApplicationBuilderExtensions
{
    // Upstream MinIO community images are gone from Docker Hub and quay.io; Chainguard's free tier
    // only publishes :latest, so it is pinned by digest. Keep identical to the test Aspire host.
    private const string BlobImage = "cgr.dev/chainguard/minio@sha256:bd014394a80898e68c149f2311fdf8d5a2c2f3bb2c33b9327ae6d02b4b065ae1";
    private const string SeqImageRegistry = "docker.io";
    private const string SeqImage = "datalust/seq";
    private const string SeqImageTag = "2025.2";
    private const string DatabaseResourceName = "mimers-head";
    private const string HostConnectionStringName = "SmoothAiProductContextMemory";
    private const string SeqConnectionStringName = "seq";
    private const string HostReadinessPath = "/health";
    private const string BlobBucketName = "smooth-mimisbrunnr-memory-well";
    private const string PostgresImageRegistry = "docker.io";
    private const string PostgresImage = "apache/age";
    private const string PostgresImageTag = "release_PG17_1.7.0";

    extension(IDistributedApplicationBuilder builder)
    {
        internal IDistributedApplicationBuilder AddSmoothAiProductContextMemoryAppHostResources()
        {
            AppHostConfiguration configuration = AppHostConfiguration.Create(builder.Configuration);
            Console.WriteLine(HostLaunchMode.FormatAnnouncement(configuration.UseProject, configuration.HostImage));
            Console.WriteLine($"AppHost mode: {configuration.Mode}. Installation: {configuration.InstallationId}. Engine: {configuration.EngineKind}. Controller version: {configuration.ReleaseVersion}.");
            var database = builder.AddPostgresResource(configuration);
            var blob = builder.AddBlobResource(configuration);
            var seq = builder.AddSeqResource(configuration);
            var apiReadToken = builder.AddParameter("api-read-token", secret: true);
            var apiWriteToken = builder.AddParameter("api-write-token", secret: true);

            builder.AddHostProject(database, blob, seq, apiReadToken, apiWriteToken, configuration);

            return builder;
        }

        internal IDistributedApplicationBuilder WriteDashboardStartupHint()
        {
            AppHostConfiguration configuration = AppHostConfiguration.Create(builder.Configuration);
            string? dashboardUrls = builder.Configuration["ASPNETCORE_URLS"];
            string? dashboardUrl = dashboardUrls?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(dashboardUrl))
            {
                Console.WriteLine($"Aspire dashboard UI: {dashboardUrl}");
                Console.WriteLine("If the dashboard asks for login, use the /login?t=... URL that Aspire prints after startup.");
            }

            if (configuration.IsRelease)
            {
                Console.WriteLine($"Release resources are scoped to installation '{configuration.InstallationId}'. Stop the controller gracefully to remove workloads while preserving named data volumes.");
                Console.WriteLine("After forced termination, use the controller image's stop or reset command. Reset is destructive and ownership-checked.");
            }
            else
            {
                Console.WriteLine("Dashboard dies with this process. tyr-postgres (mimisbrunnr-postgres), idunn-blob (mimisbrunnr-blob-well), and saga-seq (mimisbrunnr-seq) keep running (ContainerLifetime.Persistent). mimisbrunnr-host may remain after a hard kill.");
                Console.WriteLine("Stop (keep data): scripts/stop-dev-stack.sh");
                Console.WriteLine("Reset (destroy volumes): scripts/reset-dev-stack.sh");
                Console.WriteLine("Do not glob mimisbrunnr-* — that also matches mimisbrunnr-testcontainer-*.");
            }

            return builder;
        }

        private IResourceBuilder<PostgresDatabaseResource> AddPostgresResource(AppHostConfiguration configuration)
        {
            var postgresPassword = builder.AddParameter(
                "postgres-password",
                configuration.PostgresPassword,
                secret: true);

            var postgres = builder.AddPostgres("tyr-postgres", password: postgresPassword, port: configuration.PostgresPort)
                .WithImage(PostgresImage, PostgresImageTag)
                .WithImageRegistry(PostgresImageRegistry)
                .WithContainerName(configuration.PostgresContainerName)
                .WithDataVolume(configuration.PostgresDataVolume)
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={configuration.DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={configuration.PostgresContainerName}",
                    "--label", $"{AppHostConfiguration.OwnershipLabel}={configuration.InstallationId}",
                    "--label", $"{AppHostConfiguration.ManagedLabel}=true");

            postgres = builder.ConfigureReleaseEndpoint(postgres, "tcp", configuration);

            if (!configuration.IsRelease)
            {
                postgres = postgres.WithLifetime(ContainerLifetime.Persistent);
            }

            // The dashboard name follows Mímir's severed head. Keep the physical database as "app":
            // changing it on an existing persistent cluster requires an explicit data migration.
            return postgres.AddDatabase(DatabaseResourceName, databaseName: "app");
        }

        private IResourceBuilder<ContainerResource> AddBlobResource(AppHostConfiguration configuration)
        {
            // MinIO (S3-compatible object store), registered as a plain container because no
            // first-party Aspire integration exists. Fixed env-var credentials make bucket creation
            // and .NET client auth fully deterministic. SeaweedFS was rejected: its S3 gateway needs
            // a JSON credentials file and an admin JWT to create buckets, which is impractical to
            // automate inside an Aspire container (see HLD 001 LADR-06).
            // The image is registry-qualified so Podman never has to resolve a short name.
            var blob = builder.AddContainer("idunn-blob", BlobImage)
                .WithArgs("server", "/data", "--console-address", ":9001")
                .WithHttpEndpoint(port: configuration.BlobPort, targetPort: 9000, name: "s3")
                .WithHttpEndpoint(port: configuration.BlobConsolePort, targetPort: 9001, name: "console")
                .WithEnvironment("MINIO_ROOT_USER", configuration.BlobAccessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", configuration.BlobSecretKey)
                .WithVolume(configuration.BlobDataVolume, "/data")
                .WithContainerName(configuration.BlobContainerName)
                // Chainguard's image runs as a non-root user, which cannot write a data volume created
                // by the earlier root-run MinIO image; run as root so existing dev volumes keep working.
                .WithContainerRuntimeArgs(
                    "--user", "0:0",
                    "--label", $"com.docker.compose.project={configuration.DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={configuration.BlobContainerName}",
                    "--label", $"{AppHostConfiguration.OwnershipLabel}={configuration.InstallationId}",
                    "--label", $"{AppHostConfiguration.ManagedLabel}=true")
                .WithLifetime(configuration.IsRelease ? ContainerLifetime.Session : ContainerLifetime.Persistent);

            blob = builder.ConfigureReleaseEndpoint(blob, "s3", configuration);
            blob = builder.ConfigureReleaseEndpoint(blob, "console", configuration);
            return blob;
        }

        private IResourceBuilder<IResourceWithConnectionString> AddSeqResource(AppHostConfiguration configuration)
        {
            // Seq is retained for persistence: the Aspire dashboard's telemetry store is in-memory,
            // capacity-bounded and cleared when the AppHost stops, so an intermittent failure looked at
            // tomorrow is already gone. That argument only holds with a data volume — without one Seq's
            // persistence claim was false beyond container removal, which is why one is attached here
            // alongside the Postgres and blob volumes.
            var seq = builder.AddSeq("saga-seq", port: configuration.SeqPort)
                .WithImage(SeqImage, SeqImageTag)
                .WithImageRegistry(SeqImageRegistry)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithDataVolume(configuration.SeqDataVolume)
                .WithContainerName(configuration.SeqContainerName)
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={configuration.DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={configuration.SeqContainerName}",
                    "--label", $"{AppHostConfiguration.OwnershipLabel}={configuration.InstallationId}",
                    "--label", $"{AppHostConfiguration.ManagedLabel}=true")
                .WithLifetime(configuration.IsRelease ? ContainerLifetime.Session : ContainerLifetime.Persistent);

            return builder.ConfigureReleaseEndpoint(seq, "http", configuration);
        }

        private void AddHostProject(
            IResourceBuilder<PostgresDatabaseResource> database,
            IResourceBuilder<ContainerResource> blob,
            IResourceBuilder<IResourceWithConnectionString> seq,
            IResourceBuilder<ParameterResource> apiReadToken,
            IResourceBuilder<ParameterResource> apiWriteToken,
            AppHostConfiguration configuration)
        {
            if (!configuration.UseProject)
            {
                builder.AddHostContainer(database, blob, seq, apiReadToken, apiWriteToken, configuration);
                return;
            }

            builder.AddProject<Projects.SmoothAiProductContextMemory_Host>(HostLaunchMode.WorkingTreeResourceName)
                .WithReference(database, connectionName: HostConnectionStringName)
                .WithReference(seq, connectionName: SeqConnectionStringName)
                .WithEnvironment("BlobStorage__Endpoint", blob.GetEndpoint("s3"))
                .WithEnvironment("BlobStorage__AccessKey", configuration.BlobAccessKey)
                .WithEnvironment("BlobStorage__SecretKey", configuration.BlobSecretKey)
                .WithEnvironment("BlobStorage__Bucket", BlobBucketName)
                .WithEnvironment("ApiAccess__ReadToken", apiReadToken)
                .WithEnvironment("ApiAccess__WriteToken", apiWriteToken)
                .WithHttpHealthCheck(HostReadinessPath)
                .WaitFor(database)
                .WaitFor(blob)
                .WaitFor(seq);
        }

        private void AddHostContainer(
            IResourceBuilder<PostgresDatabaseResource> database,
            IResourceBuilder<ContainerResource> blob,
            IResourceBuilder<IResourceWithConnectionString> seq,
            IResourceBuilder<ParameterResource> apiReadToken,
            IResourceBuilder<ParameterResource> apiWriteToken,
            AppHostConfiguration configuration)
        {
            ContainerImageReference imageReference = ContainerImageReference.Parse(configuration.HostImage);
            IResourceBuilder<ContainerResource> host = imageReference.Tag is null
                ? builder.AddContainer(HostLaunchMode.PublishedImageResourceName, imageReference.Image)
                : builder.AddContainer(HostLaunchMode.PublishedImageResourceName, imageReference.Image, imageReference.Tag);

            if (configuration.HostImage.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase))
            {
                host = host.WithImagePullPolicy(ImagePullPolicy.Always);
            }

            host
                .WithHttpEndpoint(port: configuration.HostPort, targetPort: 5141, name: "http")
                .WithContainerName(configuration.HostContainerName)
                .WithVolume(configuration.HostContextVolume, "/app/.context")
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={configuration.DockerDesktopGroupName}",
                    "--label", $"com.docker.compose.service={configuration.HostContainerName}",
                    "--label", $"{AppHostConfiguration.OwnershipLabel}={configuration.InstallationId}",
                    "--label", $"{AppHostConfiguration.ManagedLabel}=true")
                .WithReference(database, connectionName: HostConnectionStringName)
                .WithReference(seq, connectionName: SeqConnectionStringName)
                .WithEnvironment("BlobStorage__Endpoint", blob.GetEndpoint("s3"))
                .WithEnvironment("BlobStorage__AccessKey", configuration.BlobAccessKey)
                .WithEnvironment("BlobStorage__SecretKey", configuration.BlobSecretKey)
                .WithEnvironment("BlobStorage__Bucket", BlobBucketName)
                .WithEnvironment("ApiAccess__ReadToken", apiReadToken)
                .WithEnvironment("ApiAccess__WriteToken", apiWriteToken)
                .WithHttpHealthCheck(HostReadinessPath)
                .WithOtlpExporter()
                .WaitFor(database)
                .WaitFor(blob)
                .WaitFor(seq);

            builder.ConfigureReleaseEndpoint(host, "http", configuration);
        }

        private IResourceBuilder<T> ConfigureReleaseEndpoint<T>(
            IResourceBuilder<T> resource,
            string endpointName,
            AppHostConfiguration configuration)
            where T : IResourceWithEndpoints
        {
            if (!configuration.IsRelease)
            {
                return resource;
            }

            resource.WithEndpoint(endpointName, endpoint =>
            {
                endpoint.IsProxied = false;
                endpoint.TargetHost = configuration.EngineBindAddress;
            }, createIfNotExists: false);
            return resource.OnResourceEndpointsAllocated((model, _, _) =>
            {
                EndpointAnnotation endpoint = model.Annotations
                    .OfType<EndpointAnnotation>()
                    .Single(endpoint => endpoint.Name == endpointName);
                AllocatedEndpoint allocated = endpoint.AllocatedEndpoint
                    ?? throw new InvalidOperationException($"Endpoint '{endpointName}' was not allocated for '{model.Name}'.");

                endpoint.AllocatedEndpoint = new AllocatedEndpoint(
                    endpoint,
                    configuration.EngineHostAddress,
                    allocated.Port,
                    allocated.BindingMode,
                    allocated.TargetPortExpression,
                    allocated.NetworkID);

                return Task.CompletedTask;
            });
        }
    }

}
