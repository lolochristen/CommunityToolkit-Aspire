namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// </summary>
/// <param name="name"></param>
/// <param name="accessToken"></param>
public class ZitadelLoginClientResource(string name, ParameterResource? accessToken)
    : ContainerResource(name), IResourceWithServiceDiscovery
{
    internal const string BasePath = "/ui/v2/login";

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    ///     Gets the primary endpoint for the Zitadel Login instance.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new EndpointReference(this, "http");

    /// <summary>
    ///     Gets the Zitadel base endpoint reference.
    /// </summary>
    public ReferenceExpression BaseEndpoint => ReferenceExpression.Create($"{PrimaryEndpoint}{BasePath}"); // ? external address

    /// <summary>
    ///     Gets the OpenID Connect login endpoint reference.
    /// </summary>
    public ReferenceExpression OidcLoginEndpoint => ReferenceExpression.Create($"{BaseEndpoint}/login?authRequest=");

    /// <summary>
    ///     Gets the OpenID Connect logout endpoint reference.
    /// </summary>
    public ReferenceExpression OidcLogoutEndpoint => ReferenceExpression.Create($"{BaseEndpoint}/logout?post_logout_redirect=");

    /// <summary>
    ///     Gets the SAML login endpoint reference.
    /// </summary>
    public ReferenceExpression SamlLoginEndpoint => ReferenceExpression.Create($"{BaseEndpoint}/login?samlRequest=");

    /// <summary>
    ///     PAT for the service user to access the Zitadel Management API.
    /// </summary>
    public ParameterResource? ServiceAccessToken { get; } = accessToken;
}