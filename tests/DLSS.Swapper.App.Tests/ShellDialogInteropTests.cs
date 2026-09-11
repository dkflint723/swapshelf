using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// The shell dialogs behind the app's folder and file pickers still come up.
/// </summary>
/// <remarks>
/// <para>
/// The Win32 declarations they use are generated in the Data project and reach the app through
/// InternalsVisibleTo. The app used to generate its own copy of an overlapping list, which put every
/// generated type in the compilation twice - fourteen CS0436 warnings on every build. Consolidating
/// them removed the warnings; this pins that it did not remove the pickers with them, which no
/// other test touches because none of them opens a dialog.
/// </para>
/// <para>
/// Each dialog is created and configured the way FileSystemHelper does it, and never shown. On a
/// thread of its own in the single-threaded apartment, as the UI thread that shows them for real is.
/// </para>
/// </remarks>
public class ShellDialogInteropTests
{
    static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception err)
            {
                failure = err;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("The shell dialog could not be set up: " + failure);
        }
    }

    [Fact]
    public void TheFolderPickerDialogCanBeCreatedAndPointedAtAFolder()
    {
        var folder = Directory.CreateTempSubdirectory("swapshelf-dialog-").FullName;
        try
        {
            OnStaThread(() =>
            {
                var created = PInvoke.CoCreateInstance<IFileOpenDialog>(typeof(FileOpenDialog).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out var dialog);
                Assert.True(created >= 0, $"CoCreateInstance(FileOpenDialog) returned 0x{created.Value:X8}");

                try
                {
                    dialog.SetOptions(FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM);

                    var parsed = PInvoke.SHCreateItemFromParsingName<IShellItem>(folder, null, out var item);
                    Assert.True(parsed >= 0, $"SHCreateItemFromParsingName returned 0x{parsed.Value:X8}");

                    dialog.SetFolder((IShellItem)item);
                    dialog.SetDefaultFolder((IShellItem)item);
                    dialog.SetOkButtonLabel("Choose");
                    Marshal.ReleaseComObject(item);
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            });
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void TheSaveDialogCanBeCreatedAndGivenAName()
    {
        OnStaThread(() =>
        {
            var created = PInvoke.CoCreateInstance<IFileSaveDialog>(typeof(FileSaveDialog).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out var dialog);
            Assert.True(created >= 0, $"CoCreateInstance(FileSaveDialog) returned 0x{created.Value:X8}");

            try
            {
                dialog.SetFileName("swapshelf-export");
                dialog.SetDefaultExtension("zip");
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        });
    }
}
