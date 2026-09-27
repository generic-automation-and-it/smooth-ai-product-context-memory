using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class GetMemoryBlobHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Returns_proxied_blob_by_version()
    {
        (Guid uuid, string address) = await SeedBlobAsync("product", "payload bytes");

        GetMemoryBlob.Response response = await NewHandler().Handle(new GetMemoryBlob.Request(uuid, 1), Ct);

        using var reader = new StreamReader(response.Content);
        (await reader.ReadToEndAsync(Ct)).ShouldBe("payload bytes");
        response.ContentType.ShouldBe("text/plain; charset=utf-8");
    }

    [Fact]
    public async Task Missing_version_throws_not_found()
    {
        await Should.ThrowAsync<NotFoundException>(async () =>
            await NewHandler().Handle(new GetMemoryBlob.Request(Guid.NewGuid(), 1), Ct));
    }

    [Fact]
    public async Task Program_scoped_blob_is_blocked_without_the_scope()
    {
        (Guid uuid, _) = await SeedBlobAsync("program", "payload");

        await Should.ThrowAsync<ForbiddenException>(async () =>
            await NewHandler().Handle(new GetMemoryBlob.Request(uuid, 1), Ct));
    }

    [Fact]
    public async Task Program_scoped_blob_is_read_with_the_scope()
    {
        (Guid uuid, _) = await SeedBlobAsync("program", "payload");

        GetMemoryBlob.Response response = await NewHandler().Handle(
            new GetMemoryBlob.Request(uuid, 1, "program"), Ct);

        response.ContentType.ShouldBe("text/plain; charset=utf-8");
    }

    private async Task<(Guid Uuid, string Address)> SeedBlobAsync(string dimension, string content)
    {
        MemoryGroup group = TestEntities.NewGroup(scopeDimension: dimension);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Memory memory = TestEntities.NewMemory(group.Id, "m", "a scoped memory");
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        string address = await Blob.StoreAsync(stream, "text/plain; charset=utf-8", Ct);

        Db.Memories.Add(memory);
        Db.MemoryVersions.Add(new MemoryVersion
        {
            Memory = memory,
            Version = 1,
            IsCurrent = true,
            Statement = "stmt",
            ContentSummary = "summary",
            Kind = "decision",
            Confidence = 50,
            Status = MemoryVersion.MemoryVersionStatus.Proposed,
            BlobAddress = address,
            ValidFrom = DateTimeOffset.UtcNow,
            CreatedOn = DateTimeOffset.UtcNow,
        });
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        return (memory.Uuid, address);
    }

    private GetMemoryBlob.Handler NewHandler() =>
        new(AppDb, Blob, Loggers.CreateLogger<GetMemoryBlob.Handler>());
}

public sealed class GetMemoryVersionsHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Returns_versions_ordered_ascending()
    {
        MemoryGroup group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        Memory memory = TestEntities.NewMemory(group.Id, "m", "a memory");
        Db.Memories.Add(memory);
        Db.MemoryVersions.AddRange(
            Version(memory, 1, current: true, "first"),
            Version(memory, 2, current: false, "second"));
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        GetMemoryVersions.Response response = await NewHandler().Handle(new GetMemoryVersions.Request(memory.Uuid), Ct);

        response.Items.Select(v => v.Version).ShouldBe([1, 2]);
        response.Items[0].Statement.ShouldBe("first");
        response.Items[1].Statement.ShouldBe("second");
    }

    [Fact]
    public async Task Unknown_memory_throws_not_found()
    {
        await Should.ThrowAsync<NotFoundException>(async () =>
            await NewHandler().Handle(new GetMemoryVersions.Request(Guid.NewGuid()), Ct));
    }

    [Fact]
    public async Task Program_scoped_memory_is_blocked_without_the_scope()
    {
        MemoryGroup group = TestEntities.NewGroup(scopeDimension: "program");
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        Memory memory = TestEntities.NewMemory(group.Id, "m", "a memory");
        Db.Memories.Add(memory);
        Db.MemoryVersions.Add(Version(memory, 1, current: true, "first"));
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        await Should.ThrowAsync<ForbiddenException>(async () =>
            await NewHandler().Handle(new GetMemoryVersions.Request(memory.Uuid), Ct));
    }

    private static MemoryVersion Version(Memory memory, int version, bool current, string statement) => new()
    {
        Memory = memory,
        Version = version,
        IsCurrent = current,
        Statement = statement,
        ContentSummary = "summary",
        Kind = "decision",
        Confidence = 50,
        Status = MemoryVersion.MemoryVersionStatus.Proposed,
        ValidFrom = DateTimeOffset.UtcNow,
        CreatedOn = DateTimeOffset.UtcNow,
    };

    private GetMemoryVersions.Handler NewHandler() =>
        new(AppDb, Loggers.CreateLogger<GetMemoryVersions.Handler>());
}
