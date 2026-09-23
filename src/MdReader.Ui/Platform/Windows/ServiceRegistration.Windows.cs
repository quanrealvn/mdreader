using MdReader.Edge;
using MdReader.Shell.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.Ui.Composition;

public static partial class ServiceRegistration
{
    /// The WebView2 backend, shared with the WPF shell through MdReader.Edge.
    private static partial void AddWebViewBackend(IServiceCollection services)
    {
        services.AddSingleton<WebView2EnvironmentProvider>();
        services.AddSingleton<IWebView2EnvironmentProvider>(sp => sp.GetRequiredService<WebView2EnvironmentProvider>());
        services.AddSingleton<IWebViewEnvironmentProvider>(sp => sp.GetRequiredService<WebView2EnvironmentProvider>());
    }
}
