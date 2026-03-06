using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.Zitadel;

/// <summary>
/// Resource for the Zitadel API server.
/// </summary>
public sealed class ZitadelResource(string name) : ContainerResource(name)
{
    internal const string HttpEndpointName = "http";

    /// <summary>
    /// The parameter that contains the (default) Zitadel admin username.
    /// </summary>
    public required ParameterResource AdminUsernameParameter { get; set; }

    /// <summary>
    /// The parameter that contains the (default) Zitadel admin password.
    /// </summary>
    public required ParameterResource AdminPasswordParameter { get; set; }

    /// <summary>
    /// The parameter that contains the machine service account name.
    /// </summary>
    public ParameterResource? MachineServiceAccountNameParameter { get; set; }

    /// <summary>
    /// The parameter that contains the machine service account key.
    /// </summary>
    public ParameterResource? MachineServiceAccountKeyParameter { get; set; }

    /// <summary>
    /// The parameter that contains the login service account name.
    /// </summary>
    public ParameterResource? LoginServiceAccountNameParameter { get; set; }

    /// <summary>
    /// The parameter that contains the login service account access token.
    /// </summary>
    public ParameterResource? LoginServiceAccountTokenParameter { get; set; }

}