using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using DLSS_Swapper.Data;
using DLSS_Swapper.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DLSS_Swapper;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public sealed partial class App : Application, IUiDispatcher
{
    ElementTheme _globalElementTheme;

    /// <summary>
    /// The theme every page roots itself to.
    /// </summary>
    /// <remarks>
    /// Setting it repaints the accent, because each preset has a different value per theme and the
    /// ink that stays readable on it changes with them.
    /// </remarks>
    public ElementTheme GlobalElementTheme
    {
        get { return _globalElementTheme; }
        set
        {
            _globalElementTheme = value;
            AccentManager.Apply();
        }
    }

    MainWindow? _mainWindow;
#pragma warning disable CS8603 // Possible null reference return.
    public MainWindow MainWindow => _mainWindow;
#pragma warning restore CS8603 // Possible null reference return.
    public WindowManager WindowManager { get; } = new WindowManager();

    public static App CurrentApp => (App)Application.Current;

    public HttpClient HttpClient { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // Before anything else that might marshal: the data layer routes its UI work through
        // UiThread, which runs inline until something is here to hand it to.
        UiThread.Dispatcher = this;

        // The loading screen, for the manifest migration that reports into it. Unset outside the
        // app, where writing to it should do nothing rather than throw.
        Settings.AccentChanged = AccentManager.Apply;

        // Read each time: the client is replaced whenever the proxy settings change.
        Helpers.AppHttpClient.AppClient = () => CurrentApp?.HttpClient;

        LoadingMessage.Read = () => CurrentApp?.MainWindow?.ViewModel?.LoadingMessage;
        LoadingMessage.Write = (message) =>
        {
            var viewModel = CurrentApp?.MainWindow?.ViewModel;
            if (viewModel is not null)
            {
                UiThread.Run(() => viewModel.LoadingMessage = message);
            }
        };

        Logger.Init();

        HttpClient = GenerateNewHttpClient();

        var language = Settings.Instance.Language;

        // Language is not set, try to fetch from system.
        if (string.IsNullOrWhiteSpace(language))
        {
            // Try the language of the current thread.
            var currentLauguage = Thread.CurrentThread.CurrentCulture.Name;
            var knownLanguages = LanguageManager.Instance.GetKnownLanguages();
            foreach (var knownLanguage in knownLanguages)
            {
                if (string.Equals(currentLauguage, knownLanguage, StringComparison.InvariantCultureIgnoreCase))
                {
                    language = knownLanguage;
                    break;
                }
            }

            // TODO: Can we fallback to other languages? eg. Is fr-CA acceptable to fallback to fr-FR or does the app just default back to en-US?
        }

        // If we failed to fetch the users language, default to en-US.
        if (string.IsNullOrWhiteSpace(language))
        {
            language = "en-US";
        }
        Settings.Instance.Language = language;

        LanguageManager.Instance.ChangeLanguage(language);

        UnhandledException += App_UnhandledException;

        GlobalElementTheme = (ElementTheme)Settings.Instance.AppTheme;

        this.InitializeComponent();
    }

    internal void RegenerateHttpClient()
    {
        HttpClient = GenerateNewHttpClient();
    }


    /// <summary>
    /// The app's client, built the same way anything headless builds one.
    /// </summary>
    /// <remarks>
    /// The body moved to <see cref="Helpers.AppHttpClient"/> so that code reached with no app
    /// running - the command line, tests - can make the same requests with the same proxy and user
    /// agent, rather than dereferencing a null App partway through a download.
    /// </remarks>
    HttpClient GenerateNewHttpClient()
    {
        return Helpers.AppHttpClient.Create();
    }


    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Serilog.Log.Error(e.Exception, "UnhandledException");
        Serilog.Log.CloseAndFlush();
    }

    /// <summary>
    /// Invoked when the application is launched normally by the end user.  Other entry points
    /// will be used such as when the application is launched to open a specific file.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // If this is the first instance launched, then register it as the "main" instance.
        // If this isn't the first instance launched, then "main" will already be registered,
        // so retrieve it.
        var mainInstance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("main");

        // If the instance that's executing the OnLaunched handler right now
        // isn't the "main" instance.
        if (mainInstance.IsCurrent == false)
        {
            // Redirect the activation (and args) to the "main" instance, and exit.
            var activatedEventArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            await mainInstance.RedirectActivationToAsync(activatedEventArgs);
            Process.GetCurrentProcess().Kill();
            return;
        }

        if (Storage.StoragePath.Trim(Path.DirectorySeparatorChar).Contains(Environment.SystemDirectory, StringComparison.InvariantCultureIgnoreCase))
        {
            var failToLaunchWindow = new FailToLaunchWindow();
            WindowManager.ShowWindow(failToLaunchWindow);
            return;
        }

        var version = GetVersion();
        var versionString = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        Logger.Info($"App launch - v{versionString}", null);
        Logger.Info($"StoragePath: {Storage.StoragePath}");

        // Check if its the first launch of the app from a new version.
        var lastLaunchVersion = Settings.Instance.LastLaunchVersion;
        if (lastLaunchVersion != versionString)
        {
            try
            {
                var manifestPath = Storage.GetManifestPath();
                if (File.Exists(manifestPath))
                {
                    var fileInfo = new FileInfo(manifestPath);
                    // Asked of DLSS.Swapper.Data, not of this assembly. The manifest is embedded
                    // there now, beside the DLLManager that is its main reader - and asking the
                    // wrong assembly does not fail, it returns null, which would have quietly
                    // meant "no bundled manifest" on every first run.
                    using (var staticManifestStream = typeof(DLLManager).Assembly.GetManifestResourceStream("DLSS_Swapper.Assets.static_manifest.json"))
                    {
                        if (staticManifestStream is not null)
                        {
                            // If the static manifest is larger than the file, we likely want to replace the current manifest.
                            if (staticManifestStream.Length >= fileInfo.Length)
                            {
                                using (var fileWriter = File.Create(manifestPath))
                                {
                                    var length = fileWriter.Length;
                                    staticManifestStream.CopyTo(fileWriter);
                                }
                            }
                        }
                    }
                }

                Settings.Instance.LastLaunchVersion = versionString;
            }
            catch (Exception err)
            {
                Logger.Error(err, "Unable to perform first launch duties.");
            }
        }

        Database.Instance.Init();

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
        }
        WindowManager.ShowWindow(_mainWindow);

        // Only now: UISettings and the resolved theme are both unsafe to touch before the window
        // exists, and doing so kills the process before anything is logged.
        AccentManager.Start();

