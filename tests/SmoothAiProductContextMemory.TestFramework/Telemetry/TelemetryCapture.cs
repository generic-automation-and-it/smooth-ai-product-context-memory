using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;

namespace SmoothAiProductContextMemory.TestFramework.Telemetry;

/// <summary>
/// Observes what the application's telemetry pipeline actually emits.
/// </summary>
/// <remarks>
/// Spans are captured with a <see cref="BaseProcessor{T}"/> appended to the application's own tracer
/// provider rather than a bare <see cref="ActivityListener"/>. Processors run in registration order,
/// so a processor registered from test services runs <em>after</em> the confidentiality scrubbing one
/// — which means assertions see the post-scrub span, the same thing an exporter would ship. A raw
/// listener would fire in listener-registration order and could observe a pre-scrub span, making a
/// clean pipeline look like a leak.
/// <para>
/// Metric tags come from a <see cref="MeterListener"/>, which has no such ordering concern.
/// </para>
/// </remarks>
public sealed class TelemetryCapture : IDisposable
{
    private readonly MeterListener _meterListener;
    private readonly List<CapturedSpan> _spans = [];
    private readonly List<string> _metricTagValues = [];
    private readonly HashSet<string> _meterNames = [];
    private readonly Lock _gate = new();

    public TelemetryCapture()
    {
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                lock (_gate)
                {
                    _meterNames.Add(instrument.Meter.Name);
                }

                listener.EnableMeasurementEvents(instrument);
            },
        };
        _meterListener.SetMeasurementEventCallback<long>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<double>(OnMeasurement);
        _meterListener.Start();
    }

    public IReadOnlyList<CapturedSpan> Spans
    {
        get
        {
            lock (_gate)
            {
                return [.. _spans];
            }
        }
    }

    public IReadOnlyList<string> MetricTagValues
    {
        get
        {
            lock (_gate)
            {
                return [.. _metricTagValues];
            }
        }
    }

    public IReadOnlyCollection<string> MeterNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _meterNames];
            }
        }
    }

    /// <summary>Register this with the application's tracer provider to start collecting spans.</summary>
    public BaseProcessor<Activity> CreateSpanProcessor() => new SpanCapturingProcessor(this);

    /// <summary>Every string this capture has seen anywhere in a span or a metric tag.</summary>
    public IEnumerable<string> AllTelemetryText()
    {
        foreach (CapturedSpan span in Spans)
        {
            foreach (string text in span.SearchableText())
            {
                yield return text;
            }
        }

        foreach (string tagValue in MetricTagValues)
        {
            yield return tagValue;
        }
    }

    /// <summary>Spans belonging to one trace, so a single request can be inspected in isolation.</summary>
    public IReadOnlyList<CapturedSpan> SpansForTrace(string traceId) =>
        [.. Spans.Where(span => string.Equals(span.TraceId, traceId, StringComparison.Ordinal))];

    public void Dispose() => _meterListener.Dispose();

    private void Add(CapturedSpan span)
    {
        lock (_gate)
        {
            _spans.Add(span);
        }
    }

    private void OnMeasurement<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        where T : struct
    {
        List<string> values = [];
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Value is not null)
            {
                values.Add(tag.Value.ToString() ?? string.Empty);
            }
        }

        if (values.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _metricTagValues.AddRange(values);
        }
    }

    private sealed class SpanCapturingProcessor(TelemetryCapture owner) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => owner.Add(CapturedSpan.From(data));
    }
}
