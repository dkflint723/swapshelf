using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using DLSS_Swapper.Data;
using DLSS_Swapper.Interfaces;

namespace DLSS_Swapper.Helpers;

internal class SystemDetails
{
    /// <summary>
    /// This build's version, without asking the application for it.
    /// </summary>
    /// <remarks>
    /// The app used to be asked, which made a diagnostics dump impossible to produce from anything
    /// that is not the app - the command line included. Then this asked its own assembly, which is
    /// DLSS.Swapper.Data and versioned 1.0.0, so every dump said "Swapshelf: 1.0.0.0". The program
    /// that was started - app or command line - carries the release's number; see AppVersion.
    /// </remarks>
    static string VersionString()
    {
        return AppVersion.Display;
    }

    public string GetSystemData()
    {
        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine("```");
        try
        {
            var currentAssembly = Assembly.GetExecutingAssembly();


            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"Swapshelf: {VersionString()}");
#if PORTABLE
            stringBuilder.AppendLine("Portable: true");
#else
            stringBuilder.AppendLine("Portable: false");
#endif
            stringBuilder.AppendLine();

            stringBuilder.AppendLine("System");
            try
            {
                string query = "SELECT * FROM Win32_OperatingSystem";
                var searcher = new System.Management.ManagementObjectSearcher(query);

                foreach (var os in searcher.Get())
                {
                    stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"OS: {os["Caption"]}");
                }
            }
            catch (Exception)
            {
                // NOOP
            }
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"OSVersion: {Environment.OSVersion.VersionString}");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"Is64BitOperatingSystem: {Environment.Is64BitOperatingSystem}");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"Is64BitProcess: {Environment.Is64BitProcess}");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {Environment.Version}");


            stringBuilder.AppendLine();
            stringBuilder.AppendLine("Permissions");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"IsPrivilegedProcess: {Environment.IsPrivilegedProcess}");

            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"IsInRole Administrator: {principal.IsInRole(WindowsBuiltInRole.Administrator)}");
                stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"IsInRole User: {principal.IsInRole(WindowsBuiltInRole.User)}");
            }


            stringBuilder.AppendLine();
            stringBuilder.AppendLine("Paths");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"StoragePath: {Storage.StoragePath}");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"CurrentDirectory: {Environment.CurrentDirectory}");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"GetCurrentDirectory: {Directory.GetCurrentDirectory()}");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"AppDomain.CurrentDomain.BaseDirectory: {AppDomain.CurrentDomain.BaseDirectory}");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"Assembly Location: {currentAssembly.Location}");
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"ProcessPath: {Environment.ProcessPath ?? string.Empty}");

        }
        catch (Exception err)
        {
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"ERROR: {err.Message}");
        }
        finally
        {
            stringBuilder.AppendLine("```");
        }

        return stringBuilder.ToString();
    }

    public string GetLibraryData()
    {
        var gameList = GameManager.Instance.GetSynchronisedGamesListCopy();
        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine("```");

        try
        {
            foreach (var gameLibraryEnum in GameManager.Instance.GetGameLibraries(false))
            {
                var gameLibrary = IGameLibrary.GetGameLibrary(gameLibraryEnum);
                stringBuilder.AppendLine(gameLibrary.Name);
                stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"Status: {(gameLibrary.IsEnabled ? "Enabled" : "Disabled")}");
                if (gameLibrary.IsEnabled)
                {
                    stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"Games: {gameList.Count(x => x.GameLibrary == gameLibrary.GameLibrary)}");
                }
                stringBuilder.AppendLine();
            }
        }
        catch (Exception err)
        {
            stringBuilder.AppendLine(CultureInfo.InvariantCulture, $"ERROR: {err.Message}");
        }
        finally
        {
            stringBuilder.AppendLine("```");
        }

        return stringBuilder.ToString();
    }
}