#if !PORTABLE
        // No need to calculate this for portable app.
        var calculateInstallSizeThread = new Thread(CalculateInstallSize);
        calculateInstallSizeThread.Start();
#endif

        // Delete updates folder
        var updatesFolder = Storage.GetUpdatesFolder();
        if (Directory.Exists(updatesFolder))
        {
            try
            {
                Directory.Delete(updatesFolder, true);
            }
            catch (Exception err)
            {
                // If we failed 
                Logger.Error(err);
            }
        }
    }

#if !PORTABLE
    /// <summary>How often the number Windows shows in Apps &amp; features is worked out again.</summary>
    /// <remarks>
    /// This walks every file under the app's data folder, and that folder holds the downloaded dll
    /// cache - which is where the size comes from and can be thousands of files. It used to do that
    /// on every launch, competing for the disk with the library scan happening at the same time, to
    /// refresh a number nobody is looking at. A week late is not a wrong answer for an estimate.
    /// </remarks>
    const double InstallSizeIntervalDays = 7;

    const string InstallSizeCalculatedAtValueName = "DlssSwapperEstimatedSizeCalculatedAt";

    void CalculateInstallSize()
    {
        try
        {
            using (var dlssSwapperRegistryKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Swapshelf", true))
            {
                if (dlssSwapperRegistryKey is null)
                {
                    // Not an installed copy, so there is no entry to keep up to date and nothing
                    // would read the answer. The walk used to happen first and be thrown away.
                    return;
                }

                // Every launch, not weekly like the size below: a version that is wrong is wrong now.
                // The installer's own write of it has failed to stick more than once; see UninstallEntry.
                var replaced = UninstallEntry.KeepVersionCurrent(dlssSwapperRegistryKey, AppContext.BaseDirectory, AppVersion.Display);
                if (replaced is not null)
                {
                    Logger.Warning($"Add or remove programs named version {replaced}; corrected it to {AppVersion.Display}.");
                }

                if (WasInstallSizeCalculatedRecently(dlssSwapperRegistryKey))
                {
                    return;
                }

                long installSize = 0;
                installSize += CalculateDirectorySize(Storage.StoragePath);

                var installLocation = dlssSwapperRegistryKey.GetValue("InstallLocation") as string;
                if (string.IsNullOrEmpty(installLocation) == false && Directory.Exists(installLocation) == true)
                {
                    installSize += CalculateDirectorySize(installLocation);
                }

                if (installSize > 0)
                {
                    var installSizeKB = (int)(installSize / 1000);
                    dlssSwapperRegistryKey.SetValue("EstimatedSize", installSizeKB, Microsoft.Win32.RegistryValueKind.DWord);

                    // Stamped only after a real answer was written, so a walk that found nothing
                    // does not buy itself a week of silence.
                    dlssSwapperRegistryKey.SetValue(
                        InstallSizeCalculatedAtValueName,
                        DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
                        Microsoft.Win32.RegistryValueKind.String);
                }
            }
        }
        catch (Exception err)
        {
            Logger.Error(err);
        }
    }

    /// <summary>
    /// Whether the size was worked out recently enough to leave alone.
    /// </summary>
    /// <remarks>
    /// Kept in the same key as the value it guards, so an uninstall takes both and a fresh install
    /// starts by measuring. A clock that has gone backwards reads as "not recent", which costs one
    /// extra walk rather than parking the estimate forever.
    /// </remarks>
    static bool WasInstallSizeCalculatedRecently(Microsoft.Win32.RegistryKey registryKey)
    {
        if (registryKey.GetValue(InstallSizeCalculatedAtValueName) is not string stamp)
        {
            return false;
        }

        if (long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) == false)
        {
            return false;
        }

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);

        return age >= TimeSpan.Zero && age.TotalDays < InstallSizeIntervalDays;
    }

    long CalculateDirectorySize(string path)
    {
        var directorySize = 0L;
        var fileCount = 0;
        var directoryInfo = new DirectoryInfo(path);
        foreach (var fileInfo in directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            directorySize += fileInfo.Length;
            ++fileCount;
        }

        //Logger.Debug($"{path} has {fileCount} files for a total size of {directorySize} bytes");

        return directorySize;
    }
