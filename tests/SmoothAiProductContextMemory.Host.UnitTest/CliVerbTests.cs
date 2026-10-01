extern alias HostApp;

using System.Text;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Host.Cli;
using SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;

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
///
/// Isolated two ways. Each verb is driven through its settings overload, so the host's configuration
/// is exactly what the test supplies: no process-wide environment variables are mutated, and a
/// developer's user secrets cannot point a verb at a real store. And the class runs in a
/// non-parallel collection, because the empty-argv case redirects the process-wide
/// <see cref="Console.Out"/>, which would swallow or interleave any concurrently running test's output.
/// </remarks>
[Collection(CliVerbCollection.Name)]
public sealed class CliVerbTests
{
    private static readonly IReadOnlyDictionary<string, string?> NoSettings = new Dictionary<string, string?>();

    // The full host configuration is required, not just a connection string: resolving the repository
    // pulls in blob storage, and that resolution happens before the verb's own try/catch.
    private static readonly IReadOnlyDictionary<string, string?> RestoreSettings = new Dictionary<string, string?>
    {
        ["ConnectionStrings:SmoothAiProductContextMemory"] = "Host=127.0.0.1;Database=none;Username=none;Password=none;Timeout=1",
        ["BlobStorage:Endpoint"] = "http://127.0.0.1:1",
        ["BlobStorage:AccessKey"] = "unused",
        ["BlobStorage:SecretKey"] = "unused",
        ["BlobStorage:Bucket"] = "unused",
    };

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
        // because an unconfigured host fails inside AddInfrastructure before the verb does anything.
        //
        // The exit code alone is NOT the claim, and asserting only "non-zero" was the defect: `verify`
        // returns 1 both for a parse error and for a clean parse whose archive turns out absent, so a
        // regression making the argument optional would still return 1 and this would stay green. The
        // claim is that the action never ran, and the action is the only thing that prints a
        // `Verify …` / `Restore …` line. Asserting that line is absent is what makes the refusal
        // observable, and unlike a code comparison it does not depend on which non-zero the parser
        // chose.
        //
        // For `restore` the code is independently discriminating: 2 is "the archive is bad" and 0 is a
        // clean reconcile, so an argument that stopped being required would have to return one of
        // those instead of 1.
        var stdout = new StringWriter();
        TextWriter previous = Console.Out;
        Console.SetOut(stdout);
        try
        {
            int exit = verb switch
            {
                "verify" => await VerifyCommand.InvokeAsync([], NoSettings),
                "restore" => await RestoreCommand.InvokeAsync([], RestoreSettings),
                _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unknown verb"),
            };

            // 1 is System.CommandLine's parse-failure code, and on its own it proves nothing: with the
            // argument relaxed to ZeroOrOne the exit is *still* 1, because the action then runs and
            // fails on the null path. The exit code is therefore not the claim.
            exit.ShouldBe(1, $"{verb} did not fail argument parsing on an empty argv");

            // The load-bearing assertion is that the PARSER rejected the argv. System.CommandLine
            // renders the command's help on a parse error and prints nothing when the action runs, so
            // "Usage:" is present on the refusal path and absent on the action path. That is the
            // opposite shape to the obvious "the action did not print", which cannot work: under the
            // ZeroOrOne mutation the action's own verdict line is also absent (it throws before
            // printing), so an absence check passes on the regression it is meant to catch. Verified
            // both ways: relaxing the arity makes this fail.
            string printed = stdout.ToString();
            printed.ShouldContain("Usage:", Case.Sensitive,
                $"the {verb} action ran instead of the parser refusing the empty argv, so the "
                + "required argument is no longer required");
            printed.ShouldNotContain(verb == "verify" ? "Verify clean:" : "Restore reconciled:",
                Case.Sensitive,
                $"the {verb} action ran and reported, so an empty argv was accepted rather than refused");
        }
        finally
        {
            Console.SetOut(previous);
        }
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
            int exit = await VerifyCommand.InvokeAsync([path], NoSettings);
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
        // The full host configuration is required (RestoreSettings): a half-configured host fails
        // inside System.CommandLine's default handler and returns 1, which would have made this test
        // pass for the wrong reason.
        int exit = await RestoreCommand.InvokeAsync([AbsentPath()], RestoreSettings);
        exit.ShouldBe(2, "an unverifiable archive must exit 2 (integrity), not 1 (operational)");
    }

    [Fact]
    public async Task Restore_of_an_archive_it_may_not_read_exits_operational_not_integrity()
    {
        // A permissions refusal says nothing about the archive's bytes. Reporting it as 2 would tell
        // the operator to replace a sound archive instead of fixing a file mode.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes only.");
            return;
        }

        string path = AbsentPath();
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            bool bypassesModes;
            try
            {
                using FileStream probe = File.OpenRead(path);
                bypassesModes = true;
            }
            catch (UnauthorizedAccessException)
            {
                bypassesModes = false;
            }

            Assert.SkipWhen(bypassesModes, "The test process can read a mode-000 file (running as root).");

            int exit = await RestoreCommand.InvokeAsync([path], RestoreSettings);
            exit.ShouldBe(1, "an archive the process may not open is an operational failure (1), not an integrity one (2)");
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Verify_exits_zero_on_a_clean_archive()
    {
        // Every other verify case exits 1, so a verb that returned 1 unconditionally passed them all.
        string directory = Path.Combine(Path.GetTempPath(), $"mimisbrunnr-cli-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "snapshot.tar");
        try
        {
            await new TarSnapshotArchive().WriteAsync(
                path,
                new SnapshotCapture([], [], [], [], [], [], [], [], [], []),
                new SnapshotWalkResult([], 0, 0),
                (_, _) => throw new InvalidOperationException("an empty capture cites no body"),
                TestContext.Current.CancellationToken);

            int exit = await VerifyCommand.InvokeAsync([path], NoSettings);

            exit.ShouldBe(0, "a clean archive must verify with exit 0");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliVerbCollection
{
    public const string Name = "CLI verbs";
}
