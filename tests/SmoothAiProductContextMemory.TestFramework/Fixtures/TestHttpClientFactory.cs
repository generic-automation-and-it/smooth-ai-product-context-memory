namespace SmoothAiProductContextMemory.TestFramework.Fixtures;

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> for component tests that construct an adapter directly
/// instead of resolving it from a container. Production wiring goes through the real factory so the
/// Host's <c>ConfigureHttpClientDefaults</c> applies; these tests exercise the adapter, not the
/// handler pipeline.
/// </summary>
public sealed class TestHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly List<HttpClient> _clients = [];

    public HttpClient CreateClient(string name)
    {
        HttpClient client = new();
        _clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}
