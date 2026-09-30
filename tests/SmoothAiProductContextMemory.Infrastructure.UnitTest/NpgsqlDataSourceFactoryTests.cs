using Npgsql;
using Shouldly;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest;

public class NpgsqlDataSourceFactoryTests
{
    private const string ConnectionString = "Host=localhost;Database=throwaway;Username=x;Password=y";

    [Fact]
    public void Create_DisablesResetOnClose()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSourceFactory.Create(ConnectionString);

        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        builder.NoResetOnClose.ShouldBeTrue();
    }

    [Fact]
    public void Create_AppliesTheRequestTrafficCommandTimeoutByDefault()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSourceFactory.Create(ConnectionString);

        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString)
            .CommandTimeout.ShouldBe(120);
    }

    [Fact]
    public void Create_LetsABulkOperationRaiseTheClientSideCommandTimeout()
    {
        // The client abandons a statement at its own cap whatever the server's statement_timeout
        // says, so a bulk operation that raised only the server side was still capped at 120 s and
        // the knob did nothing above that. Asserted on the built data source's connection string
        // because that value is what every command on the connection inherits — EF batches and raw
        // ones alike — and a behavioural test cannot separate the two without a sleep longer than
        // the default it is trying to exceed.
        using NpgsqlDataSource dataSource = NpgsqlDataSourceFactory.Create(ConnectionString, 900);

        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString)
            .CommandTimeout.ShouldBe(900);
    }
}
