// ReSharper disable once CheckNamespace

using Zitadel.Management.V1;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// </summary>
/// <param name="name"></param>
/// <param name="projectRequest"></param>
/// <param name="parent"></param>
public class ZitadelProjectResource(string name, AddProjectRequest projectRequest, ZitadelResource parent)
    : Resource(name), IResourceWithParent<ZitadelResource>, IResourceWithWaitSupport
{
    /// <summary>
    /// </summary>
    public AddProjectRequest ProjectRequest { get; } = projectRequest;

    /// <summary>
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// </summary>
    public ZitadelResource Parent { get; } = parent;
}