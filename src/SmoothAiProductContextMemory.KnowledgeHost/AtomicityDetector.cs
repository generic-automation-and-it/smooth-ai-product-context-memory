using System.Diagnostics;
using System.Text.Json;
using SmoothAiProductContextMemory.Knowledge;

namespace SmoothAiProductContextMemory.KnowledgeHost;

public sealed class AtomicityDetector(string python, string script) : IAtomicityDetector
{
    public async Task ValidateAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(script);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            process.Start();
            started = true;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(KnowledgeJson.Serialize(candidates.Select(candidate => new { candidate.Statement })).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0) throw new KnowledgeException("atomicity_detector_failed");
            using var result = JsonDocument.Parse(await output);
            var verdicts = result.RootElement.GetProperty("results");
            if (verdicts.GetArrayLength() != candidates.Count) throw new KnowledgeException("atomicity_detector_failed");
            foreach (var verdict in verdicts.EnumerateArray())
            {
                string? value = verdict.GetProperty("verdict").GetString();
                if (value == "bundled") throw new KnowledgeException("non_atomic_claims");
                if (value != "simple") throw new KnowledgeException("atomicity_detector_failed");
            }
        }
        catch (Exception exception) when (exception is not KnowledgeException)
        {
            if (started && !process.HasExited) process.Kill(true);
            throw new KnowledgeException("atomicity_detector_failed");
        }
    }
}
