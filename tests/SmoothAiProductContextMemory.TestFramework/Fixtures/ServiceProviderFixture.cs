using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.TestFramework.Logging;
using Xunit.v3;

namespace SmoothAiProductContextMemory.TestFramework.Fixtures;

/// <summary>
/// Builds an isolated <see cref="ServiceCollection"/>/<see cref="IServiceProvider"/> for L0/L1
/// tests, routing all logging to the active xunit test output.
/// </summary>
public sealed class ServiceProviderFixture : IAsyncDisposable
{
    private readonly XUnitLoggerFactory _loggerFactory = new();
    private ServiceCollection? _services;
    private IServiceProvider? _serviceProvider;

    public ILoggerFactory LoggerFactory => _loggerFactory;

    public void SetOutput(ITestOutputHelper? output) => _loggerFactory.SetOutput(output);

    public ServiceCollection Start(Dictionary<string, string?>? configurationOverrides = null)
    {
        // Captured at Start, the earliest point a caller reaches the fixture from inside a running
        // test. The logger factory's own fallback reads TestContext.Current, which is gone by the
        // time the provider is disposed, so a log line emitted during teardown would otherwise be
        // written to nothing and read as a silent pass.
        _loggerFactory.SetOutput(TestContext.Current?.TestOutputHelper);

        _services = new ServiceCollection();

        if (configurationOverrides is not null)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configurationOverrides)
                .Build();

            _services.AddSingleton(configuration);
            _services.AddSingleton<IConfiguration>(configuration);
        }

        _services.RemoveAll<ILoggerFactory>();
        _services.AddSingleton<ILoggerFactory>(_loggerFactory);
        _services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.Services.RemoveAll<ILoggerFactory>();
            builder.Services.AddSingleton<ILoggerFactory>(_loggerFactory);
        });

        return _services;
    }

    public IServiceProvider Build()
    {
        if (_services is null)
        {
            throw new InvalidOperationException("Call Start() before Build().");
        }

        _serviceProvider = _services.BuildServiceProvider();
        return _serviceProvider;
    }

    public T GetRequiredService<T>()
        where T : notnull
    {
        if (_serviceProvider is null)
        {
            throw new InvalidOperationException("Call Build() before resolving services.");
        }

        return _serviceProvider.GetRequiredService<T>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _loggerFactory.Dispose();
    }
}
