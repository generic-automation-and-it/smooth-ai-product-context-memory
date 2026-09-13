using Microsoft.Extensions.Logging.Abstractions;
using SmoothAiProductContextMemory.Application.Features.Export;
using SmoothAiProductContextMemory.Infrastructure.Export;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest.Export;

public class FileSystemMarkdownExportSinkTests
{
    [Fact]
    public async Task Prepare_refuses_unmarked_non_empty_directory()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "keep.txt"), "nope", TestContext.Current.CancellationToken);

        var sink = new FileSystemMarkdownExportSink(NullLogger<FileSystemMarkdownExportSink>.Instance);
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sink.PrepareAsync(dir.Path, force: false, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("--force");
        File.Exists(Path.Combine(dir.Path, "keep.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Prepare_force_wipes_unmarked_directory()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "keep.txt"), "nope", TestContext.Current.CancellationToken);

        var sink = new FileSystemMarkdownExportSink(NullLogger<FileSystemMarkdownExportSink>.Instance);
        await sink.PrepareAsync(dir.Path, force: true, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(dir.Path, "keep.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(dir.Path, ExportPaths.MarkerFileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task WriteFile_rejects_path_escape()
    {
        using var dir = new TempDir();
        var sink = new FileSystemMarkdownExportSink(NullLogger<FileSystemMarkdownExportSink>.Instance);
        await sink.PrepareAsync(dir.Path, force: false, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => sink.WriteFileAsync("../escape.md", "x\n", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteFile_uses_utf8_without_bom_and_lf()
    {
        using var dir = new TempDir();
        var sink = new FileSystemMarkdownExportSink(NullLogger<FileSystemMarkdownExportSink>.Instance);
        await sink.PrepareAsync(dir.Path, force: false, TestContext.Current.CancellationToken);
        await sink.WriteFileAsync("groups/a/_group.md", "hello\n", TestContext.Current.CancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "groups", "a", "_group.md"), TestContext.Current.CancellationToken);
        bytes.ShouldBe("hello\n"u8.ToArray());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cm-sink-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
