using Microsoft.Extensions.Configuration;
using SmoothAiProductContextMemory.AppHost;

namespace SmoothAiProductContextMemory.AppHost.UnitTest;

public class HostLaunchModeTests
{
    [Fact]
    public void ResolveUseProject_defaults_to_working_tree()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        HostLaunchMode.ResolveUseProject(configuration).ShouldBeTrue();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ResolveUseProject_honours_explicit_flag(string value, bool expected)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostConfiguration:UseProject"] = value,
            })
            .Build();

        HostLaunchMode.ResolveUseProject(configuration).ShouldBe(expected);
    }

    [Fact]
    public void Announcement_for_working_tree_cannot_be_read_as_image_mode()
    {
        string announcement = HostLaunchMode.FormatAnnouncement(useProject: true, hostImage: "ignored");

        announcement.ShouldContain("working tree");
        announcement.ShouldNotContain("published image");
    }

    [Fact]
    public void Announcement_for_published_image_names_the_tag_and_warns_it_may_lag()
    {
        const string image = "ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory:latest";

        string announcement = HostLaunchMode.FormatAnnouncement(useProject: false, hostImage: image);

        announcement.ShouldContain("published image");
        announcement.ShouldContain(image);
        announcement.ShouldContain("may lag");
    }
}
