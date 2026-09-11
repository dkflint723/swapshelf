using System;
using System.IO;
using DLSS_Swapper.Helpers;
using Microsoft.Win32;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// The installed copy puts the right version into Add or remove programs when the installer did not.
/// </summary>
/// <remarks>
/// Against a scratch key of the same shape under the current user, deleted afterwards - never the real
/// entry. The rules that matter are the two refusals: a copy running from anywhere but the install
/// folder must not stamp its version on the entry, and an entry already right is not written at all.
/// </remarks>
public class UninstallEntryTests : IDisposable
{
    readonly string _keyPath = ParentPath + @"\" + Guid.NewGuid().ToString("N");
    readonly RegistryKey _key;
    readonly string _installFolder = Path.Combine(Path.GetTempPath(), "swapshelf-install-" + Guid.NewGuid().ToString("N"));

    public UninstallEntryTests()
    {
        _key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
    }

    public void Dispose()
    {
        _key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

        // And the folder the scratch keys live in, once the last one is gone, so a test run leaves
        // nothing behind in the registry of whoever ran it.
        using (var parent = Registry.CurrentUser.OpenSubKey(ParentPath, writable: true))
        {
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(ParentPath, throwOnMissingSubKey: false);
            }
        }
    }

    const string ParentPath = @"Software\SwapshelfTests";

    void Entry(string? version, string? installLocation)
    {
        if (version is not null)
        {
            _key.SetValue("DisplayVersion", version);
        }

        if (installLocation is not null)
        {
            _key.SetValue("InstallLocation", installLocation);
        }
    }

    [Fact]
    public void AStaleVersionIsCorrectedByTheInstalledCopy()
    {
        Entry("3.0.5.0", _installFolder);

        var replaced = UninstallEntry.KeepVersionCurrent(_key, _installFolder + Path.DirectorySeparatorChar, "3.0.6.2");

        Assert.Equal("3.0.5.0", replaced);
        Assert.Equal("3.0.6.2", _key.GetValue("DisplayVersion"));

        var note = Assert.IsType<string>(_key.GetValue(UninstallEntry.CorrectedValueName));
        Assert.StartsWith("3.0.5.0 -> 3.0.6.2 at ", note);
    }

    [Fact]
    public void TheInstallFolderMatchesWhateverItsCaseOrTrailingSeparator()
    {
        Entry("3.0.5.0", _installFolder.ToUpperInvariant() + "\\");

        Assert.Equal("3.0.5.0", UninstallEntry.KeepVersionCurrent(_key, _installFolder.ToLowerInvariant(), "3.0.6.2"));
    }

    [Fact]
    public void ACopyRunningFromSomewhereElseLeavesTheEntryAlone()
    {
        // A development build, or a portable copy, is not the program this entry describes.
        Entry("3.0.5.0", _installFolder);

        var replaced = UninstallEntry.KeepVersionCurrent(_key, Path.Combine(Path.GetTempPath(), "some-other-copy"), "9.9.9.9");

        Assert.Null(replaced);
        Assert.Equal("3.0.5.0", _key.GetValue("DisplayVersion"));
        Assert.Null(_key.GetValue(UninstallEntry.CorrectedValueName));
    }

    [Fact]
    public void AnEntryThatIsAlreadyRightIsNotWritten()
    {
        Entry("3.0.6.2", _installFolder);

        Assert.Null(UninstallEntry.KeepVersionCurrent(_key, _installFolder, "3.0.6.2"));
        Assert.Null(_key.GetValue(UninstallEntry.CorrectedValueName));
    }

    [Fact]
    public void AnEntryWithNoInstallLocationIsLeftAlone()
    {
        Entry("3.0.5.0", installLocation: null);

        Assert.Null(UninstallEntry.KeepVersionCurrent(_key, _installFolder, "3.0.6.2"));
        Assert.Equal("3.0.5.0", _key.GetValue("DisplayVersion"));
    }

    [Fact]
    public void AMissingVersionIsFilledIn()
    {
        Entry(version: null, _installFolder);

        Assert.Equal(string.Empty, UninstallEntry.KeepVersionCurrent(_key, _installFolder, "3.0.6.2"));
        Assert.Equal("3.0.6.2", _key.GetValue("DisplayVersion"));
        Assert.StartsWith("(none) -> 3.0.6.2", (string)_key.GetValue(UninstallEntry.CorrectedValueName)!);
    }

    [Fact]
    public void TheVersionIsFourPartsLikeTheInstallerWritesIt()
    {
        var parts = AppVersion.Display.Split('.');

        Assert.Equal(4, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out var number) && number >= 0));
    }
}
