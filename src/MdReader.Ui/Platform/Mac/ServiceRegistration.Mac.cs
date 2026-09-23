using MdReader.Mac;
using MdReader.Shell.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.Ui.Composition;

public static partial class ServiceRegistration
{
    /// The WKWebView backend. There is no runtime to find and nothing to download: WebKit ships with the system.
    private static partial void AddWebViewBackend(IServiceCollection services)
    {
        services.AddSingleton<WkWebViewEnvironmentProvider>();
        services.AddSingleton<IWebViewEnvironmentProvider>(sp => sp.GetRequiredService<WkWebViewEnvironmentProvider>());
    }
}
