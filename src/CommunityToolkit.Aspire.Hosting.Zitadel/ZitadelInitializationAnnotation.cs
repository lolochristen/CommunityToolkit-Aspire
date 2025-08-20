using Aspire.Hosting.ApplicationModel;
using Zitadel.Api;

namespace Aspire.Hosting;

/// <summary>
/// </summary>
public class ZitadelInitializationAnnotation : IResourceAnnotation
{
    /// <summary>
    ///     Constructs a new instance of the <see cref="ZitadelInitializationAnnotation" /> class.
    /// </summary>
    /// <param name="initialization"></param>
    public ZitadelInitializationAnnotation(Func<Clients.Options, IResource, Task> initialization)
    {
        Initialization = initialization;
    }

    /// <summary>
    /// </summary>
    public Func<Clients.Options, IResource, Task> Initialization { get; }
}