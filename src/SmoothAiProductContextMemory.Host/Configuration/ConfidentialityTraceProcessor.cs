using System.Diagnostics;
using OpenTelemetry;

namespace SmoothAiProductContextMemory.Host.Configuration;

/// <summary>
/// Enforces NFR-05 inside the trace pipeline, where logging conventions cannot reach.
/// </summary>
/// <remarks>
/// Three routes are closed:
/// <list type="bullet">
/// <item>
/// <c>url.full</c> on an outbound HTTP span. The object store is content-addressed, so the request
/// path <em>is</em> a content address — and NFR-05 treats an address plus store access as equivalent
/// to the content. Scheme and authority survive, so "which store, how slow, did it fail" is still
/// answerable; <c>http.request.method</c> still says whether it was a read or a write.
/// </item>
/// <item>
/// An error status description. A PostgreSQL unique-violation message embeds the offending value in
/// its <c>DETAIL:</c> clause, and Npgsql copies exception text into the activity status.
/// </item>
/// <item>
/// Command text and exception detail tags. Npgsql 10 emits neither today — it publishes only
/// <c>db.namespace</c>, <c>db.system.name</c>, <c>db.response.status_code</c>, <c>error.type</c> and
/// the server address — and instrumentation runs with <c>RecordException</c> off. These are held
/// closed so that turning either on later cannot silently become a content leak.
/// </item>
/// </list>
/// The exception <em>type</em> is deliberately kept: a failure has to stay diagnosable from
/// identifiers alone.
/// </remarks>
internal sealed class ConfidentialityTraceProcessor : BaseProcessor<Activity>
{
    internal const string RedactedFailureDescription = "Operation failed";
    internal const string RedactedPath = "/[redacted]";

    private const string UrlFullTag = "url.full";

    private static readonly string[] ScrubbedTags =
    [
        "db.statement",
        "db.query.text",
        "exception.message",
        "exception.stacktrace",
    ];

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        foreach (string tag in ScrubbedTags)
        {
            data.SetTag(tag, null);
        }

        RedactUrlPath(data);

        if (data.Status is ActivityStatusCode.Error)
        {
            data.SetStatus(ActivityStatusCode.Error, RedactedFailureDescription);
        }
    }

    private static void RedactUrlPath(Activity data)
    {
        if (data.GetTagItem(UrlFullTag) is not string url
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return;
        }

        data.SetTag(UrlFullTag, $"{parsed.Scheme}://{parsed.Authority}{RedactedPath}");
    }
}
