using System.Diagnostics;

namespace SmoothAiProductContextMemory.TestFramework.Telemetry;

/// <summary>
/// Snapshot of a finished <see cref="Activity"/>. Activities are pooled and their tag collections
/// mutate after <c>OnEnd</c>, so assertions must run against a copy, not the live object.
/// </summary>
public sealed record CapturedSpan(
    string Source,
    string OperationName,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    ActivityStatusCode Status,
    string? StatusDescription,
    IReadOnlyDictionary<string, string?> Tags,
    IReadOnlyList<string> EventTexts)
{
    public static CapturedSpan From(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        Dictionary<string, string?> tags = [];
        foreach (KeyValuePair<string, string?> tag in activity.Tags)
        {
            tags[tag.Key] = tag.Value;
        }

        List<string> eventTexts = [];
        foreach (ActivityEvent activityEvent in activity.Events)
        {
            foreach (KeyValuePair<string, object?> tag in activityEvent.Tags)
            {
                eventTexts.Add($"{activityEvent.Name}:{tag.Key}={tag.Value}");
            }
        }

        return new CapturedSpan(
            activity.Source.Name,
            activity.OperationName,
            activity.TraceId.ToString(),
            activity.SpanId.ToString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToString(),
            activity.Status,
            activity.StatusDescription,
            tags,
            eventTexts);
    }

    public IEnumerable<string> SearchableText()
    {
        yield return OperationName;

        if (StatusDescription is not null)
        {
            yield return StatusDescription;
        }

        foreach (KeyValuePair<string, string?> tag in Tags)
        {
            if (tag.Value is not null)
            {
                yield return tag.Value;
            }
        }

        foreach (string eventText in EventTexts)
        {
            yield return eventText;
        }
    }
}
