// ReSharper disable once CheckNamespace

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// </summary>
/// <param name="name"></param>
/// <param name="admin"></param>
/// <param name="adminPassword"></param>
/// <param name="masterKey"></param>
/// <param name="primaryEndpointName"></param>
public class ZitadelResource(string name, ParameterResource? admin, ParameterResource adminPassword, ParameterResource masterKey, string primaryEndpointName)
    : ContainerResource(name), IResourceWithServiceDiscovery
{
    private const string DefaultAdmin = "root";

    private readonly Dictionary<string, string> _projects = new(StringComparer.InvariantCultureIgnoreCase);

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    ///     Gets the primary endpoint for the Grafana k6 instance.
    ///     This endpoint is used for all API calls over HTTP.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new EndpointReference(this, primaryEndpointName);

    /// <summary>
    /// </summary>
    public ParameterResource? AdminUserNameParameter { get; } = admin;

    internal ReferenceExpression AdminReference =>
        AdminUserNameParameter is not null ? ReferenceExpression.Create($"{AdminUserNameParameter}") : ReferenceExpression.Create($"{DefaultAdmin}");

    /// <summary>
    /// </summary>
    public ParameterResource AdminPasswordParameter { get; } = adminPassword ?? throw new ArgumentNullException(nameof(adminPassword));

    /// <summary>
    /// </summary>
    public ParameterResource MasterKeyParameter { get; } = masterKey ?? throw new ArgumentNullException(nameof(masterKey));

    /// <summary>
    /// </summary>
    public string? MachineUserKeyPath { get; set; }

    /// <summary>
    ///     A dictionary where the key is the resource name and the value is the database name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Projects => _projects;

    /// <summary>
    ///     Organization name used for the ZITADEL instance.
    /// </summary>
    public string OrganizationName { get; set; } = "ZITADEL";

    internal void AddProject(string name, string projectName)
    {
        _projects.TryAdd(name, projectName);
    }
}