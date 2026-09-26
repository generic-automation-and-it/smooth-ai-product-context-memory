using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace SmoothAiProductContextMemory.AppHost;

internal enum AppHostMode
{
    Development,
    Release,
}

internal sealed partial record AppHostConfiguration(
    AppHostMode Mode,
    string InstallationId,
    string PostgresPassword,
    int PostgresPort,
    string BlobAccessKey,
    string BlobSecretKey,
    int BlobPort,
    int BlobConsolePort,
    int SeqPort,
    int HostPort,
    bool UseProject,
    string HostImage,
    string ReleaseVersion,
    string EngineKind,
    string EngineHostAddress)
{
    internal string EngineBindAddress { get; init; } = "127.0.0.1";
    internal const string OwnershipLabel = "io.smooth-mimisbrunnr.installation";
    internal const string ManagedLabel = "io.smooth-mimisbrunnr.managed";
    private const string DefaultHostImage = "ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory:latest";
    private const string DevelopmentPassword = "LocalMachineAccessNoInterestingDataDev#Passw0rd!FirewallNotExposed";

    internal bool IsRelease => Mode == AppHostMode.Release;
    internal string DockerDesktopGroupName => IsRelease
        ? $"smooth-mímisbrunnr-release-{InstallationId}"
        : "smooth-mímisbrunnr";
    internal string PostgresContainerName => ResourceName("postgres");
    internal string BlobContainerName => ResourceName("blob-well");
    internal string SeqContainerName => ResourceName("seq");
    internal string HostContainerName => ResourceName("host");
    internal string PostgresDataVolume => VolumeName("postgres-data");
    internal string BlobDataVolume => VolumeName("blob-well-data");
    internal string SeqDataVolume => VolumeName("seq-data");
    internal string HostContextVolume => VolumeName("host-context");

    internal static AppHostConfiguration Create(IConfiguration configuration)
    {
        AppHostMode mode = ResolveMode(configuration);
        bool isRelease = mode == AppHostMode.Release;
        bool useProject = HostLaunchMode.ResolveUseProject(configuration);

        if (isRelease && useProject)
        {
            throw new InvalidOperationException("Release mode requires HostConfiguration:UseProject=false.");
        }

        string hostImage = GetValue(configuration, "HostConfiguration:Image", isRelease, DefaultHostImage);
        if (isRelease && !ContainerImageReference.IsDigestPinned(hostImage))
        {
            throw new InvalidOperationException("Release mode requires HostConfiguration:Image to use an immutable sha256 digest.");
        }

        string installationId = configuration["InstallationConfiguration:Id"] ?? "default";
        if (!InstallationIdPattern().IsMatch(installationId))
        {
            throw new InvalidOperationException("InstallationConfiguration:Id must start with a lowercase letter or digit and contain only lowercase letters, digits, and hyphens (maximum 32 characters).");
        }

        string engineKind = configuration["EngineConfiguration:Kind"] ?? "docker";
        if (engineKind is not ("docker" or "podman"))
        {
            throw new InvalidOperationException("EngineConfiguration:Kind must be 'docker' or 'podman'.");
        }

        string engineHostAddress = GetEngineHostAddress(configuration, engineKind);
        string bindAddress = configuration["EngineConfiguration:BindAddress"] ?? (isRelease ? "" : "127.0.0.1");
        if (isRelease && (!System.Net.IPAddress.TryParse(bindAddress, out var address) ||
            address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any)))
        {
            throw new InvalidOperationException("Release mode requires EngineConfiguration:BindAddress to be an explicit engine interface IP reachable from the controller. Wildcard binds are not allowed.");
        }

        var result = new AppHostConfiguration(
            mode,
            installationId,
            GetValue(configuration, "PostgresConfiguration:Password", isRelease, DevelopmentPassword),
            GetPort(configuration, "PostgresConfiguration:Port", 5432),
            GetValue(configuration, "BlobConfiguration:AccessKey", isRelease, "smooth-local"),
            GetValue(configuration, "BlobConfiguration:SecretKey", isRelease, DevelopmentPassword),
            GetPort(configuration, "BlobConfiguration:Port", 9000),
            GetPort(configuration, "BlobConfiguration:ConsolePort", 9001),
            GetPort(configuration, "SeqConfiguration:Port", 5341),
            GetPort(configuration, "HostConfiguration:Port", 5141),
            useProject,
            hostImage,
            configuration["ReleaseConfiguration:Version"] ?? "development",
            engineKind,
            engineHostAddress)
        {
            EngineBindAddress = bindAddress,
        };

        if (isRelease && new[] { result.PostgresPort, result.BlobPort, result.BlobConsolePort, result.SeqPort, result.HostPort }.Distinct().Count() != 5)
        {
            throw new InvalidOperationException("Release workload ports must be distinct.");
        }

        return result;
    }

    private string ResourceName(string resource) => IsRelease
        ? $"mimisbrunnr-{InstallationId}-{resource}"
        : $"mimisbrunnr-{resource}";

    private string VolumeName(string resource) => IsRelease
        ? $"mimisbrunnr-{InstallationId}-{resource}"
        : $"mimisbrunnr-{resource}";

    private static AppHostMode ResolveMode(IConfiguration configuration)
    {
        string? value = configuration["AppHostConfiguration:Mode"];
        if (value is null || string.Equals(value, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return AppHostMode.Development;
        }

        return string.Equals(value, "Release", StringComparison.OrdinalIgnoreCase)
            ? AppHostMode.Release
            : throw new InvalidOperationException("AppHostConfiguration:Mode must be Development or Release.");
    }

    private static string GetValue(
        IConfiguration configuration,
        string key,
        bool required,
        string developmentDefault)
    {
        string? value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (required)
        {
            throw new InvalidOperationException($"Release mode requires {key}.");
        }

        return developmentDefault;
    }

    private static int GetPort(IConfiguration configuration, string key, int defaultValue)
    {
        string? configuredValue = configuration[key];
        if (configuredValue is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(configuredValue, out int value) || value is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{key} must be between 1 and 65535.");
        }

        return value;
    }

    private static string GetEngineHostAddress(IConfiguration configuration, string engineKind)
    {
        string value = configuration["EngineConfiguration:HostAddress"]
            ?? (engineKind == "podman" ? "host.containers.internal" : "host.docker.internal");

        if (Uri.CheckHostName(value) == UriHostNameType.Unknown)
        {
            throw new InvalidOperationException("EngineConfiguration:HostAddress must be a host name or IP address without a URI scheme.");
        }

        return value;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallationIdPattern();
}
