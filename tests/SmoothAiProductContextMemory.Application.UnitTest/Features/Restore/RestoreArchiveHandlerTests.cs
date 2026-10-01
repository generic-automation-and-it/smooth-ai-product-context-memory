using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Features.Restore;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.Restore;

/// <summary>
/// The restore handler's own guards, reached directly: the archive fake reports a clean verification,
/// so each guard here is the only thing standing between a bad input and a mutation.
/// </summary>
public sealed class RestoreArchiveHandlerTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("restored body");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain\r\nX-Injected: 1")]
    public async Task A_content_type_the_system_never_writes_is_refused_before_any_body_is_stored(string contentType)
    {
        var blobs = new FakeBlobStorage();
        var repository = new FakeRepository();
        RestoreArchive.Handler handler = Handler(new FakeArchive(Address(Body), Body, contentType), blobs, repository);

        ArchiveVerificationFailedException refusal = await Should.ThrowAsync<ArchiveVerificationFailedException>(
            () => handler.Handle(Request(), Ct).AsTask());

        refusal.Findings.ShouldHaveSingleItem().Kind.ShouldBe(SnapshotFindingKind.Corruption);
        refusal.Message.ShouldNotContain(contentType);
        blobs.StoredContentTypes.ShouldBeEmpty();
        repository.RestoreCalls.ShouldBe(0);
    }

    [Fact]
    public async Task An_allowed_content_type_reaches_the_object_store_unchanged()
    {
        var blobs = new FakeBlobStorage();
        var repository = new FakeRepository();
        RestoreArchive.Handler handler = Handler(
            new FakeArchive(Address(Body), Body, "text/plain; charset=utf-8"), blobs, repository);

        RestoreArchive.Response response = await handler.Handle(Request(), Ct);

        response.Reconciled.ShouldBeTrue();
        blobs.StoredContentTypes.ShouldBe(["text/plain; charset=utf-8"]);
    }

    private static RestoreArchive.Handler Handler(FakeArchive archive, FakeBlobStorage blobs, FakeRepository repository) =>
        new(repository, archive, blobs, NullLogger<RestoreArchive.Handler>.Instance);

    private static RestoreArchive.Request Request() => new("unused.tar", "Host=unused");

    private static string Address(byte[] content)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        return $"{hash[..2]}/{hash[2..4]}/{hash}";
    }

    private static SnapshotCapture CaptureCiting(string address) =>
        new(
            [],
            [],
            [],
            [],
            [new Memory { Id = 1, Uuid = Guid.NewGuid(), LineageId = Guid.NewGuid(), GroupId = 1, Name = "m", Description = "d", SubjectSlug = "d" }],
            [new MemoryVersion { Id = 1, MemoryId = 1, Version = 1, IsCurrent = true, Statement = "s", ContentSummary = "c", BlobAddress = address, Kind = "decision", Confidence = 5, Status = "approved", ValidFrom = DateTimeOffset.UtcNow, CreatedOn = DateTimeOffset.UtcNow }],
            [],
            [],
            [],
            []);

    private sealed class FakeArchive(string address, byte[] archivedBody, string? contentType) : ISnapshotArchive
    {
        public Task<SnapshotWriteReport> WriteAsync(
            string destinationPath,
            SnapshotCapture capture,
            SnapshotWalkResult walk,
            Func<string, CancellationToken, Task<(byte[] Content, string Sha256, string? ContentType)>> readBlobAsync,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SnapshotArchive> ReadAsync(string archivePath, CancellationToken cancellationToken)
        {
            var manifest = new SnapshotManifest(
                SnapshotFormat.Version,
                [new SnapshotArchiveEntry($"blobs/{address}", "unused", archivedBody.Length, contentType)],
                new SnapshotCounts(1, 1, 0, 0, 1, 0, 0),
                new SnapshotExclusions(["recall_feedback"]));
            return Task.FromResult(new SnapshotArchive(
                manifest,
                [$"blobs/{address}"],
                CaptureCiting(address),
                a => a == address,
                _ => archivedBody,
                _ => contentType));
        }

        public Task<SnapshotVerification> VerifyAsync(string archivePath, CancellationToken cancellationToken) =>
            Task.FromResult(new SnapshotVerification(true, []));
    }

    private sealed class FakeBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public List<string?> StoredContentTypes { get; } = [];

        public async Task<string> StoreAsync(Stream content, string? contentType = null, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            byte[] raw = buffer.ToArray();
            string address = Address(raw);
            StoredContentTypes.Add(contentType);

            // Mirrors the S3 adapter: an existing key is never overwritten, so existence is not integrity.
            _objects.TryAdd(address, raw);
            return address;
        }

        public Task<BlobContent?> GetAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(_objects.TryGetValue(address, out byte[]? content)
                ? new BlobContent(new MemoryStream(content, writable: false), null)
                : null);

        public Task<bool> ExistsAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(_objects.ContainsKey(address));
    }

    private sealed class FakeRepository : ISnapshotRepository
    {
        public int RestoreCalls { get; private set; }

        public Task<SnapshotCaptureResult> CaptureAsync(string connectionString, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsTargetEmptyAsync(string connectionString, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<RestoreResults> RestoreAsync(
            string connectionString,
            SnapshotCapture capture,
            SnapshotCounts expected,
            bool overrideNonEmpty,
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            return Task.FromResult(new RestoreResults(
                expected.Memories,
                expected.Versions,
                expected.Vertices,
                expected.Edges,
                expected.TicketVertices,
                expected.TicketEdges,
                expected.Edges,
                Committed: true));
        }
    }
}
