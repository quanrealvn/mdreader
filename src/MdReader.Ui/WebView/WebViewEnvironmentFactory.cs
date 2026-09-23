using MdReader.Core.Diagnostics;
using MdReader.Edge;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Ui.WebView;

/// <summary>
/// The single <see cref="CoreWebView2Environment"/> every web view in the process shares (ARCHITECTURE §6). The options
/// come from <see cref="WebView2EnvironmentProvider"/>: they MUST stay identical in every process that shares the user
/// data folder, so both shells create the environment through the same call.
/// </summary>
internal sealed class WebViewEnvironmentFactory
{
    internal const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private const string Category = "WebView2";

    private readonly string _userDataFolder;
    private readonly IAppLog _log;
    private Task<CoreWebView2Environment>? _environment;

    public WebViewEnvironmentFactory(string userDataFolder, IAppLog log)
    {
        _userDataFolder = userDataFolder;
        _log = log;
    }

    /// <summary>Idempotent; every caller gets the same task. UI thread.</summary>
    public Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is not null)
        {
            return _environment;
        }

        try
        {
            Directory.CreateDirectory(_userDataFolder);
            _log.Write(AppLogLevel.Debug, Category, $"Creating the environment; user data folder {_userDataFolder}.");
            _environment = WebView2EnvironmentProvider.CreateAsync(_userDataFolder);
        }
        catch (Exception ex)
        {
            _environment = Task.FromException<CoreWebView2Environment>(ex);
        }

        return _environment;
    }

    /// <summary>A one-line reason for stderr when the environment can't be created; exit code 3 follows.</summary>
    public static string DescribeFailure(Exception exception) => exception is WebView2RuntimeNotFoundException
        ? "MdReader needs the Microsoft Edge WebView2 Runtime to display documents, and it isn't installed. "
          + $"Download it from {RuntimeDownloadUrl}"
        : $"MdReader couldn't start the Microsoft Edge WebView2 Runtime: {exception.Message} "
          + $"Reinstalling or repairing the runtime usually fixes this: {RuntimeDownloadUrl}";
}
