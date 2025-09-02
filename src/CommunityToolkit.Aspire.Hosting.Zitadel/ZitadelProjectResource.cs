// ReSharper disable once CheckNamespace

using Zitadel.Management.V1;

// ReSharper disable once CheckNamespace
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a Zitadel project resource that organizes applications and configurations within a Zitadel instance.
/// </summary>
/// <param name="name">The unique name of the project resource.</param>
/// <param name="projectRequest">The project creation request containing configuration details.</param>
/// <param name="parent">The parent Zitadel resource that this project belongs to.</param>
public class ZitadelProjectResource(string name, AddProjectRequest projectRequest, ZitadelResource parent)
    : Resource(name), IResourceWithParent<ZitadelResource>, IResourceWithWaitSupport
{
    /// <summary>
    /// Gets the project creation request used to configure this project in Zitadel.
    /// </summary>
    public AddProjectRequest ProjectRequest { get; } = projectRequest;

    /// <summary>
    /// Gets or sets the unique project ID assigned by Zitadel after project creation.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets the parent Zitadel resource that this project belongs to.
    /// </summary>
    public ZitadelResource Parent { get; } = parent;
}