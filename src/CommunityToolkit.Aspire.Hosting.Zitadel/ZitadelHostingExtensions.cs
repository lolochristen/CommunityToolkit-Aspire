using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.Zitadel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Xml.Linq;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Zitadel to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class ZitadelHostingExtensions
{
    /// <summary>
    /// Adds a Zitadel container resource to the <see cref="IDistributedApplicationBuilder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder" /> to add the Zitadel container to.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port used when launching the container. If <c>null</c> a random port will be assigned</param>
    /// <param name="username">An optional parameter to set a username for the admin account, if <c>null</c> will auto generate one.</param>
    /// <param name="password">An optional parameter to set a password for the admin account, if <c>null</c> will auto generate one.</param>
    /// <param name="masterKey">An optional parameter to set the masterkey, if <c>null</c> will auto generate one.</param>
    public static IResourceBuilder<ZitadelResource> AddZitadel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? username = null,
        IResourceBuilder<ParameterResource>? password = null,
        IResourceBuilder<ParameterResource>? masterKey = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var usernameParameter = username?.Resource ?? new ParameterResource($"{name}-username", _ => "admin", false);
        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", minSpecial: 1);
        var masterKeyParameter = masterKey?.Resource ?? ParameterResourceBuilderExtensions.CreateGeneratedParameter(builder, $"{name}-masterKey", true, new GenerateParameterDefault
        {
            MinLength = 32, // Zitadel requires 32, CreateDefaultPasswordParameter generates 22
            Lower = true,
            Upper = true,
            Numeric = true,
            Special = true,
            MinLower = 1,
            MinUpper = 1,
            MinNumeric = 1,
            MinSpecial = 1
        });

        var resource = new ZitadelResource(name)
        {
            AdminUsernameParameter = usernameParameter,
            AdminPasswordParameter = passwordParameter
        };

        var zitadelBuilder = builder.AddResource(resource)
            .WithImage(ZitadelContainerImageTags.Image)
            .WithImageTag(ZitadelContainerImageTags.Tag)
            .WithImageRegistry(ZitadelContainerImageTags.Registry)
            .WithArgs("start-from-init", "--masterkeyFromEnv")
            .WithHttpEndpoint(
                targetPort: 8080,
                port: port,
                name: ZitadelResource.HttpEndpointName
            )
            .WithHttpHealthCheck("/healthz")
            .WithEnvironment("ZITADEL_MASTERKEY", masterKeyParameter)
            .WithUrlForEndpoint(ZitadelResource.HttpEndpointName, e => e.DisplayText = "Zitadel Dashboard");

#pragma warning disable ASPIRECERTIFICATES001
        zitadelBuilder.WithHttpsCertificateConfiguration(ctx =>
        {
            ctx.EnvironmentVariables["ZITADEL_EXTERNALSECURE"] = "true";
            ctx.EnvironmentVariables["ZITADEL_TLS_ENABLED"] = "true";
            ctx.EnvironmentVariables["ZITADEL_TLS_CERTPATH"] = ctx.CertificatePath;
            ctx.EnvironmentVariables["ZITADEL_TLS_KEYPATH"] = ctx.KeyPath;
            return Task.CompletedTask;
        });
#pragma warning restore ASPIRECERTIFICATES001

        if (builder.ExecutionContext.IsRunMode)
        {
#pragma warning disable ASPIRECERTIFICATES001
            builder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
            {
                var developerCertificateService = @event.Services.GetRequiredService<IDeveloperCertificateService>();

                bool addHttps = false;
                if (!zitadelBuilder.Resource.TryGetLastAnnotation<HttpsCertificateAnnotation>(out var annotation))
                {
                    if (developerCertificateService.UseForHttps)
                    {
                        // If no certificate is configured, and the developer certificate service supports container trust,
                        // configure the resource to use the developer certificate for its key pair.
                        addHttps = true;
                    }
                }
                else if (annotation.UseDeveloperCertificate.GetValueOrDefault(developerCertificateService.UseForHttps) || annotation.Certificate is not null)
                {
                    addHttps = true;
                }

                if (addHttps)
                {
                    // If a TLS certificate is configured, override the endpoint to use HTTPS instead of HTTP
                    // Zitadel only binds to a single port
                    zitadelBuilder
                        .WithEndpoint(ZitadelResource.HttpEndpointName, ep => ep.UriScheme = "https");
                }

                return Task.CompletedTask;
            });
#pragma warning restore ASPIRECERTIFICATES001
            zitadelBuilder.OnResourceReady(async (zitadelResource, @event, cancellationToken) =>
            {
                // store keys
#pragma warning disable ASPIREUSERSECRETS001
                if (zitadelResource.ServiceAccountKeyParameter != null && zitadelResource.ServiceAccountNameParameter != null)
                {
                    var serviceAccountName = await zitadelResource.ServiceAccountNameParameter.GetValueAsync(cancellationToken);
                    var keyPath = Path.Combine(GetLocalKeysPath(zitadelBuilder.Resource.Name), $"{serviceAccountName}.json");
                    if (File.Exists(keyPath))
                    {
                        var secretName = $"Parameters:{zitadelResource.ServiceAccountKeyParameter.Name}";
                        zitadelBuilder.ApplicationBuilder.UserSecretsManager.TrySetSecret(secretName, await File.ReadAllTextAsync(keyPath, cancellationToken));
                    }
                }

                if (zitadelResource.LoginClientAccessTokenParameter != null && zitadelResource.LoginClientUsernameParameter != null)
                {
                    var serviceAccountName = await zitadelResource.LoginClientUsernameParameter.GetValueAsync(cancellationToken);
                    var keyPath = Path.Combine(GetLocalKeysPath(zitadelBuilder.Resource.Name), $"{serviceAccountName}.pat");
                    if (File.Exists(keyPath))
                    {
                        var secretName = $"Parameters:{zitadelResource.LoginClientAccessTokenParameter.Name}";
                        var pat = await File.ReadAllTextAsync(keyPath, cancellationToken);
                        zitadelBuilder.ApplicationBuilder.UserSecretsManager.TrySetSecret(secretName, pat.Trim(' ', '\n'));
                    }
                }
#pragma warning restore ASPIREUSERSECRETS001
            });
        }

        return zitadelBuilder
            .WithEnvironment(ctx =>
            {
                var resource = ctx.Resource;
                if (resource.TryGetEndpoints(out var endpoints))
                {
                    var endpoint = endpoints.Single(e => e.Name == ZitadelResource.HttpEndpointName);
                    ctx.EnvironmentVariables.TryAdd("ZITADEL_EXTERNALDOMAIN", endpoint.TargetHost);
                    var port = endpoint.Port;
                    if (port is not null)
                    {
                        ctx.EnvironmentVariables.TryAdd("ZITADEL_EXTERNALPORT", port);
                    }
                }
            })
            // Disable Login V2 for simpler setup (no separate login container needed)
            .WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "false")
            // Configure admin user
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_HUMAN_USERNAME", usernameParameter)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_HUMAN_PASSWORD", passwordParameter)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_HUMAN_PASSWORDCHANGEREQUIRED", "false");
    }

    /// <summary>
    /// Adds database support to the Zitadel resource.
    /// </summary>
    /// <param name="builder">The Zitadel resource to add database support to.</param>
    /// <param name="server">The Postgres server resource to use for the database.</param>
    /// <param name="databaseName">An optional name for the database Zitadel will use, if left empty will default to <c>"zitadel-db"</c>.</param>
    public static IResourceBuilder<ZitadelResource> WithDatabase(
        this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<PostgresServerResource> server,
        [ResourceName] string? databaseName = null
    )
    {
        databaseName = string.IsNullOrWhiteSpace(databaseName) ? "zitadel-db" : databaseName;
        var database = server.AddDatabase(databaseName);

        return WithDatabase(builder, database);
    }

    /// <summary>
    /// Adds database support to the Zitadel resource.
    /// </summary>
    /// <param name="builder">The Zitadel resource to add database support to.</param>
    /// <param name="database">The Postgres database resource to use for the database.</param>
    public static IResourceBuilder<ZitadelResource> WithDatabase(this IResourceBuilder<ZitadelResource> builder, IResourceBuilder<PostgresDatabaseResource> database)
    {
        ArgumentNullException.ThrowIfNull(database);

        builder
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_USER_USERNAME", database.Resource.Parent.UserNameReference)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_USER_PASSWORD", database.Resource.Parent.PasswordParameter)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_ADMIN_USERNAME", database.Resource.Parent.UserNameReference)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_ADMIN_PASSWORD", database.Resource.Parent.PasswordParameter)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_HOST", database.Resource.Parent.Host)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_PORT", database.Resource.Parent.Port)
            .WithEnvironment("ZITADEL_DATABASE_POSTGRES_DATABASE", database.Resource.DatabaseName)
            .WithReference(database)
            .WaitFor(database);

        return builder;
    }

    /// <summary>
    /// Configures the external domain for the Zitadel resource. This overrides the default domain set in <see cref="AddZitadel"/>.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder.</param>
    /// <param name="externalDomain">The external domain to use (e.g., "auth.example.com"). Cannot be null or empty.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="externalDomain"/> is null or whitespace.</exception>
    public static IResourceBuilder<ZitadelResource> WithExternalDomain(
        this IResourceBuilder<ZitadelResource> builder,
        string externalDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalDomain);

        return builder.WithEnvironment("ZITADEL_EXTERNALDOMAIN", externalDomain);
    }

    /// <summary>
    /// Configures a service account user for programmatic access to the Zitadel instance with API key authentication.
    /// Creates a service account that can be used for server-to-server communication and API automation.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with machine user settings.</param>
    /// <param name="serviceAccountName">The machine user name for the service account. Default is "admin".</param>
    /// <param name="serviceAccountKey"></param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithServiceAccount(this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<ParameterResource>? serviceAccountName = null,
        IResourceBuilder<ParameterResource>? serviceAccountKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string path = GetLocalKeysPath(builder.Resource.Name);
        var serviceAccountNameParameter = serviceAccountName?.Resource ?? 
                                          new ParameterResource($"{builder.Resource.Name}-service-account", _ => "admin", false);

        builder.ApplicationBuilder.CreateResourceBuilder<ParameterResource>(serviceAccountNameParameter);

        var keyParamName = $"{builder.Resource.Name}-service-account-key";
        var serviceAccountKeyParameter = serviceAccountKey?.Resource ??
                                         new ParameterResource(keyParamName,
                                             _ =>
                                             {
                                                 var value = builder.ApplicationBuilder.Configuration[$"Parameters:{keyParamName}"];

                                                 if (!string.IsNullOrWhiteSpace(value)) return value;

                                                 var filePath = Path.Combine(path, $"{serviceAccountNameParameter.GetValueAsync(CancellationToken.None).Result}.json");
                                                 if (File.Exists(filePath))
                                                 {
                                                     return File.ReadAllText(filePath);
                                                 }

                                                 return string.Empty;
                                             }, true);

        builder.Resource.ServiceAccountKeyParameter = serviceAccountKeyParameter;
        builder.Resource.ServiceAccountNameParameter = serviceAccountNameParameter;

        builder
            .WithKeysMount()
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_MACHINEKEYPATH", $"/keys/{serviceAccountNameParameter}.json")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_USERNAME", serviceAccountNameParameter)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINE_NAME", "Automatically Initialized IAM_OWNER")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_MACHINE_MACHINEKEY_TYPE", "1");

        return builder;
    }

    

    /// <summary>
    /// Mounts the keys directory of Zitadel to a local directory.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure.</param>
    /// <param name="source">Source directory to mount</param>
    /// <returns></returns>
    public static IResourceBuilder<ZitadelResource> WithKeysMount(this IResourceBuilder<ZitadelResource> builder, string? source = null)
    {
        source ??= GetLocalKeysPath(builder.Resource.Name);

        builder.Resource.TryGetAnnotationsOfType<ContainerMountAnnotation>(out IEnumerable<ContainerMountAnnotation>? mountAnnotations);
        if (mountAnnotations == null || mountAnnotations.All(m => m.Target != "/keys"))
        {
            builder.WithBindMount(source, "/keys");
        }

        return builder;
    }

    /// <summary>
    /// Configures a login client with personal access token for custom login UI integration.
    /// Creates the necessary configuration for implementing a custom login interface that integrates with Zitadel's authentication flows.
    /// </summary>
    /// <param name="builder">The Zitadel resource builder to configure with login client settings.</param>
    /// <param name="loginClientAccessToken"></param>
    /// <param name="expirationDate">Optional expiration date. Default today + 5y</param>
    /// <param name="loginClientUsername"></param>
    /// <returns>The Zitadel resource builder for method chaining.</returns>
    public static IResourceBuilder<ZitadelResource> WithLoginClientUser(this IResourceBuilder<ZitadelResource> builder,
        IResourceBuilder<ParameterResource>? loginClientUsername = null,
        IResourceBuilder<ParameterResource>? loginClientAccessToken = null,
        DateTimeOffset? expirationDate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string path = GetLocalKeysPath(builder.Resource.Name);

        var loginClientUsernameParameter = loginClientUsername?.Resource ??
                                          new ParameterResource($"{builder.Resource.Name}-login-client", _ => "login-client", false);

        var tokenParamName = $"{builder.Resource.Name}-login-client-token";
        var loginClientAccessTokenParameter = loginClientAccessToken?.Resource ??
                                         new ParameterResource(tokenParamName,
                                             _ =>
                                             {
                                                 var value = builder.ApplicationBuilder.Configuration[$"Parameters:{tokenParamName}"];

                                                 if (!string.IsNullOrWhiteSpace(value)) return value;

                                                 var filePath = Path.Combine(path, $"{loginClientUsernameParameter.GetValueAsync(CancellationToken.None).Result}.pat");
                                                 if (File.Exists(filePath))
                                                 {
                                                     return File.ReadAllText(filePath);
                                                 }

                                                 return string.Empty;
                                             }, true);

        builder.Resource.LoginClientUsernameParameter = loginClientUsernameParameter;
        builder.Resource.LoginClientAccessTokenParameter = loginClientAccessTokenParameter;

        builder
            .WithKeysMount()
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_LOGINCLIENTPATPATH", $"/keys/{loginClientUsernameParameter}.pat")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_MACHINE_USERNAME", loginClientUsernameParameter)
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_MACHINE_NAME", "Automatically Initialized IAM_LOGIN_CLIENT")
            .WithEnvironment("ZITADEL_FIRSTINSTANCE_ORG_LOGINCLIENT_PAT_EXPIRATIONDATE", (expirationDate ?? DateTime.UtcNow.AddYears(5)).ToString("yyyy-MM-ddTHH:mm:ssZ"));

        return builder;
    }

    /// <summary>
    /// Adds a Zitadel login client container for custom login UI functionality.
    /// Deploys a Next.js-based login interface that provides a customizable authentication experience for end users.
    /// </summary>
    /// <param name="zitadelBuilder">The Zitadel resource builder to add the login client to.</param>
    /// <param name="name">The name of the login client resource for service discovery and configuration.</param>
    /// <param name="port">Optional host port for the login client. If not specified, a random port will be assigned.</param>
    /// <param name="accessToken">Optional parameter resource for the service access token. If not provided, it will be read from the generated PAT file.</param>
    /// <returns>A resource builder for the Zitadel login client resource that can be used for further configuration.</returns>
    public static IResourceBuilder<ZitadelLoginClientResource> AddZitadelLoginClient(this IResourceBuilder<ZitadelResource> zitadelBuilder, 
        string name, 
        int? port = null,
        IResourceBuilder<ParameterResource>? accessToken = null)
    {
        ArgumentNullException.ThrowIfNull(zitadelBuilder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = zitadelBuilder.ApplicationBuilder;

        ParameterResource accessTokenParameter = accessToken?.Resource ??
                                                        zitadelBuilder.Resource.LoginClientAccessTokenParameter ?? throw new ArgumentException("access token missing");

        ZitadelLoginClientResource loginClientResource = new(name, accessTokenParameter);

        IResourceBuilder<ZitadelLoginClientResource> loginBuilder = zitadelBuilder.ApplicationBuilder.AddResource(loginClientResource)
            .WithImage(ZitadelContainerImageTags.LoginImage)
            .WithImageRegistry(ZitadelContainerImageTags.Registry)
            .WithImageTag(ZitadelContainerImageTags.LoginTag)
            .WithHttpEndpoint(port, 3000)
            .WithEnvironment("ZITADEL_API_URL", zitadelBuilder.Resource.GetEndpoint(ZitadelResource.HttpEndpointName))
            .WithEnvironment("NEXT_PUBLIC_BASE_PATH", ZitadelLoginClientResource.BasePath)
            .WithEnvironment("ZITADEL_SERVICE_USER_TOKEN", accessTokenParameter)
            .WithEnvironment("NODE_TLS_REJECT_UNAUTHORIZED", "0")
            .WithEnvironment("CUSTOM_REQUEST_HEADERS", "Host:localhost")
            .WithHttpHealthCheck(ZitadelLoginClientResource.BasePath + "/healthy", 200)
            .WaitFor(zitadelBuilder);

        zitadelBuilder.WithEnvironment("ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_REQUIRED", "true")
            .WithEnvironment(async context =>
            {
                // workaround to resolve external address of login client
                if (zitadelBuilder.ApplicationBuilder.ExecutionContext.IsRunMode)
                {
                    context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] =
                        await loginBuilder.Resource.BaseEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGINURLV2"] = await loginBuilder.Resource.OidcLoginEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGOUTURLV2"] = await loginBuilder.Resource.OidcLogoutEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                    context.EnvironmentVariables["ZITADEL_SAML_DEFAULTLOGINURLV2"] = await loginBuilder.Resource.SamlLoginEndpoint.GetValueAsync(context.CancellationToken) ?? "";
                }
                else
                {
                    context.EnvironmentVariables["ZITADEL_DEFAULTINSTANCE_FEATURES_LOGINV2_BASEURI"] = loginBuilder.Resource.BaseEndpoint;
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGINURLV2"] = loginBuilder.Resource.OidcLoginEndpoint;
                    context.EnvironmentVariables["ZITADEL_OIDC_DEFAULTLOGOUTURLV2"] = loginBuilder.Resource.OidcLogoutEndpoint;
                    context.EnvironmentVariables["ZITADEL_SAML_DEFAULTLOGINURLV2"] = loginBuilder.Resource.SamlLoginEndpoint;
                }
            });

