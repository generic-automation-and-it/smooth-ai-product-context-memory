namespace SmoothAiProductContextMemory.Application.Common.Exceptions;

/// <summary>
/// The record exists but is not readable under the caller's declared scope. Distinct from
/// <see cref="NotFoundException"/> on purpose: the caller holds a valid uuid and needs to know the
/// scope rule blocked it, not that the memory is missing.
/// </summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message)
    {
    }
}
