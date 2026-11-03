using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using SmoothAiProductContextMemory.Knowledge;
using SmoothAiProductContextMemory.KnowledgeHost;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((_, configuration) => configuration.MinimumLevel.Information().WriteTo.Console());
bool enabled = builder.Configuration.GetValue<bool>("KnowledgeService:Enabled");
if (enabled)
{
    string Required(string key) => builder.Configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException("Knowledge service configuration missing: " + key);
    string openAiKey = Required("KnowledgeService:OpenAiKey");
    string jevKey = Required("KnowledgeService:JevKey");
    string readToken = Required("KnowledgeService:ReadToken");
    string writeToken = Required("KnowledgeService:WriteToken");
    string coreRead = Required("KnowledgeService:CoreReadToken");
    string coreWrite = Required("KnowledgeService:CoreWriteToken");
    string model = builder.Configuration["KnowledgeService:Model"] ?? "gpt-6-luna";
    string? strongerModel = builder.Configuration["KnowledgeService:StrongerModel"];
    if (model != "gpt-6-luna" && (builder.Configuration["KnowledgeService:Rates:Input"] is null || builder.Configuration["KnowledgeService:Rates:Output"] is null)) throw new InvalidOperationException("Configure KnowledgeService:Rates:Input and Output for the selected model.");
    if (strongerModel is not null && strongerModel != "gpt-6.1-sol" && (builder.Configuration["KnowledgeService:Rates:StrongerInput"] is null || builder.Configuration["KnowledgeService:Rates:StrongerOutput"] is null)) throw new InvalidOperationException("Configure stronger model rates.");
    var rates = builder.Configuration.GetSection("KnowledgeService:Rates").Get<ProviderRates>() ?? new ProviderRates();
    if (new[] { rates.Input, rates.Output, rates.StrongerInput, rates.StrongerOutput, rates.JevInput, rates.JevOutput }.Any(rate => rate < 0)) throw new InvalidOperationException("Provider rates must be nonnegative estimates per million tokens.");
    builder.Services.AddSingleton(rates);
    Uri coreUri = new(Required("KnowledgeService:CoreUrl"));
    builder.Services.AddSingleton<ISecretFilter>(new SecretFilter(builder.Configuration["KnowledgeService:Python"] ?? "python3", Path.Combine(AppContext.BaseDirectory, "redact.py")));
    builder.Services.AddSingleton<IAtomicityDetector>(new AtomicityDetector(builder.Configuration["KnowledgeService:Python"] ?? "python3", Path.Combine(AppContext.BaseDirectory, "atomicity.py")));
    builder.Services.AddSingleton(new CaptureJournal(builder.Configuration["KnowledgeService:JournalPath"] ?? "data/captures.db", builder.Configuration.GetValue<long?>("KnowledgeService:MaxJournalBytes") ?? 128 * 1024 * 1024));
    builder.Services.AddHttpClient("core-read", client => { client.BaseAddress = coreUri; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", coreRead); client.Timeout = TimeSpan.FromSeconds(20); });
    builder.Services.AddHttpClient("core-write", client => { client.BaseAddress = coreUri; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", coreWrite); client.Timeout = TimeSpan.FromSeconds(30); });
    builder.Services.AddHttpClient("providers", client => client.Timeout = TimeSpan.FromSeconds(25));
    builder.Services.AddSingleton<ICoreReader>(services => new CoreReader(services.GetRequiredService<IHttpClientFactory>().CreateClient("core-read")));
    builder.Services.AddSingleton<ICoreCommitter>(services => new CoreCommitter(services.GetRequiredService<IHttpClientFactory>().CreateClient("core-write")));
    builder.Services.AddSingleton<IGenerativeProvider>(services => new OpenAiProvider(services.GetRequiredService<IHttpClientFactory>().CreateClient("providers"), openAiKey, model, strongerModel));
    builder.Services.AddSingleton<IJevProvider>(services => new JevProvider(services.GetRequiredService<IHttpClientFactory>().CreateClient("providers"), jevKey, builder.Configuration["KnowledgeService:JevModel"] ?? "jev-1.13.0"));
    builder.Services.AddSingleton<Workflow>();
    builder.Services.AddHostedService<CaptureWorker>();
}
var app = builder.Build();
app.MapGet("/alive", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health", async (CancellationToken ct) =>
{
    if (!enabled) return Results.Json(new { status = "disabled" }, statusCode: 503);
    try
    {
        await app.Services.GetRequiredService<ISecretFilter>().SanitizeAsync("readiness", ct);
        await app.Services.GetRequiredService<ICoreReader>().StateAsync(ct);
        return Results.Ok(new { status = "ready", workflowVersion = Workflow.Version });
    }
    catch { return Results.Json(new { status = "unavailable" }, statusCode: 503); }
});
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/knowledge")) { await next(); return; }
    if (!enabled) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { errorClass = "service_disabled" }); return; }
    bool write = context.Request.Path.StartsWithSegments("/api/knowledge/captures") && context.Request.Method != "GET";
    string provided = context.Request.Headers.Authorization.ToString();
    string? expected = app.Configuration[write ? "KnowledgeService:WriteToken" : "KnowledgeService:ReadToken"];
    bool matches = expected is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + expected)));
    if (!write && !matches && app.Configuration["KnowledgeService:WriteToken"] is { } writeToken) matches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + writeToken)));
    if (!matches) { context.Response.StatusCode = 401; return; }
    try { await next(); }
    catch (KnowledgeException error)
    {
        context.Response.StatusCode = error.Code is "idempotency_conflict" or "capture_busy" or "reconciliation_required" or "reconciliation_epoch_mismatch" or "restore_not_detected" ? 409 : error.Code == "journal_full" ? 507 : error.Code == "secret_detector_failed" ? 503 : 400;
        await context.Response.WriteAsJsonAsync(new { errorClass = error.Code });
    }
});
if (enabled)
{
    app.MapPost("/api/knowledge/context", (ContextRequest request, Workflow workflow, CancellationToken ct) => workflow.ContextAsync(request, ct));
    app.MapPost("/api/knowledge/captures", async (CaptureRequest request, Workflow workflow, CancellationToken ct) => { var receipt = await workflow.AcceptAsync(request, ct); return Results.Accepted("/api/knowledge/captures/" + receipt.Id, receipt); });
    app.MapGet("/api/knowledge/captures/{id:guid}", async (Guid id, Workflow workflow, CancellationToken ct) => await workflow.GetAsync(id, ct) is { } receipt ? Results.Ok(receipt) : Results.NotFound());
    app.MapPost("/api/knowledge/captures/{id:guid}/clarifications", async (Guid id, ClarificationRequest request, Workflow workflow, CancellationToken ct) => Results.Accepted("/api/knowledge/captures/" + id, await workflow.ClarifyAsync(id, request, ct)));
    app.MapPost("/api/knowledge/captures/{id:guid}/reconcile", async (Guid id, ReconciliationRequest request, Workflow workflow, CancellationToken ct) => Results.Accepted("/api/knowledge/captures/" + id, await workflow.ReconcileAsync(id, request, ct)));
}
await app.RunAsync();
public partial class Program;
