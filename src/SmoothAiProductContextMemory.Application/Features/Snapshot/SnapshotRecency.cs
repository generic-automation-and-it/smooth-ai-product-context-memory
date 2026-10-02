namespace SmoothAiProductContextMemory.Application.Features.Snapshot;

/// <summary>
/// Human-readable age of the last snapshot, shared by the read-only preflight report and the startup
/// migration posture. Scale to minutes/hours/days so the statement stays readable as the snapshot ages
/// (raw minutes reads "10080 minutes ago" at a week). Clamp at zero: clock skew ahead of the capture
/// time would otherwise render a negative age.
/// </summary>
public static class SnapshotRecency
{
    public static string Format(TimeSpan age)
    {
        if (age < TimeSpan.Zero) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} minutes ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} hours ago";
        return $"{(int)age.TotalDays} days ago";
    }
}
