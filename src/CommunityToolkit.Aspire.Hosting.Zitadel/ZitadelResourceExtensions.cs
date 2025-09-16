using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.Threading;
using Zitadel.Api;
using Zitadel.Credentials;

// ReSharper disable once CheckNamespace
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
///     Extensions for <see cref="ZitadelResource" /> to provide easy access to service accounts and client options.
/// </summary>
public static class ZitadelResourceExtensions
{
    /// <summary>
    ///     Gets the machine service account from the Zitadel resource.
    /// </summary>
    /// <param name="zitadel"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static async Task<ServiceAccount> GetMachineServiceAccount(this ZitadelResource zitadel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zitadel);

        if (zitadel.MachineUserKeyParameter != null)
        {
            var machineKey = await zitadel.MachineUserKeyParameter.GetValueAsync(cancellationToken);

            if (machineKey == null)
            {
                throw new InvalidOperationException("MachineUserKey not available or KeyFile does not exists");
            }

            var machineKeyBytes = Convert.FromBase64String(machineKey);
            using var machineKeyBuffer = new MemoryStream(machineKeyBytes);
            return await ServiceAccount.LoadFromJsonStreamAsync(machineKeyBuffer);
        }

        if (string.IsNullOrEmpty(zitadel.MachineUserKeyPath) || !File.Exists(zitadel.MachineUserKeyPath))
        {
            throw new InvalidOperationException("MachineUserKeyPath not available or KeyFile does not exists");
        }

        return await ServiceAccount.LoadFromJsonFileAsync(zitadel.MachineUserKeyPath);
    }

    /// <summary>
    ///     Creates an API client options instance for the Zitadel resource using the machine service account.
    /// </summary>
    /// <param name="zitadel"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<Clients.Options> CreateApiClientOptions(this ZitadelResource zitadel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zitadel);

        ServiceAccount serviceAccount = await zitadel.GetMachineServiceAccount(cancellationToken);

        ITokenProvider tokenProvider = ITokenProvider.ServiceAccount(
            zitadel.PrimaryEndpoint.Url,
            serviceAccount,
            new ServiceAccount.AuthOptions { ApiAccess = true });

        Clients.Options options = new(zitadel.PrimaryEndpoint.Url, tokenProvider);
        return options;
    }
}