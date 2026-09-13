using Npgsql;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest;

public class NpgsqlDataSourceFactoryTests
{
    [Fact]
    public void Create_DisablesResetOnClose()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSourceFactory.Create(
            "Host=localhost;Database=throwaway;Username=x;Password=y");

        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        builder.NoResetOnClose.ShouldBeTrue();
    }
}
