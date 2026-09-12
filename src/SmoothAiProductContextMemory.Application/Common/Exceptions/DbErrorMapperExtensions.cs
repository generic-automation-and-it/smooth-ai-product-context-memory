using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Common.Exceptions;

public static class DbErrorMapperExtensions
{
    /// <summary>
    /// Runs a persist action, translating recognised constraint and trigger violations. Anything the
    /// mapper does not recognise propagates untouched — the filter returns false, so the original
    /// exception keeps its stack trace instead of being rethrown from here.
    /// </summary>
    public static async Task SaveOrMapAsync(this IDbErrorMapper mapper, Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (Exception ex) when (mapper.TryMap(ex, out Exception mapped))
        {
            throw mapped;
        }
    }

    public static async Task<T> SaveOrMapAsync<T>(this IDbErrorMapper mapper, Func<Task<T>> save)
    {
        try
        {
            return await save();
        }
        catch (Exception ex) when (mapper.TryMap(ex, out Exception mapped))
        {
            throw mapped;
        }
    }
}
