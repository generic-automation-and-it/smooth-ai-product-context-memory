using Microsoft.EntityFrameworkCore;

namespace SmoothAiProductContextMemory.Application.Common.Exceptions;

public static class DbExceptionMapping
{
    public static Exception Map(DbUpdateException exception)
    {
        string text = exception.InnerException?.Message ?? exception.Message;

        if (text.Contains("Append-only history", StringComparison.Ordinal))
        {
            return new ConflictException("History is append-only.");
        }

        if (text.Contains("23505", StringComparison.Ordinal)
            || text.Contains("unique", StringComparison.OrdinalIgnoreCase))
        {
            return new ConflictException("A unique constraint was violated.");
        }

        if (text.Contains("23503", StringComparison.Ordinal))
        {
            return new ConflictException("A referenced record was not found.");
        }

        return exception;
    }

    public static async Task<T> SaveOrMapAsync<T>(Func<Task<T>> save)
    {
        try
        {
            return await save();
        }
        catch (DbUpdateException ex)
        {
            throw Map(ex);
        }
    }

    public static async Task SaveOrMapAsync(Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (DbUpdateException ex)
        {
            throw Map(ex);
        }
    }
}
