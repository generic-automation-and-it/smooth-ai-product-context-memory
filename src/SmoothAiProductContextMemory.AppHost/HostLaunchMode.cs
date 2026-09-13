using Microsoft.Extensions.Configuration;

namespace SmoothAiProductContextMemory.AppHost;

internal static class HostLaunchMode
{
    internal const bool DefaultUseProject = true;
    internal const string WorkingTreeResourceName = "host-working-tree";
    internal const string PublishedImageResourceName = "host-published-image";

    internal static bool ResolveUseProject(IConfiguration configuration) =>
        configuration.GetValue("HostConfiguration:UseProject", DefaultUseProject);

    internal static string FormatAnnouncement(bool useProject, string hostImage) =>
        useProject
            ? "Host mode: working tree (source). Image path is opt-in: HostConfiguration:UseProject=false."
            : $"Host mode: published image {hostImage}. Tag may lag the working tree.";
}