#endif

    public bool IsAdminUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);

        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void RestartAsAdmin()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.CurrentDirectory,
            FileName = Assembly.GetExecutingAssembly().GetName().Name,
            Verb = "runas"
        };

        try
        {
            Process.Start(startInfo);
            Logger.Info("Restarting as admin.");
        }
        catch (Win32Exception)
        {
            Logger.Warning("User refused the elevation.");
            return;
        }

        App.CurrentApp.Exit();
    }

    /*
    // Disabled as I am unsure how to prompt to run as admin.
    internal void RelaunchAsAdministrator()
    {
        //var currentExe = Process.GetCurrentProcess().MainModule.FileName;

        //var executingAssembly = System.Reflection.Assembly.GetExecutingAssembly();
        //executingAssembly.FullName;
        
        // So this does prompt UAC, this was temporarily used to copy files in UpdateDll and ResetDll
        // but it would prompt for every action. 
        //var startInfo = new ProcessStartInfo()
        //{
        //    WindowStyle = ProcessWindowStyle.Hidden,
        //    FileName = "cmd.exe",
        //    Arguments = $"/C copy \"{dll}\" \"{targetDllPath}\"",
        //    UseShellExecute = true,
        //    Verb = "runas",
        //};
        //Process.Start(startInfo);

        MainWindow.Close();
        //Logger.Error(System.Reflection.Assembly.GetExecutingAssembly().Location);
    }
    */

    public Version GetVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version();
    }

    public string GetVersionString()
    {
        var version = GetVersion();
        if (version.Build == 0 && version.Revision == 0)
        {
            return $"{version.Major}.{version.Minor}";
        }
        else if (version.Revision == 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    bool IUiDispatcher.Run(Action action) => RunOnUIThread(action);

    bool IUiDispatcher.TryEnqueue(Action action)
    {
        var dispatcher = MainWindow?.DispatcherQueue;

        // Null before the window exists, which the caller treats the same as a queue that refused
        // the work: it does the work itself instead.
        return dispatcher is not null && dispatcher.TryEnqueue(() => action());
    }

    Task IUiDispatcher.RunAsync(Func<Task> function) => RunOnUIThreadAsync(function);

    public bool RunOnUIThread(Action action)
    {
        if (Environment.CurrentManagedThreadId == 1)
        {
            action();
            return true;
        }

        if (_mainWindow?.DispatcherQueue is not null)
        {
            var didEnqueue = _mainWindow.DispatcherQueue.TryEnqueue(new DispatcherQueueHandler(action));

            if (didEnqueue == false)
            {
                try
                {
                    // I am sure there is a better way to fill out a stacktrace than throwing an exception
                    throw new Exception("TryEnqueue failed.");
                }
                catch (Exception err)
                {
                    Logger.Error(err);
                }
            }

            return didEnqueue;
        }

        return false;
    }


    public Task RunOnUIThreadAsync(Func<Task> function)
    {
        if (Environment.CurrentManagedThreadId == 1)
        {
            return function();
        }

        if (_mainWindow?.DispatcherQueue is not null)
        {
            return _mainWindow.DispatcherQueue.EnqueueAsync(function);
        }

        return Task.CompletedTask;
    }

}

