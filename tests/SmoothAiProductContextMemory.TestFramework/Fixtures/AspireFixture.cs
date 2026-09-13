using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Npgsql;
using SmoothAiProductContextMemory.TestFramework.Aspire;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using Xunit.v3;

namespace SmoothAiProductContextMemory.TestFramework.Fixtures;

public sealed class AspireFixture : IAsyncLifetime
{
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);
    private const string MaintenanceDatabaseName = "postgres";
    private const string PostgresResourceName = "postgres";
    private const string WireMockResourceName = "wiremock";
    private const string RedisResourceName = "redis";
    private const string BlobResourceName = "blob";
    private const string PostgresContainerName = "mimisbrunnr-testcontainer-postgres";
    private const string RedisContainerName = "mimisbrunnr-testcontainer-redis";
    private const string WireMockContainerName = "mimisbrunnr-testcontainer-wiremock";
    private const string BlobContainerName = "mimisbrunnr-testcontainer-blob";
    private const string PostgresPassword = "LocalMachineAccessNoInterestingDataTestDev#Passw0rd!FirewallNotExposed";
    public const string BlobAccessKey = "minioadmin";
    public const string BlobSecretKey = "LocalMachineAccessNoInterestingDataTestDev#Passw0rd!FirewallNotExposed";
    private const int PostgresPort = 15432;
    private const int RedisPort = 16379;
    private const int WireMockPort = 19091;
    private const int BlobPort = 9002;
    private const int MaxEndpointCheckAttempts = 3;
    private const int CommandTimeoutMilliseconds = 2000;

    private static readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private static DistributedApplication? _sharedApp;
    private static string? _sharedPostgresBaseConnectionString;
    private static string _sharedRedisConnectionString = string.Empty;
    private static string _sharedWireMockBaseUrl = string.Empty;
    private static string _sharedBlobEndpoint = string.Empty;

    private bool _ownsSharedApp;
    private string? _postgresBaseConnectionString;
    private ITestOutputHelper? _output;

    public void SetOutput(ITestOutputHelper? output) => _output = output;

    public string RedisConnectionString { get; private set; } = string.Empty;
    public string WireMockBaseUrl { get; private set; } = string.Empty;
    public string BlobEndpoint { get; private set; } = string.Empty;

    public WireMockAdminClient CreateWireMockAdminClient() => WireMockAdminClient.Create(WireMockBaseUrl);

    public string CreateDatabaseConnectionString(string databaseName)
    {
        string baseConnectionString = _postgresBaseConnectionString
            ?? throw new InvalidOperationException("Aspire fixture has not been initialized.");

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName
        };

        return builder.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        await _initSemaphore.WaitAsync();
        try
        {
            if (_sharedPostgresBaseConnectionString is not null)
            {
                _postgresBaseConnectionString = _sharedPostgresBaseConnectionString;
                RedisConnectionString = _sharedRedisConnectionString;
                WireMockBaseUrl = _sharedWireMockBaseUrl;
                BlobEndpoint = _sharedBlobEndpoint;
                Log($"Reusing shared postgres connection: {LogPostgresAddress(_postgresBaseConnectionString)}");
                return;
            }

            Log("Attempting to use fixed well-known endpoints (pre-warmed containers)...");
            if (await TryUseFixedEndpointsAsync())
            {
                Log($"Using fixed endpoints — postgres=127.0.0.1:{PostgresPort} redis=127.0.0.1:{RedisPort} wiremock=127.0.0.1:{WireMockPort} blob=127.0.0.1:{BlobPort}");
                _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
                _sharedRedisConnectionString = RedisConnectionString;
                _sharedWireMockBaseUrl = WireMockBaseUrl;
                _sharedBlobEndpoint = BlobEndpoint;
                return;
            }

            Log("Fixed endpoints not available. Querying Docker for persistent container ports...");
            if (await TryUsePersistentContainerEndpointsAsync())
            {
                Log($"Using persistent container endpoints — postgres={LogPostgresAddress(_postgresBaseConnectionString)} redis={RedisConnectionString} wiremock={WireMockBaseUrl} blob={BlobEndpoint}");
                _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
                _sharedRedisConnectionString = RedisConnectionString;
                _sharedWireMockBaseUrl = WireMockBaseUrl;
                _sharedBlobEndpoint = BlobEndpoint;
                return;
            }

            Log("No pre-existing containers reachable. Starting Aspire host to provision containers...");
            using var cts = new CancellationTokenSource(_timeout);

            try
            {
                var appHost = await DistributedApplicationTestingBuilder
                    .CreateAsync<Projects.SmoothAiProductContextMemory_TestFramework_Aspire>(["--no-dashboard"], cts.Token);

                DistributedApplication app = await appHost.BuildAsync(cts.Token);
                await app.StartAsync(cts.Token);

                await app.ResourceNotifications
                    .WaitForResourceHealthyAsync(PostgresResourceName, cts.Token);

                _postgresBaseConnectionString = await app.GetConnectionStringAsync(PostgresResourceName, cts.Token);
                RedisConnectionString = await app.GetConnectionStringAsync(RedisResourceName, cts.Token) ?? string.Empty;
                WireMockBaseUrl = app.GetEndpoint(WireMockResourceName).AbsoluteUri.TrimEnd('/');
                BlobEndpoint = app.GetEndpoint(BlobResourceName).AbsoluteUri.TrimEnd('/');

                Log($"Aspire host provisioned — postgres={LogPostgresAddress(_postgresBaseConnectionString)} redis={RedisConnectionString} wiremock={WireMockBaseUrl} blob={BlobEndpoint}");

                await WaitUntilPostgresAcceptsConnectionsAsync(cts.Token);

                await app.ResourceNotifications
                    .WaitForResourceHealthyAsync(RedisResourceName, cts.Token);

                await app.ResourceNotifications
                    .WaitForResourceHealthyAsync(WireMockResourceName, cts.Token);

                await app.ResourceNotifications
                    .WaitForResourceHealthyAsync(BlobResourceName, cts.Token);

                _sharedApp = app;
                _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
                _sharedRedisConnectionString = RedisConnectionString;
                _sharedWireMockBaseUrl = WireMockBaseUrl;
                _sharedBlobEndpoint = BlobEndpoint;
                _ownsSharedApp = true;
            }
            catch (Exception aspireEx)
            {
                // When multiple test assemblies start in parallel, several processes race to provision
                // the same persistent containers. Only one wins; the others get here because the
                // container name/port is already in use. Retry the endpoint checks — by now the
                // winning process should have the containers up.
                Log($"Aspire host failed ({aspireEx.GetType().Name}: {aspireEx.Message}). Checking whether another process started the containers...");

                for (int retry = 1; retry <= 10; retry++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

                    if (await TryUseFixedEndpointsAsync())
                    {
                        Log($"Retry {retry}/10: Fixed endpoints now reachable — containers started by another process.");
                        _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
                        _sharedRedisConnectionString = RedisConnectionString;
                        _sharedWireMockBaseUrl = WireMockBaseUrl;
                        _sharedBlobEndpoint = BlobEndpoint;
                        return;
                    }

                    if (await TryUsePersistentContainerEndpointsAsync())
                    {
                        Log($"Retry {retry}/10: Persistent container endpoints now reachable.");
                        _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
                        _sharedRedisConnectionString = RedisConnectionString;
                        _sharedWireMockBaseUrl = WireMockBaseUrl;
                        _sharedBlobEndpoint = BlobEndpoint;
                        return;
                    }

                    Log($"Retry {retry}/10: Containers not yet reachable.");
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(aspireEx).Throw();
                throw; // unreachable — satisfies compiler flow analysis
            }
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        // The Aspire DistributedApplication is shared across all fixtures in this test process.
        // Containers use ContainerLifetime.Persistent and outlive any individual fixture instance,
        // so disposal is intentionally a no-op here.
        Log("DisposeAsync called — shared Aspire containers are persistent and will not be stopped.");
        _ = _ownsSharedApp;
        return ValueTask.CompletedTask;
    }

    private async Task<bool> TryPostgresAsync()
    {
        string maintenanceConnectionString;
        try
        {
            maintenanceConnectionString = CreateDatabaseConnectionString(MaintenanceDatabaseName);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        try
        {
            // Disable SSL to avoid negotiation hangs against the plain test postgres container.
            await using var connection = new NpgsqlConnection(
                $"{maintenanceConnectionString};Timeout=5;Command Timeout=5;SSL Mode=Disable");
            await connection.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log($"Postgres probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryUseFixedEndpointsAsync()
    {
        for (int attempt = 1; attempt <= MaxEndpointCheckAttempts; attempt++)
        {
            if (attempt > 1)
            {
                Log($"Retrying fixed endpoint check (attempt {attempt}/{MaxEndpointCheckAttempts})...");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            _postgresBaseConnectionString = BuildConnectionString(PostgresPort, MaintenanceDatabaseName);
            RedisConnectionString = $"127.0.0.1:{RedisPort}";
            WireMockBaseUrl = $"http://127.0.0.1:{WireMockPort}";
            BlobEndpoint = $"http://127.0.0.1:{BlobPort}";

            bool[] results = await Task.WhenAll(
                TryPostgresAsync(),
                TryRedisAsync(),
                TryWireMockAsync(),
                TryBlobAsync());

            bool postgresOk = results[0];
            bool redisOk = results[1];
            bool wireMockOk = results[2];
            bool blobOk = results[3];

            Log($"Fixed endpoint check (attempt {attempt}/{MaxEndpointCheckAttempts}): postgres={postgresOk} redis={redisOk} wiremock={wireMockOk} blob={blobOk}");

            if (postgresOk && redisOk && wireMockOk && blobOk)
            {
                return true;
            }
        }

        _postgresBaseConnectionString = null;
        RedisConnectionString = string.Empty;
        WireMockBaseUrl = string.Empty;
        BlobEndpoint = string.Empty;
        return false;
    }

    private async Task<bool> TryUsePersistentContainerEndpointsAsync()
    {
        int? mappedPostgresPort = TryGetPublishedPort(PostgresContainerName, "5432/tcp");
        int? mappedRedisPort = TryGetPublishedPort(RedisContainerName, "6379/tcp");
        int? mappedWireMockPort = TryGetPublishedPort(WireMockContainerName, "8080/tcp");
        int? mappedBlobPort = TryGetPublishedPort(BlobContainerName, "9000/tcp");

        Log($"Persistent container port discovery — postgres={mappedPostgresPort?.ToString() ?? "not found"} redis={mappedRedisPort?.ToString() ?? "not found"} wiremock={mappedWireMockPort?.ToString() ?? "not found"} blob={mappedBlobPort?.ToString() ?? "not found"}");

        if (mappedPostgresPort is null || mappedRedisPort is null || mappedWireMockPort is null || mappedBlobPort is null)
        {
            return false;
        }

        _postgresBaseConnectionString = BuildConnectionString(mappedPostgresPort.Value, MaintenanceDatabaseName);
        RedisConnectionString = $"127.0.0.1:{mappedRedisPort.Value}";
        WireMockBaseUrl = $"http://127.0.0.1:{mappedWireMockPort.Value}";
        BlobEndpoint = $"http://127.0.0.1:{mappedBlobPort.Value}";

        bool[] results = await Task.WhenAll(
            TryPostgresAsync(),
            TryRedisAsync(),
            TryWireMockAsync(),
            TryBlobAsync());

        bool postgresOk = results[0];
        bool redisOk = results[1];
        bool wireMockOk = results[2];
        bool blobOk = results[3];

        Log($"Persistent container endpoint check: postgres={postgresOk} redis={redisOk} wiremock={wireMockOk} blob={blobOk}");

        if (postgresOk && redisOk && wireMockOk && blobOk)
        {
            return true;
        }

        _postgresBaseConnectionString = null;
        RedisConnectionString = string.Empty;
        WireMockBaseUrl = string.Empty;
        BlobEndpoint = string.Empty;
        return false;
    }

    private async Task WaitUntilPostgresAcceptsConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await TryPostgresAsync())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<bool> TryRedisAsync()
    {
        if (string.IsNullOrWhiteSpace(RedisConnectionString))
        {
            return false;
        }

        try
        {
            string[] parts = RedisConnectionString.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[^1], out int port))
            {
                return false;
            }

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(2));
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryWireMockAsync()
    {
        if (string.IsNullOrWhiteSpace(WireMockBaseUrl))
        {
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = await http.GetAsync($"{WireMockBaseUrl}/__admin/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryBlobAsync()
    {
        if (string.IsNullOrWhiteSpace(BlobEndpoint))
        {
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = await http.GetAsync($"{BlobEndpoint}/minio/health/live");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildConnectionString(int port, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = databaseName,
            Username = "postgres",
            Password = PostgresPassword,
            IncludeErrorDetail = true,
            // Disable SSL to avoid negotiation hangs against the plain test postgres container.
            SslMode = SslMode.Disable
        };

        return builder.ConnectionString;
    }

    private static int? TryGetPublishedPort(string containerName, string containerPort)
    {
        // Docker is the default runtime; Podman is probed as the fallback.
        foreach (string containerRuntime in new[] { "docker", "podman" })
        {
            string? output = TryRunCommand(containerRuntime, "port", containerName, containerPort);
            if (string.IsNullOrWhiteSpace(output))
            {
                continue;
            }

            string line = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;

            int separatorIndex = line.LastIndexOf(':');
            if (separatorIndex < 0)
            {
                continue;
            }

            string portValue = line[(separatorIndex + 1)..];
            if (int.TryParse(portValue, out int parsedPort))
            {
                return parsedPort;
            }
        }

        return null;
    }

    private static string? TryRunCommand(string fileName, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return null;
            }

            // Read asynchronously: a stalled runtime CLI (for example Podman with a stopped machine)
            // keeps stdout open indefinitely, and a synchronous ReadToEnd would block forever —
            // the WaitForExit timeout below would never be reached.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(CommandTimeoutMilliseconds))
            {
                TryKill(process);
                return null;
            }

            if (!Task.WhenAll(outputTask, errorTask).Wait(CommandTimeoutMilliseconds))
            {
                return null;
            }

            return process.ExitCode == 0 ? outputTask.Result.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already exited or cannot be killed — nothing further to do.
        }
    }

    private void Log(string message)
    {
        try
        {
            _output?.WriteLine($"[AspireFixture {DateTime.UtcNow:HH:mm:ss.fff}] {message}");
        }
        catch (InvalidOperationException)
        {
            // Output helper is no longer active (test has ended).
        }
    }

    private static string LogPostgresAddress(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "(none)";
        }

        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{b.Host}:{b.Port}/{b.Database}";
        }
        catch
        {
            return "(unparseable)";
        }
    }
}

[CollectionDefinition("Aspire")]
public sealed class AspireCollection : ICollectionFixture<AspireFixture>;
