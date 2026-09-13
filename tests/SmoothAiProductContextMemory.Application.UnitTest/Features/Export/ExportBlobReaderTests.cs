using System.Text;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.Export;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.Export;

public class ExportBlobReaderTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("text/plain", true)]
    [InlineData("text/markdown; charset=utf-8", true)]
    [InlineData("application/json", true)]
    [InlineData("application/octet-stream", false)]
    [InlineData("image/png", false)]
    public void Text_content_types_are_classified(string? contentType, bool expected)
    {
        ExportBlobReader.IsTextContentType(contentType).ShouldBe(expected);
    }

    [Fact]
    public async Task Invalid_utf8_is_non_text()
    {
        await using var blob = new BlobContent(new MemoryStream([0x80, 0x81, 0x82]), "text/plain");
        (BlobRenderState state, string? text) = await ExportBlobReader.ReadAsync(blob, TestContext.Current.CancellationToken);
        state.ShouldBe(BlobRenderState.NonText);
        text.ShouldBeNull();
    }

    [Fact]
    public async Task Utf8_text_is_inlined()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("hello");
        await using var blob = new BlobContent(new MemoryStream(bytes), "text/plain; charset=utf-8");
        (BlobRenderState state, string? text) = await ExportBlobReader.ReadAsync(blob, TestContext.Current.CancellationToken);
        state.ShouldBe(BlobRenderState.Inlined);
        text.ShouldBe("hello");
    }
}