#pragma warning disable ASPIRECERTIFICATES001
        loginBuilder.WithHttpsCertificateConfiguration(ctx =>
        {
            ctx.EnvironmentVariables["ZITADEL_TLS_ENABLED"] = "true";
            ctx.EnvironmentVariables["ZITADEL_TLS_CERTPATH"] = ctx.CertificatePath;
            ctx.EnvironmentVariables["ZITADEL_TLS_KEYPATH"] = ctx.KeyPath;
            return Task.CompletedTask;
        });
#pragma warning restore ASPIRECERTIFICATES001


        if (builder.ExecutionContext.IsRunMode)
        {
#pragma warning disable ASPIRECERTIFICATES001
            builder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
            {
                var developerCertificateService = @event.Services.GetRequiredService<IDeveloperCertificateService>();

                bool addHttps = false;
                if (!loginBuilder.Resource.TryGetLastAnnotation<HttpsCertificateAnnotation>(out var annotation))
                {
                    if (developerCertificateService.UseForHttps)
                    {
                        // If no certificate is configured, and the developer certificate service supports container trust,
                        // configure the resource to use the developer certificate for its key pair.
                        addHttps = true;
                    }
                }
                else if (annotation.UseDeveloperCertificate.GetValueOrDefault(developerCertificateService.UseForHttps) || annotation.Certificate is not null)
                {
                    addHttps = true;
                }

                if (addHttps)
                {
                    // If a TLS certificate is configured, override the endpoint to use HTTPS instead of HTTP
                    // Zitadel only binds to a single port
                    loginBuilder
                        .WithEndpoint(ZitadelLoginClientResource.HttpEndpointName, ep => ep.UriScheme = "https");
                }

                return Task.CompletedTask;
            });
#pragma warning restore ASPIRECERTIFICATES001
        }

        return loginBuilder;
    }

    private static string GetLocalKeysPath(string name, bool ensureExists = true)
    {
        var path = Path.GetFullPath($"./.zitadel/{name}-keys");
        if (ensureExists && !Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }
}