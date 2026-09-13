using System.Diagnostics;
using SmoothAiProductContextMemory.Host.Configuration;

namespace SmoothAiProductContextMemory.Host.UnitTest;

/// <summary>
/// NFR-05 for span attributes. The failing cases mirror the two real leak routes: PostgreSQL puts the
/// offending value in a unique-violation <c>DETAIL:</c> clause which Npgsql copies into the activity's
/// error status, and command text would carry content if a statement were ever hand-interpolated.
/// </summary>
public sealed class ConfidentialityTraceProcessorTests : IDisposable
{
    private const string Marker = "super-secret-memory-content";

    private readonly ActivitySource _source = new(nameof(ConfidentialityTraceProcessorTests));
    private readonly ActivityListener _listener;

    public ConfidentialityTraceProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == nameof(ConfidentialityTraceProcessorTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public void Scrubs_database_statement_text()
    {
        using Activity activity = StartActivity();
        activity.SetTag("db.statement", $"INSERT INTO memory_versions (statement) VALUES ('{Marker}')");
        activity.SetTag("db.query.text", $"MATCH (m:Memory {{statement: '{Marker}'}}) RETURN m");

        Process(activity);

        activity.GetTagItem("db.statement").ShouldBeNull();
        activity.GetTagItem("db.query.text").ShouldBeNull();
    }

    [Fact]
    public void Replaces_error_status_description_that_carries_content()
    {
        using Activity activity = StartActivity();
        activity.SetStatus(
            ActivityStatusCode.Error,
            $"23505: duplicate key value violates unique constraint \"ix_memory\" DETAIL: Key (statement)=({Marker}) already exists.");

        Process(activity);

        activity.StatusDescription.ShouldBe(ConfidentialityTraceProcessor.RedactedFailureDescription);
        activity.StatusDescription!.ShouldNotContain(Marker);
    }

    [Fact]
    public void Drops_exception_message_and_stack_but_keeps_the_type()
    {
        using Activity activity = StartActivity();
        activity.SetTag("exception.type", "Npgsql.PostgresException");
        activity.SetTag("exception.message", $"Failed writing {Marker}");
        activity.SetTag("exception.stacktrace", $"at Write({Marker})");

        Process(activity);

        activity.GetTagItem("exception.message").ShouldBeNull();
        activity.GetTagItem("exception.stacktrace").ShouldBeNull();
        activity.GetTagItem("exception.type").ShouldBe("Npgsql.PostgresException");
    }

    /// <summary>
    /// The object store is content-addressed, so the request path is a content address. Authority
    /// survives because "which store was slow" must stay answerable.
    /// </summary>
    [Fact]
    public void Redacts_the_content_address_from_an_outbound_url()
    {
        const string address = "ab/cd/abcd000000000000000000000000000000000000000000000000000000000000";
        using Activity activity = StartActivity();
        activity.SetTag("url.full", $"http://127.0.0.1:9002/bucket/{address}");

        Process(activity);

        string redacted = activity.GetTagItem("url.full").ShouldBeOfType<string>();
        redacted.ShouldBe($"http://127.0.0.1:9002{ConfidentialityTraceProcessor.RedactedPath}");
        redacted.ShouldNotContain(address);
    }

    [Fact]
    public void Leaves_a_non_url_span_tag_untouched()
    {
        using Activity activity = StartActivity();
        activity.SetTag("db.system", "postgresql");

        Process(activity);

        activity.Status.ShouldBe(ActivityStatusCode.Unset);
        activity.StatusDescription.ShouldBeNull();
        activity.GetTagItem("db.system").ShouldBe("postgresql");
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private static void Process(Activity activity)
    {
        using ConfidentialityTraceProcessor processor = new();
        processor.OnEnd(activity);
    }

    private Activity StartActivity() =>
        _source.StartActivity("write")
        ?? throw new InvalidOperationException("The activity listener did not sample the activity.");
}
