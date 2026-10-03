namespace SmoothAiProductContextMemory.Knowledge;

public sealed class BudgetLedger(BudgetState initial, Func<BudgetState, Task> persist, Func<Step, Task>? persistStep = null)
{
    private readonly SemaphoreSlim gate = new(1);
    private BudgetState state = initial;
    private readonly List<Step> steps = [];
    public BudgetState State => state;
    public IReadOnlyList<Step> Steps => steps.ToArray();
    public async Task TraceAsync(string node, string outcome, string? model, int milliseconds = 0)
    {
        var step = new Step(node, outcome, model, milliseconds, DateTimeOffset.UtcNow);
        steps.Add(step);
        if (persistStep is not null) await persistStep(step);
    }
    public static BudgetState Start(BudgetLimits limits) => new(limits, new Usage(), []);

    public async Task<Reservation> ReserveAsync(object data, int outputLimit, decimal inputRate, decimal outputRate)
    {
        await gate.WaitAsync();
        try
        {
            int input = System.Text.Encoding.UTF8.GetByteCount(KnowledgeJson.Serialize(data)) + 2048;
            decimal dollars = input * inputRate / 1000000m + outputLimit * outputRate / 1000000m;
            var used = state.Usage;
            var reservations = state.Reservations;
            if (used.Calls + reservations.Count >= state.Limits.Calls || used.InputTokens + reservations.Sum(r => r.InputTokens) + input > state.Limits.InputTokens || used.OutputTokens + reservations.Sum(r => r.OutputTokens) + outputLimit > state.Limits.OutputTokens || used.EstimatedDollars + reservations.Sum(r => r.Dollars) + dollars > state.Limits.Dollars)
                throw new KnowledgeException("budget_exhausted");
            var reservation = new Reservation(Guid.NewGuid(), input, outputLimit, dollars);
            state = state with { Reservations = [.. reservations, reservation] };
            await persist(state);
            return reservation;
        }
        finally { gate.Release(); }
    }

    public async Task SettleAsync(Reservation reservation, int? input, int? output, decimal inputRate, decimal outputRate)
    {
        await gate.WaitAsync();
        try
        {
            int actualInput = input ?? reservation.InputTokens;
            int actualOutput = output ?? reservation.OutputTokens;
            state = state with
            {
                Reservations = state.Reservations.Where(r => r.Id != reservation.Id).ToArray(),
                Usage = new Usage(state.Usage.Calls + 1, state.Usage.InputTokens + actualInput, state.Usage.OutputTokens + actualOutput,
                    state.Usage.EstimatedDollars + actualInput * inputRate / 1000000m + actualOutput * outputRate / 1000000m, true)
            };
            await persist(state);
            if (actualInput > reservation.InputTokens || actualOutput > reservation.OutputTokens)
                throw new KnowledgeException("provider_usage_exceeded_reservation");
        }
        finally { gate.Release(); }
    }

    public Usage Usage => state.Usage with { OutstandingReservations = state.Reservations.Count };
}
