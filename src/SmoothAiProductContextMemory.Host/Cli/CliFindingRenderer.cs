using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

namespace SmoothAiProductContextMemory.Host.Cli;

/// <summary>
/// Shared renderer for a verification's findings at the CLI boundary. Both verify (which reports the
/// findings directly) and restore (which renders the findings carried by an
/// <see cref="Application.Common.Exceptions.ArchiveVerificationFailedException"/>) print through this
/// one helper so the two verbs never drift in format.
/// </summary>
internal static class CliFindingRenderer
{
    public static void PrintFindings(IReadOnlyList<SnapshotFinding> findings)
    {
        foreach (SnapshotFinding finding in findings)
        {
            Console.WriteLine($"  {finding.Kind}: {finding.EntryName} — {finding.Message}");
        }
    }
}
