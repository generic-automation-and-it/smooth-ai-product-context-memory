using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.RecallFeedback;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.RecallFeedback;

public class RecallFeedbackQueryHandlerTests
{
    [Fact]
    public async Task Get_miss_rate_delegates_and_maps_the_result()
    {
        var query = new FakeRecallFeedbackQuery(
            missRate: new MissRateResult(10, 4, 0.4));

        var response = await new GetMissRate.Handler(query).Handle(
            new GetMissRate.Request(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow),
            CancellationToken.None);

        response.ShouldBe(new GetMissRate.Response(10, 4, 0.4));
    }

    [Fact]
    public async Task Get_never_recalled_delegates_and_maps_the_result()
    {
        var row = new NeverRecalledRow(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-30));
        var query = new FakeRecallFeedbackQuery(neverRecalled: [row]);

        var response = await new GetNeverRecalledMemories.Handler(query).Handle(
            new GetNeverRecalledMemories.Request(DateTimeOffset.UtcNow),
            CancellationToken.None);

        response.Items.ShouldBe([row]);
    }

    [Fact]
    public async Task Reset_delegates_and_returns_the_deleted_count()
    {
        var query = new FakeRecallFeedbackQuery(resetDeleted: 42);

        var response = await new ResetRecallFeedback.Handler(query).Handle(
            new ResetRecallFeedback.Request(),
            CancellationToken.None);

        response.RecordsDeleted.ShouldBe(42);
    }

    private sealed class FakeRecallFeedbackQuery(
        IReadOnlyList<NeverRecalledRow>? neverRecalled = null,
        MissRateResult? missRate = null,
        int resetDeleted = 0) : IRecallFeedbackQuery
    {
        public Task<IReadOnlyList<NeverRecalledRow>> NeverRecalledAsync(
            NeverRecalledRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(neverRecalled ?? []);

        public Task<MissRateResult> MissRateAsync(MissRateRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(missRate ?? new MissRateResult(0, 0, 0));

        public Task<int> ResetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(resetDeleted);
    }
}
