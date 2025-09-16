// ReSharper disable once CheckNamespace
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Zitadel identity and access management container resource for Aspire applications.
/// </summary>
/// <param name="name">The unique name of the Zitadel resource.</param>
/// <param name="admin">Optional admin username parameter resource.</param>
/// <param name="adminPassword">Admin password parameter resource.</param>
/// <param name="masterKey">Master encryption key parameter resource.</param>
/// <param name="primaryEndpointName">Primary endpoint name for HTTP/HTTPS communication.</param>
public class ZitadelResource(string name, ParameterResource? admin, ParameterResource adminPassword, ParameterResource masterKey, string primaryEndpointName)
    : ContainerResource(name), IResourceWithServiceDiscovery
{
    private const string DefaultAdmin = "root";

    private readonly Dictionary<string, string> _projects = new(StringComparer.InvariantCultureIgnoreCase);

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Gets the primary endpoint for the Zitadel instance.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new EndpointReference(this, primaryEndpointName);

    /// <summary>
    /// Gets the admin username parameter resource.
    /// </summary>
    public ParameterResource? AdminUserNameParameter { get; } = admin;

    /// <summary>
    /// Gets a reference expression for the admin username.
    /// </summary>
    internal ReferenceExpression AdminReference =>
        AdminUserNameParameter is not null ? ReferenceExpression.Create($"{AdminUserNameParameter}") : ReferenceExpression.Create($"{DefaultAdmin}");

    /// <summary>
    /// Gets the admin password parameter resource.
    /// </summary>
    public ParameterResource AdminPasswordParameter { get; } = adminPassword ?? throw new ArgumentNullException(nameof(adminPassword));

    /// <summary>
    /// Gets the master encryption key parameter resource.
    /// </summary>
    public ParameterResource MasterKeyParameter { get; } = masterKey ?? throw new ArgumentNullException(nameof(masterKey));

    /// <summary>
    /// Gets or sets the file system path for machine user key files.
    /// </summary>
    public string? MachineUserKeyPath { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public ParameterResource? MachineUserKeyParameter { get; internal set; }

    /// <summary>
    /// Gets the projects associated with this Zitadel instance.
    /// </summary>
    public IReadOnlyDictionary<string, string> Projects => _projects;

    /// <summary>
    /// Gets or sets the organization name for the Zitadel instance.
    /// </summary>
    public string OrganizationName { get; set; } = "ZITADEL";

    internal void AddProject(string name, string projectName)
    {
        _projects.TryAdd(name, projectName);
    }
}