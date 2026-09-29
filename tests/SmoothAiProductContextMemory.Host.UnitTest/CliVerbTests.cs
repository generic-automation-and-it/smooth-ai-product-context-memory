extern alias HostApp;

using SmoothAiProductContextMemory.Host.Cli;

namespace SmoothAiProductContextMemory.Host.UnitTest;

/// <summary>
/// Pins the one-shot CLI verbs' argument contract and exit codes.
/// </summary>
/// <remarks>
/// These codes are not incidental: HLD-006's NFR-01 is defined against them, and the container
/// entrypoint and an operator script both branch on them. Before this, no test reached any of them —
/// the dispatch is inline top-level statements in the entry point and the four verb classes were
/// called from nowhere, so a verb could stop requiring its argument, or return 0 where 1 is the
/// contract, with the whole suite green.
///
/// Deliberately no database. <c>verify</c> needs no connection string, and <c>restore</c> verifies the
/// archive before touching either store, so both failure paths are reachable with a fake connection
/// string and nothing listening on it.
/// </remarks>
public sealed class CliVerbTests
{
    private static string AbsentPath() =>
        Path.Combine(Path.GetTempPath(), $"mimisbrunnr-absent-{Guid.NewGuid():N}.tar");

    [Theory]
    [InlineData("verify")]
    [InlineData("restore")]
    public async Task A_verb_that_requires_an_argument_refuses_an_empty_argv_rather_than_defaulting(string verb)
    {
        // Only these two require an argument. A verb that accepted no arguments would act on the
        // corpus — the exact irreversible direction the exit codes exist to make visible.
        //
        // `snapshot` and `export` are deliberately NOT here: both give `--output` a
        // DefaultValueFactory, so an empty argv is a *valid* invocation by design and there is no
        // argument contract to protect. They previously sat in this theory anyway, and passed only
        // because an unconfigured host fails inside AddInfrastructure before the verb does anything —
        // the "passes for the wrong reason" hazard the restore case below already documents. A change
        // making either verb reject an empty argv would not have been caught, and a change making one
        // accept it would not have been either, because the assertion was satisfied by a configuration
        // failure rather than by the argument check.
        int exit = verb switch
        {
            "verify" => await VerifyCommand.InvokeAsync([]),
            "restore" => await RestoreCommand.InvokeAsync([]),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unknown verb"),
        };

        exit.ShouldNotBe(0, $"{verb} accepted an empty argv and reported success");
    }

    [Fact]
    public async Task Verify_reports_an_absent_archive_as_a_finding_not_a_crash()
    {
        // Exit 1, not an unhandled exception. The verifier reports an unreadable container as a
        // finding; before that fix TarReader escaped every catch and a truncated or absent file
        // surfaced as an exception rather than a non-zero exit, which is the one thing an operator
        // script cannot distinguish from a bug.
        string path = AbsentPath();
        try
        {
            int exit = await VerifyCommand.InvokeAsync([path]);
            exit.ShouldBe(1, "an absent archive must be a finding (1), not success and not a throw");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Restore_refuses_a_bad_archive_with_the_integrity_code_not_the_operational_one()
    {
        // 2 is "the archive itself is bad", 1 is "the restore ran and did not reconcile". An operator
        // script retrying on 1 must not retry on 2, and collapsing them would make a tampered archive
        // look like a transient database problem.
        //
        // The full host configuration is required, not just a connection string: resolving the
        // repository pulls in blob storage, and that resolution happens *before* the verb's own
        // try/catch — so a half-configured host fails inside System.CommandLine's default handler and
        // returns 1, which would have made this test pass for the wrong reason.
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings__SmoothAiProductContextMemory"] = "Host=127.0.0.1;Database=none;Username=none;Password=none;Timeout=1",
            ["BlobStorage__Endpoint"] = "http://127.0.0.1:1",
            ["BlobStorage__AccessKey"] = "unused",
            ["BlobStorage__SecretKey"] = "unused",
            ["BlobStorage__Bucket"] = "unused",
        };
        var previous = settings.ToDictionary(
            entry => entry.Key,
            entry => Environment.GetEnvironmentVariable(entry.Key));

        foreach ((string name, string? value) in settings)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            int exit = await RestoreCommand.InvokeAsync([AbsentPath()]);
            exit.ShouldBe(2, "an unverifiable archive must exit 2 (integrity), not 1 (operational)");
        }
        finally
        {
            foreach ((string name, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
