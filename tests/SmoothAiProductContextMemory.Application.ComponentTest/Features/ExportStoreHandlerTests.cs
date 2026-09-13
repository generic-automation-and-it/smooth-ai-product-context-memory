using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.Export;
using SmoothAiProductContextMemory.Domain;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Export;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

public sealed class ExportStoreHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    private static readonly DateTimeOffset ValidFrom = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedOn = new(2024, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProductGroupUuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProgramGroupUuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SelfGroupUuid = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ClashGroupUuid = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid EmptyGroupUuid = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ProductMemoryUuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LinkedMemoryUuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string BlobAddress = "ab/cd/abcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcd";
    private const string BlobBody = "INLINE_BLOB_BODY";
    private const string Superseded = "CLAIM_SUPERSEDED_V1";
    private const string CurrentClaim = "CLAIM_CURRENT_V2";

    [Fact]
    public async Task Empty_store_writes_marker_only_and_is_idempotent()
    {
        using TempExportDir dir = new();
        ExportStore.Handler handler = NewHandler(new DictionaryBlobStorage());

        ExportStore.Response first = await handler.Handle(new ExportStore.Request(dir.Path), Ct);
        first.Groups.ShouldBe(0);
        first.FilesWritten.ShouldBe(0);
        File.Exists(Path.Combine(dir.Path, ExportPaths.MarkerFileName)).ShouldBeTrue();
        Directory.Exists(Path.Combine(dir.Path, ExportPaths.GroupsDirectory)).ShouldBeFalse();

        Dictionary<string, string> hashes = HashTree(dir.Path);
        await handler.Handle(new ExportStore.Request(dir.Path), Ct);
        HashTree(dir.Path).ShouldBe(hashes);
    }

    [Fact]
    public async Task Seeded_store_current_only_inlines_blob_and_marks_scope()
    {
        await SeedAsync(blobAddress: BlobAddress);
        using TempExportDir dir = new();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, BlobBody);

        ExportStore.Response response = await NewHandler(blobs).Handle(new ExportStore.Request(dir.Path), Ct);

        response.Groups.ShouldBe(5);
        response.MissingBlobs.ShouldBe(0);

        string productGroup = File.ReadAllText(Path.Combine(dir.Path, "groups", "product-group", "_group.md"));
        productGroup.ShouldContain("scope: product");
        productGroup.ShouldNotContain(ExportScopeMarks.ProgramBanner);

        string programGroup = File.ReadAllText(Path.Combine(dir.Path, "groups", "program-group", "_group.md"));
        programGroup.ShouldContain("scope: program");
        programGroup.ShouldContain(ExportScopeMarks.ProgramBanner);

        string selfGroup = File.ReadAllText(Path.Combine(dir.Path, "groups", "self-group", "_group.md"));
        selfGroup.ShouldContain("scope: self");
        selfGroup.ShouldContain(ExportScopeMarks.SelfBanner);

        string memoryPath = Path.Combine(dir.Path, "groups", "product-group", "mem-postgresql-is-the-storage-engine.md");
        string memory = File.ReadAllText(memoryPath);
        memory.ShouldContain($"group_uuid: `{ProductGroupUuid:D}`");
        memory.ShouldContain($"valid_from: {ExportRenderer.FormatTimestamp(ValidFrom)}");
        memory.ShouldContain($"created_on: {ExportRenderer.FormatTimestamp(CreatedOn)}");
        memory.ShouldContain(CurrentClaim);
        memory.ShouldNotContain(Superseded);
        memory.ShouldContain(BlobBody);
        memory.ShouldNotContain(BlobAddress);
        memory.ShouldContain("outgoing depends_on");
        memory.ShouldContain($"group_uuid: `{ProductGroupUuid:D}`");

        File.Exists(Path.Combine(dir.Path, "groups", "empty-group", "_group.md")).ShouldBeTrue();
        Directory.GetFiles(Path.Combine(dir.Path, "groups", "empty-group"), "mem-*.md").ShouldBeEmpty();

        Directory.Exists(Path.Combine(dir.Path, "groups", "product-group")).ShouldBeTrue();
        Directory.Exists(Path.Combine(dir.Path, "groups", $"product-group--{ExportPaths.CollisionSuffix(ClashGroupUuid)}"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task History_includes_superseded_claim()
    {
        await SeedAsync(blobAddress: BlobAddress);
        using TempExportDir dir = new();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, BlobBody);

        await NewHandler(blobs).Handle(new ExportStore.Request(dir.Path, IncludeHistory: true), Ct);

        string memory = File.ReadAllText(Path.Combine(dir.Path, "groups", "product-group", "mem-postgresql-is-the-storage-engine.md"));
        memory.ShouldContain(CurrentClaim);
        memory.ShouldContain(Superseded);
        memory.ShouldContain("## Version 1");
    }

    [Fact]
    public async Task Missing_blob_warns_and_continues()
    {
        await SeedAsync(blobAddress: BlobAddress);
        using TempExportDir dir = new();
        var logger = new CollectingLogger<ExportStore.Handler>();

        ExportStore.Response response = await NewHandler(new DictionaryBlobStorage(), logger)
            .Handle(new ExportStore.Request(dir.Path), Ct);

        response.MissingBlobs.ShouldBe(1);
        logger.Warnings.ShouldContain(w => w.Contains(ProductMemoryUuid.ToString("D"), StringComparison.Ordinal));
        logger.Warnings.ShouldAllBe(w => !w.Contains(BlobBody, StringComparison.Ordinal));

        string memory = File.ReadAllText(Path.Combine(dir.Path, "groups", "product-group", "mem-postgresql-is-the-storage-engine.md"));
        memory.ShouldContain(ExportScopeMarks.MissingBlobNote);
        memory.ShouldNotContain(BlobAddress);
        memory.ShouldContain(CurrentClaim);
        File.Exists(Path.Combine(dir.Path, "groups", "self-group", "_group.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task Two_runs_are_byte_identical()
    {
        await SeedAsync(blobAddress: BlobAddress);
        using TempExportDir dir = new();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, BlobBody);
        ExportStore.Handler handler = NewHandler(blobs);

        await handler.Handle(new ExportStore.Request(dir.Path), Ct);
        Dictionary<string, byte[]> first = Snapshot(dir.Path);

        await handler.Handle(new ExportStore.Request(dir.Path), Ct);
        Dictionary<string, byte[]> second = Snapshot(dir.Path);

        second.Keys.ShouldBe(first.Keys, ignoreOrder: true);
        foreach (string key in first.Keys)
        {
            second[key].ShouldBe(first[key]);
        }
    }

    [Fact]
    public async Task Two_history_runs_are_byte_identical()
    {
        await SeedAsync(blobAddress: BlobAddress);
        using TempExportDir dir = new();
        var blobs = new DictionaryBlobStorage();
        blobs.Add(BlobAddress, BlobBody);
        ExportStore.Handler handler = NewHandler(blobs);

        await handler.Handle(new ExportStore.Request(dir.Path, IncludeHistory: true), Ct);
        Dictionary<string, byte[]> first = Snapshot(dir.Path);

        await handler.Handle(new ExportStore.Request(dir.Path, IncludeHistory: true), Ct);
        Dictionary<string, byte[]> second = Snapshot(dir.Path);

        second.Keys.ShouldBe(first.Keys, ignoreOrder: true);
        foreach (string key in first.Keys)
        {
            second[key].ShouldBe(first[key]);
        }
    }

    // Note: duplicate current versions cannot be seeded — the partial unique index
    // IX_memory_version_memory_id (where is_current) rejects them at the database level.
    // The handler's multiple-current warning remains as defense-in-depth only.

    private ExportStore.Handler NewHandler(IBlobStorage blobs, ILogger<ExportStore.Handler>? logger = null) =>
        new(AppDb, blobs, new FileSystemMarkdownExportSink(NullLogger<FileSystemMarkdownExportSink>.Instance),
            logger ?? NullLogger<ExportStore.Handler>.Instance);

    private async Task SeedAsync(string? blobAddress)
    {
        MemoryGroup product = Group(ProductGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        MemoryGroup program = Group(ProgramGroupUuid, MemoryGroup.ScopeDimensionValue.Program, "programme-1");
        MemoryGroup self = Group(SelfGroupUuid, MemoryGroup.ScopeDimensionValue.Self);
        MemoryGroup clash = Group(ClashGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        MemoryGroup empty = Group(EmptyGroupUuid, MemoryGroup.ScopeDimensionValue.Product);
        Db.MemoryGroups.AddRange(product, program, self, clash, empty);
        await Db.SaveChangesAsync(Ct);

        Describe(product, 1, "Product group", "Product body");
        Describe(program, 1, "Program group", "Program body");
        Describe(self, 1, "Self group", "Self body");
        Describe(clash, 1, "Product group", "Clash body");
        Describe(empty, 1, "Empty group", "Empty body");
        await Db.SaveChangesAsync(Ct);

        Memory subject = MemoryRow(product.Id, ProductMemoryUuid, "Postgres storage", "PostgreSQL is the storage engine");
        Memory other = MemoryRow(product.Id, LinkedMemoryUuid, "Linked memory", "A related fact");
        Memory programMemory = MemoryRow(program.Id, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "Program fact", "Programme plan item");
        Memory selfMemory = MemoryRow(self.Id, Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), "Editor font", "Prefer a readable font");
        Db.Memories.AddRange(subject, other, programMemory, selfMemory);
        await Db.SaveChangesAsync(Ct);

        var v1 = Version(subject.Id, 1, Superseded, isCurrent: true, blobAddress: null);
        Db.MemoryVersions.Add(v1);
        await Db.SaveChangesAsync(Ct);

        await using var tx = await Db.Database.BeginTransactionAsync(Ct);
        v1.IsCurrent = false;
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(Version(subject.Id, 2, CurrentClaim, isCurrent: true, blobAddress: blobAddress));
        await Db.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);

        Db.MemoryVersions.Add(Version(other.Id, 1, "Related claim", isCurrent: true, blobAddress: null));
        Db.MemoryVersions.Add(Version(programMemory.Id, 1, "Programme claim", isCurrent: true, blobAddress: null));
        Db.MemoryVersions.Add(Version(selfMemory.Id, 1, "Preference claim", isCurrent: true, blobAddress: null));
        await Db.SaveChangesAsync(Ct);

        Db.MemoryLinks.Add(new MemoryLink
        {
            SourceMemoryId = subject.Id,
            TargetMemoryId = other.Id,
            Relation = MemoryLink.RelationValue.DependsOn,
            Reason = "needs it",
        });
        await Db.SaveChangesAsync(Ct);
    }

    private static MemoryGroup Group(Guid uuid, string scope, string? scopeIdentifier = null) => new()
    {
        Uuid = uuid,
        ScopeDimension = scope,
        ScopeIdentifier = scopeIdentifier,
        InitiativeId = TestEntities.DefaultInitiativeId,
        Repo = "kingstown",
        RepoUrl = "https://example.invalid/repo",
        Tickets = [TicketDocument.Create("jira", "ACM-1", "https://example.invalid/ACM-1")],
        CreatedOn = CreatedOn,
    };

    private static void Describe(MemoryGroup group, int version, string name, string body) =>
        group.Descriptions.Add(new GroupDescription
        {
            GroupId = group.Id,
            Version = version,
            Name = name,
            Body = body,
            CreatedOn = CreatedOn,
        });

    private static Memory MemoryRow(long groupId, Guid uuid, string name, string description) => new()
    {
        Uuid = uuid,
        LineageId = uuid,
        GroupId = groupId,
        Name = name,
        Description = description,
        SubjectSlug = Slug.Subject(description),
        Tags = ["persistence"],
        Facets = ["architecture"],
    };

    private static MemoryVersion Version(long memoryId, int version, string statement, bool isCurrent, string? blobAddress) => new()
    {
        MemoryId = memoryId,
        Version = version,
        IsCurrent = isCurrent,
        Statement = statement,
        ContentSummary = $"Summary of {statement}",
        BlobAddress = blobAddress,
        Kind = MemoryVersion.KindValue.Decision,
        Confidence = 80,
        Status = MemoryVersion.MemoryVersionStatus.Approved,
        Sources = [SourceDocument.Create("jira", "ACM-1", ValidFrom)],
        ValidFrom = ValidFrom,
        CreatedOn = CreatedOn,
    };

    private static Dictionary<string, string> HashTree(string root) =>
        Snapshot(root).ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)));

    private static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToDictionary(
                p => Path.GetRelativePath(root, p).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private sealed class TempExportDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cm-export-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class DictionaryBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public void Add(string address, string text) => _blobs[address] = Encoding.UTF8.GetBytes(text);

        public Task<string> StoreAsync(Stream content, string? contentType = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("unused");

        public Task<BlobContent?> GetAsync(string address, CancellationToken cancellationToken = default)
        {
            if (!_blobs.TryGetValue(address, out byte[]? bytes))
            {
                return Task.FromResult<BlobContent?>(null);
            }

            return Task.FromResult<BlobContent?>(new BlobContent(new MemoryStream(bytes, writable: false), "text/plain; charset=utf-8"));
        }

        public Task<bool> ExistsAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.ContainsKey(address));

        public Task DeleteAsync(string address, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
