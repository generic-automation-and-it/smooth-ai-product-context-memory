namespace SmoothAiProductContextMemory.Application.UnitTest.Features.Export;

public class GitignoreExportTests
{
    [Fact]
    public void Gitignore_lists_export_output_directories()
    {
        string root = FindRepoRoot();
        string[] lines = File.ReadAllLines(Path.Combine(root, ".gitignore"));

        lines.ShouldContain("/export/");
        lines.ShouldContain(".context/export/");
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, ".gitignore"))
                && File.Exists(Path.Combine(dir, "SmoothAiProductContextMemory.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from the test base directory.");
    }
}
