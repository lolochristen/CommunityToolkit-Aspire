using Zitadel.Api;
using Zitadel.Credentials;

// ReSharper disable once CheckNamespace
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Extensions for <see cref="ZitadelResource"/> to provide easy access to service accounts and client options.
/// </summary>
public static class ZitadelResourceExtensions
{
    /// <summary>
    /// Gets the machine service account from the Zitadel resource.
    /// </summary>
    /// <param name="zitadel"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static ServiceAccount GetMachinServiceAccount(this ZitadelResource zitadel)
    {
        ArgumentNullException.ThrowIfNull(zitadel);

        if (string.IsNullOrEmpty(zitadel.MachineUserKeyPath) || !File.Exists(zitadel.MachineUserKeyPath))
        {
            throw new InvalidOperationException("MachineUserKeyPath not available or KeyFile does not exists");
        }

        return ServiceAccount.LoadFromJsonFile(zitadel.MachineUserKeyPath);
    }

    /// <summary>
    /// Creates an API client options instance for the Zitadel resource using the machine service account.
    /// </summary>
    /// <param name="zitadel"></param>
    /// <returns></returns>
    public static Clients.Options CreateApiClientOptions(this ZitadelResource zitadel)
    {
        ArgumentNullException.ThrowIfNull(zitadel);

        var serviceAccount = zitadel.GetMachinServiceAccount();

        ITokenProvider tokenProvider = ITokenProvider.ServiceAccount(
            zitadel.PrimaryEndpoint.Url,
            serviceAccount,
            new ServiceAccount.AuthOptions { ApiAccess = true });

        Clients.Options options = new(zitadel.PrimaryEndpoint.Url, tokenProvider);
        return options;
    }

}