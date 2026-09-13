using Microsoft.Extensions.Logging;

namespace SmoothAiProductContextMemory.TestFramework.Logging;

/// <summary>
/// Captures every rendered log record, including exception text. Serilog runs with
/// <c>writeToProviders: true</c>, so registering this provider sees exactly what the OpenTelemetry
/// provider sees — one capture point covers both destinations.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _records = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Records
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

    private void Add(string record)
    {
        lock (_gate)
        {
            _records.Add(record);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            string message = formatter(state, exception);
            string exceptionText = exception is null ? string.Empty : $" | {exception}";
            owner.Add($"[{logLevel}] {category}: {message}{exceptionText}");
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
