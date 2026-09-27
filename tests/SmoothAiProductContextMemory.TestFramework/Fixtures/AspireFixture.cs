using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Npgsql;
using SmoothAiProductContextMemory.TestFramework.Aspire;
using System.Diagnostics;
using System.Net.Http;
using Xunit.v3;

namespace SmoothAiProductContextMemory.TestFramework.Fixtures;

public sealed class AspireFixture : IAsyncLifetime
{
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);
    private const string MaintenanceDatabaseName = "postgres";
    private const string PostgresResourceName = "postgres";
    private const string BlobResourceName = "blob";
    private const string PostgresContainerName = "mimisbrunnr-testcontainer-postgres";
    private const string BlobContainerName = "mimisbrunnr-testcontainer-blob";
    private const string PostgresPassword = "LocalMachineAccessNoInterestingDataTestDev#Passw0rd!FirewallNotExposed";
    public const string BlobAccessKey = "minioadmin";
    public const string BlobSecretKey = "LocalMachineAccessNoInterestingDataTestDev#Passw0rd!FirewallNotExposed";
    private const int PostgresPort = 15432;
    private const int BlobPort = 9002;
    private const int MaxEndpointCheckAttempts = 3;
    private const int CommandTimeoutMilliseconds = 2000;

    private static readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private static DistributedApplication? _sharedApp;
    private static string? _sharedPostgresBaseConnectionString;
    private static string _sharedBlobEndpoint = string.Empty;

    private bool _ownsSharedApp;
    private string? _postgresBaseConnectionString;
    private ITestOutputHelper? _output;

    /// <summary>
    /// xUnit v3 does not inject <see cref="ITestOutputHelper"/> into a collection fixture, so the
    /// fixture cannot receive it by construction. A consumer may still override via
    /// <see cref="SetOutput"/>; otherwise <see cref="Output"/> and <see cref="Log"/> fall back to the
    /// live <c>TestContext.Current.TestOutputHelper</c>, which is populated for the running test — so
    /// the cleanup report callbacks and fixture log lines are never silently discarded.
    /// </summary>
    public AspireFixture()
    {
    }

    public void SetOutput(ITestOutputHelper? output) => _output = output;

    public ITestOutputHelper? Output => _output ?? TestContext.Current?.TestOutputHelper;

    public string BlobEndpoint { get; private set; } = string.Empty;

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
                BlobEndpoint = _sharedBlobEndpoint;
                Log($"Reusing shared postgres connection: {LogPostgresAddress(_postgresBaseConnectionString)}");
                return;
            }

            Log("Attempting to use fixed well-known endpoints (pre-warmed containers)...");
            if (await TryUseFixedEndpointsAsync())
            {
                Log($"Using fixed endpoints — postgres=127.0.0.1:{PostgresPort} blob=127.0.0.1:{BlobPort}");
                _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
                _sharedBlobEndpoint = BlobEndpoint;
                return;
            }

            Log("Fixed endpoints not available. Querying Docker for persistent container ports...");
            if (await TryUsePersistentContainerEndpointsAsync())
            {
                Log($"Using persistent container endpoints — postgres={LogPostgresAddress(_postgresBaseConnectionString)} blob={BlobEndpoint}");
                _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
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
                BlobEndpoint = app.GetEndpoint(BlobResourceName).AbsoluteUri.TrimEnd('/');

                Log($"Aspire host provisioned — postgres={LogPostgresAddress(_postgresBaseConnectionString)} blob={BlobEndpoint}");

                await WaitUntilPostgresAcceptsConnectionsAsync(cts.Token);

                await app.ResourceNotifications
                    .WaitForResourceHealthyAsync(BlobResourceName, cts.Token);

                _sharedApp = app;
                _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
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
                        _sharedBlobEndpoint = BlobEndpoint;
                        return;
                    }

                    if (await TryUsePersistentContainerEndpointsAsync())
                    {
                        Log($"Retry {retry}/10: Persistent container endpoints now reachable.");
                        _sharedPostgresBaseConnectionString = _postgresBaseConnectionString;
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
            BlobEndpoint = $"http://127.0.0.1:{BlobPort}";

            bool[] results = await Task.WhenAll(
                TryPostgresAsync(),
                TryBlobAsync());

            bool postgresOk = results[0];
            bool blobOk = results[1];

            Log($"Fixed endpoint check (attempt {attempt}/{MaxEndpointCheckAttempts}): postgres={postgresOk} blob={blobOk}");

            if (postgresOk && blobOk)
            {
                return true;
            }
        }

        _postgresBaseConnectionString = null;
        BlobEndpoint = string.Empty;
        return false;
    }

    private async Task<bool> TryUsePersistentContainerEndpointsAsync()
    {
        int? mappedPostgresPort = TryGetPublishedPort(PostgresContainerName, "5432/tcp");
        int? mappedBlobPort = TryGetPublishedPort(BlobContainerName, "9000/tcp");

        Log($"Persistent container port discovery — postgres={mappedPostgresPort?.ToString() ?? "not found"} blob={mappedBlobPort?.ToString() ?? "not found"}");

        if (mappedPostgresPort is null || mappedBlobPort is null)
        {
            return false;
        }

        _postgresBaseConnectionString = BuildConnectionString(mappedPostgresPort.Value, MaintenanceDatabaseName);
        BlobEndpoint = $"http://127.0.0.1:{mappedBlobPort.Value}";

        bool[] results = await Task.WhenAll(
            TryPostgresAsync(),
            TryBlobAsync());

        bool postgresOk = results[0];
        bool blobOk = results[1];

        Log($"Persistent container endpoint check: postgres={postgresOk} blob={blobOk}");

        if (postgresOk && blobOk)
        {
            return true;
        }

        _postgresBaseConnectionString = null;
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
            Output?.WriteLine($"[AspireFixture {DateTime.UtcNow:HH:mm:ss.fff}] {message}");
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
