using System;
using System.Net;
using System.Net.Http;
using System.Reflection;

namespace DLSS_Swapper.Helpers;

/// <summary>
/// The one HttpClient the app makes requests through, and how it is built.
/// </summary>
/// <remarks>
/// <para>
/// It used to live on <c>App</c> and be reached as <c>App.CurrentApp.HttpClient</c>, which meant
/// every download, cover lookup and store query needed an application object to exist. With no app
/// running - from the command line, or under test - that is a null dereference partway through a
/// download rather than an honest failure, and the download path is the one place where failing
/// halfway is worst.
/// </para>
/// <para>
/// The configuration is unchanged and still built in one place: the proxy from settings, the user
/// agent carrying this build's version, the long timeout a several hundred megabyte dll needs. The
/// app still owns its instance and still replaces it when the proxy settings change; this simply
/// makes one available when there is no app to own it.
/// </para>
/// </remarks>
internal static class AppHttpClient
{
    static HttpClient? _headlessClient;

    /// <summary>
    /// The app's own client, when there is an app. Null everywhere else.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a reference to the app, because this is compiled without it now. Read
    /// each time rather than cached, since the app replaces its client whenever the proxy settings
    /// change and everything is meant to follow.
    /// </remarks>
    internal static Func<HttpClient?>? AppClient { get; set; }

    /// <summary>
    /// The app's client when there is an app, otherwise one built the same way.
    /// </summary>
    /// <remarks>
    /// Prefers the app's own so that regenerating it after a proxy change keeps taking effect for
    /// everything - two clients with different proxies would be worse than none.
    /// </remarks>
    internal static HttpClient Shared
    {
        get
        {
            var fromApp = AppClient?.Invoke();
            if (fromApp is not null)
            {
                return fromApp;
            }

            return _headlessClient ??= Create();
        }
    }

    /// <summary>Builds a client configured the way this app talks to the internet.</summary>
    internal static HttpClient Create()
    {
        // The running program's version, not this library's 1.0.0 - see AppVersion.
        var versionString = AppVersion.Display;

        var httpClientHandler = new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
        };

        Settings.ProxySettings.LoadIfNeeded();

        if (string.IsNullOrWhiteSpace(Settings.ProxySettings.Server) == false)
        {
            try
            {
                var server = Settings.ProxySettings.Server;
                if (server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var proxy = new WebProxy
                    {
                        BypassProxyOnLocal = false,
                        UseDefaultCredentials = false,
                        Address = new Uri(server),
                    };

                    if (string.IsNullOrWhiteSpace(Settings.ProxySettings.Username) == false && string.IsNullOrWhiteSpace(Settings.ProxySettings.Password) == false)
                    {
                        proxy.Credentials = new NetworkCredential(Settings.ProxySettings.Username, Settings.ProxySettings.Password);
                    }

                    httpClientHandler.UseProxy = true;
                    httpClientHandler.Proxy = proxy;
                }
                else
                {
                    Logger.Error($"Tried to set proxy with server address \"{server}\"");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to set proxy for HttpClient");
                Logger.Error(ex);
            }
        }

        var newHttpClient = new HttpClient(httpClientHandler);
        newHttpClient.DefaultRequestHeaders.Add("User-Agent", $"dlss-swapper/{versionString}");
        newHttpClient.Timeout = TimeSpan.FromMinutes(30);
        newHttpClient.DefaultRequestVersion = new Version(2, 0);
        newHttpClient.DefaultRequestHeaders.ConnectionClose = true;
        return newHttpClient;
    }
}
