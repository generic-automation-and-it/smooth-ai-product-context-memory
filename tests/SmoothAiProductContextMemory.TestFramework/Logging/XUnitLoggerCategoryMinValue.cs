using Microsoft.Extensions.Logging;

namespace SmoothAiProductContextMemory.TestFramework.Logging;

public sealed record XUnitLoggerCategoryMinValue(string CategoryPrefix, LogLevel MinLevel);
