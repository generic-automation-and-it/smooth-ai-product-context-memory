using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.UnitTest;

/// <summary>
/// L0 structural guard: the application-facing blob abstraction must not carry a deletion (or any
/// other destructive) capability. Content addresses are shared by identical bytes, so nothing in the
/// normal write/read path can prove an object is unreferenced across all versions — deletion requires
/// a separately approved garbage-collection design informed by HLD-006 orphan accounting. A delete
/// member reappearing here is that boundary being crossed, not an oversight to accommodate.
/// </summary>
public sealed class BlobStorageCapabilityGuardTests
{
    [Fact]
    public void IBlobStorage_ExposesNoDestructiveCapability()
    {
        string[] members = [.. typeof(IBlobStorage).GetMethods().Select(m => m.Name).Order()];

        members.ShouldBe(["ExistsAsync", "GetAsync", "StoreAsync"]);
    }
}
