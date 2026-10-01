extern alias HostApp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.TestFramework.Fixtures;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// Host fixture for HLD-002 NFR-01's log assertion: every Serilog level and category forced to
/// <c>Verbose</c>, and a capturing provider that records the rendered message, the exception, the
/// message template and every structured property — so a value carried only as a property, or only at
/// Debug, is still seen.
/// </summary>
public sealed class SecretContainmentWebAppFixture : WebAppFixture<HostApp::Program>
{
    private static readonly string[] OverriddenCategories =
    [
        "Microsoft", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions.Http.Resilience",
        "Microsoft.Hosting.Lifetime", "Npgsql", "OpenTelemetry", "Polly", "System",
    ];

    private readonly string _databaseName = $"host-integration-{Guid.NewGuid():N}";
    private readonly string _bucket = $"host-secret-{Guid.NewGuid():N}";

    public FullLogCapture Logs { get; } = new();

    protected override string DatabaseName => _databaseName;

    protected override bool RecreateDatabaseOnInitialize => true;

    protected override bool RemoveHostedServices => false;

    protected override Task EnrichConfigurationAsync(Dictionary<string, string?> overrides)
    {
        overrides["ConnectionStrings:SmoothAiProductContextMemory"] = Aspire.CreateDatabaseConnectionString(DatabaseName);
        overrides["BlobStorage:Endpoint"] = Aspire.BlobEndpoint;
        overrides["BlobStorage:AccessKey"] = AspireFixture.BlobAccessKey;
        overrides["BlobStorage:SecretKey"] = AspireFixture.BlobSecretKey;
        overrides["BlobStorage:Bucket"] = _bucket;
        overrides["ApiAccess:ReadToken"] = HostWebAppFixture.ReadToken;
        overrides["ApiAccess:WriteToken"] = HostWebAppFixture.WriteToken;
        overrides["Logging:Serilog:MinimumLevel:Default"] = "Verbose";
        foreach (string category in OverriddenCategories)
        {
            overrides[$"Logging:Serilog:MinimumLevel:Override:{category}"] = "Verbose";
        }

        return Task.CompletedTask;
    }

    protected override Task PostInitializeAsync()
    {
        HttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostWebAppFixture.WriteToken);
        return Task.CompletedTask;
    }

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<ILoggerProvider>(Logs);

    protected override async ValueTask DisposeCleanupAsync()
    {
        try
        {
            string maintenance = Aspire.CreateDatabaseConnectionString("postgres");
            await PostgreSqlDatabaseManager.DropDatabaseIfExistsAsync(maintenance, _databaseName);
        }
        catch (Exception exception)
        {
            Aspire.Output?.WriteLine($"[SecretContainmentWebAppFixture] failed to drop test database '{_databaseName}': {exception.Message}");
        }

        await BlobBucketCleanup.DeleteBlobBucketAsync(
            Aspire.BlobEndpoint,
            AspireFixture.BlobAccessKey,
            AspireFixture.BlobSecretKey,
            _bucket,
            report: message => Aspire.Output?.WriteLine($"[SecretContainmentWebAppFixture] {message}"));
    }

    /// <summary>
    /// Serilog forwards to providers with its <c>LogEvent</c> as the state, so the template and every
    /// property are read from it as well as from the rendered message.
    /// </summary>
    public sealed class FullLogCapture : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Text)> _records = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<(LogLevel Level, string Text)> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(LogLevel level, string text)
        {
            lock (_gate)
            {
                _records.Add((level, text));
            }
        }

        private sealed class CapturingLogger(FullLogCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                owner.Add(LogLevel.None, $"scope {category}: {Describe(state)}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Add(
                    logLevel,
                    $"[{logLevel}] {category}: {formatter(state, exception)} | {Describe(state)} | {exception}");
            }

            private static string Describe(object? state) => state switch
            {
                Serilog.Events.LogEvent logEvent => logEvent.MessageTemplate.Text + " "
                    + string.Join(" ", logEvent.Properties.Select(p => $"{p.Key}={p.Value}")),
                IEnumerable<KeyValuePair<string, object?>> pairs => string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}")),
                _ => state?.ToString() ?? string.Empty,
            };
        }
    }
}
