using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DLSS_Swapper.Data;
using DLSS_Swapper.Dlls;
using Xunit;

namespace DLSS_Swapper.Tests;

public class DllTypesTests
{
    [Fact]
    public void All_CoversEverySwappableAssetType()
    {
        var expected = new[]
        {
            GameAssetType.DLSS, GameAssetType.DLSS_G, GameAssetType.DLSS_D, GameAssetType.DLSS_NR,
            GameAssetType.FSR_31_DX12, GameAssetType.FSR_31_VK,
            GameAssetType.XeSS, GameAssetType.XeSS_FG, GameAssetType.XeSS_DX11, GameAssetType.XeLL,
        };

        Assert.Equal(expected.OrderBy(x => x), DllTypes.All.Select(x => x.AssetType).OrderBy(x => x));
    }

    #region The table itself has to be internally consistent

    [Fact]
    public void AssetTypes_AreUnique()
    {
        Assert.Equal(DllTypes.All.Length, DllTypes.All.Select(x => x.AssetType).Distinct().Count());
    }

    [Fact]
    public void FileNames_AreUnique()
    {
        Assert.Equal(DllTypes.All.Length, DllTypes.All.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ManifestKeys_AreUnique()
    {
        Assert.Equal(DllTypes.All.Length, DllTypes.All.Select(x => x.ManifestKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BackupTypes_AreUnique()
    {
        Assert.Equal(DllTypes.All.Length, DllTypes.All.Select(x => x.BackupAssetType).Distinct().Count());
    }

    /// <summary>
    /// A backup type sharing a value with a swappable type would make a backup look like an
    /// installed dll, which is how a game's original gets overwritten with itself.
    /// </summary>
    [Fact]
    public void BackupTypes_NeverCollideWithSwappableTypes()
    {
        var swappable = DllTypes.All.Select(x => x.AssetType).ToHashSet();
        var backups = DllTypes.All.Select(x => x.BackupAssetType).ToHashSet();

        Assert.Empty(swappable.Intersect(backups));
    }

    /// <summary>
    /// The two exemptions are narrow, and both belong to the same kind of type: one nobody ships.
    /// </summary>
    /// <remarks>
    /// A released upscaler must never carry either flag. Games ship it, so the copy in a game
    /// folder is a real original worth saving, and upstream publishes it, so the manifest check
    /// applies. Both flags off for such a type would silently drop it out of two safeguards.
    /// </remarks>
    [Fact]
    public void OnlyDllsNoGameShipsAreExemptFromBackupsAndTheManifest()
    {
        var notShipped = DllTypes.All.Where(x => x.GamesShipThisDll == false).ToList();
        var notInManifest = DllTypes.All.Where(x => x.ExpectedInUpstreamManifest == false).ToList();

        // A dll games do not ship cannot be one upstream publishes a download for, and the reverse
        // would be stranger still: a released dll the manifest is not expected to carry.
        Assert.Equal(notShipped, notInManifest);

        Assert.All(notShipped, x => Assert.Equal(GameAssetType.DLSS_NR, x.AssetType));
    }

    [Fact]
    public void NoDefinition_HasAnUnknownVendor()
    {
        Assert.DoesNotContain(DllTypes.All, x => x.Vendor == DllVendor.Unknown);
    }

    [Fact]
    public void NoDefinition_HasEmptyFields()
    {
        Assert.All(DllTypes.All, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.FileName));
            Assert.False(string.IsNullOrWhiteSpace(x.ManifestKey));
            Assert.False(string.IsNullOrWhiteSpace(x.DisplayNameResourceKey));
        });
    }

    #endregion

    #region Lookups

    [Theory]
    [InlineData("nvngx_dlss.dll", GameAssetType.DLSS)]
    [InlineData("nvngx_dlssg.dll", GameAssetType.DLSS_G)]
    [InlineData("nvngx_dlssd.dll", GameAssetType.DLSS_D)]
    [InlineData("nvngx_dlssnr.dll", GameAssetType.DLSS_NR)]
    [InlineData("amd_fidelityfx_dx12.dll", GameAssetType.FSR_31_DX12)]
    [InlineData("amd_fidelityfx_vk.dll", GameAssetType.FSR_31_VK)]
    [InlineData("libxess.dll", GameAssetType.XeSS)]
    [InlineData("libxess_fg.dll", GameAssetType.XeSS_FG)]
    [InlineData("libxess_dx11.dll", GameAssetType.XeSS_DX11)]
    [InlineData("libxell.dll", GameAssetType.XeLL)]
    public void ForFileName_ResolvesEveryKnownDll(string fileName, GameAssetType expected)
    {
        Assert.Equal(expected, DllTypes.ForFileName(fileName)?.AssetType);
    }

    /// <summary>Game directories are scanned case insensitively, matching how Windows behaves.</summary>
    [Fact]
    public void ForFileName_IsCaseInsensitive()
    {
        Assert.Equal(GameAssetType.DLSS, DllTypes.ForFileName("NVNGX_DLSS.DLL")?.AssetType);
    }

    /// <summary>
    /// libxess.dll and libxess_dx11.dll share a prefix, so a sloppy match would collapse them.
    /// </summary>
    [Fact]
    public void ForFileName_DistinguishesSimilarlyNamedDlls()
    {
        Assert.Equal(GameAssetType.XeSS, DllTypes.ForFileName("libxess.dll")?.AssetType);
        Assert.Equal(GameAssetType.XeSS_DX11, DllTypes.ForFileName("libxess_dx11.dll")?.AssetType);
        Assert.Equal(GameAssetType.XeSS_FG, DllTypes.ForFileName("libxess_fg.dll")?.AssetType);

        // The nvngx_dlss* family is the same trap and tighter: every name here is the plain one
        // plus a suffix, so a prefix match would file all four as DLSS and swap the wrong dll.
        Assert.Equal(GameAssetType.DLSS, DllTypes.ForFileName("nvngx_dlss.dll")?.AssetType);
        Assert.Equal(GameAssetType.DLSS_G, DllTypes.ForFileName("nvngx_dlssg.dll")?.AssetType);
        Assert.Equal(GameAssetType.DLSS_D, DllTypes.ForFileName("nvngx_dlssd.dll")?.AssetType);
        Assert.Equal(GameAssetType.DLSS_NR, DllTypes.ForFileName("nvngx_dlssnr.dll")?.AssetType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("d3d12.dll")]
    [InlineData("nvngx_dlss.dll.dlsss")]
    public void ForFileName_IgnoresAnythingElse(string? fileName)
    {
        Assert.Null(DllTypes.ForFileName(fileName));
    }

    /// <summary>Backups are not swappable, so they must not resolve to a definition.</summary>
    [Theory]
    [InlineData(GameAssetType.DLSS_BACKUP)]
    [InlineData(GameAssetType.XeSS_BACKUP)]
    [InlineData(GameAssetType.Unknown)]
    [InlineData(GameAssetType.Streamline_Interposer)]
    [InlineData(GameAssetType.DirectStorage)]
    public void ForAssetType_ReturnsNullForTypesWeDoNotSwap(GameAssetType assetType)
    {
        Assert.Null(DllTypes.ForAssetType(assetType));
    }

    [Fact]
    public void ForAssetType_RoundTripsEveryDefinition()
    {
        Assert.All(DllTypes.All, x => Assert.Same(x, DllTypes.ForAssetType(x.AssetType)));
    }

    [Fact]
    public void ForManifestKey_RoundTripsEveryDefinition()
    {
        Assert.All(DllTypes.All, x => Assert.Same(x, DllTypes.ForManifestKey(x.ManifestKey)));
    }

    [Fact]
    public void ForAssetTypeIncludingBackup_ResolvesBothSidesToTheSameDefinition()
    {
        Assert.All(DllTypes.All, x =>
        {
            Assert.Same(x, DllTypes.ForAssetTypeIncludingBackup(x.AssetType));
            Assert.Same(x, DllTypes.ForAssetTypeIncludingBackup(x.BackupAssetType));
        });
    }

    [Theory]
    [InlineData(GameAssetType.Unknown)]
    [InlineData(GameAssetType.Streamline_DLSS)]
    [InlineData(GameAssetType.DirectStorage_BACKUP)]
    public void ForAssetTypeIncludingBackup_StillReturnsNullForTypesWeDoNotSwap(GameAssetType assetType)
    {
        Assert.Null(DllTypes.ForAssetTypeIncludingBackup(assetType));
    }

    /// <summary>
    /// Used when clearing a game's cache to decide which files the app created and may delete. A
    /// swappable type answering true here would mean deleting a game's real dll.
    /// </summary>
    [Fact]
    public void IsBackupAssetType_SeparatesBackupsFromTheRealThing()
    {
        Assert.All(DllTypes.All, x =>
        {
            Assert.True(DllTypes.IsBackupAssetType(x.BackupAssetType));
            Assert.False(DllTypes.IsBackupAssetType(x.AssetType));
        });
    }

    [Theory]
    [InlineData(GameAssetType.Unknown)]
    [InlineData(GameAssetType.DirectStorage_BACKUP)]
    public void IsBackupAssetType_IsFalseForBackupsWeDoNotManage(GameAssetType assetType)
    {
        Assert.False(DllTypes.IsBackupAssetType(assetType));
    }

    [Theory]
    [InlineData(GameAssetType.DLSS, DllVendor.Nvidia)]
    [InlineData(GameAssetType.DLSS_G, DllVendor.Nvidia)]
    [InlineData(GameAssetType.DLSS_D, DllVendor.Nvidia)]
    [InlineData(GameAssetType.DLSS_NR, DllVendor.Nvidia)]
    [InlineData(GameAssetType.FSR_31_DX12, DllVendor.Amd)]
    [InlineData(GameAssetType.FSR_31_VK, DllVendor.Amd)]
    [InlineData(GameAssetType.XeSS, DllVendor.Intel)]
    [InlineData(GameAssetType.XeLL, DllVendor.Intel)]
    public void Vendors_AreAssignedCorrectly(GameAssetType assetType, DllVendor expected)
    {
        Assert.Equal(expected, DllTypes.ForAssetType(assetType)?.Vendor);
    }

    #endregion

    /// <summary>
    /// Checks the registry against the manifest the app actually ships, rather than against our own
    /// idea of what the keys are. The manifest is generated by a separate repository, so a key
    /// changing there is a real thing that can happen without this codebase noticing.
    /// </summary>
    /// <remarks>
    /// Only the types that claim to be manifest backed. A type marked
    /// <see cref="DllTypeDefinition.ExpectedInUpstreamManifest"/> false is one upstream has never
    /// published — its versions come from importing the file — and holding the shipped manifest to
    /// a key upstream does not know about would fail every sync.
    /// </remarks>
    [Fact]
    public void EveryManifestBackedKey_ExistsInTheShippedManifest()
    {
        using var document = ReadShippedManifest();

        var manifestKeys = document.RootElement
            .EnumerateObject()
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = DllTypes.All
            .Where(x => x.ExpectedInUpstreamManifest)
            .Select(x => x.ManifestKey)
            .Where(x => manifestKeys.Contains(x) == false)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The exemption is narrow on purpose: it is for types upstream genuinely does not publish, and
    /// every one of them must still be absent from the shipped manifest.
    /// </summary>
    /// <remarks>
    /// If a key shows up upstream after all, this fails and the flag comes off — which is the point.
    /// An exempt type that upstream does carry would be silently skipping the check that exists to
    /// notice a renamed key.
    /// </remarks>
    [Fact]
    public void ExemptKeys_AreGenuinelyAbsentFromTheShippedManifest()
    {
        using var document = ReadShippedManifest();

        var manifestKeys = document.RootElement
            .EnumerateObject()
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wronglyExempt = DllTypes.All
            .Where(x => x.ExpectedInUpstreamManifest == false)
            .Select(x => x.ManifestKey)
            .Where(manifestKeys.Contains)
            .ToList();

        Assert.True(
            wronglyExempt.Count == 0,
            $"The shipped manifest carries {string.Join(", ", wronglyExempt)}, so those types are no longer " +
            "fork-only and should have ExpectedInUpstreamManifest back on.");
    }

    /// <summary>
    /// The other direction, and the one that matters when upstream moves first: a key the manifest
    /// carries entries under that the registry has never heard of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing else reports this. <see cref="DllKeyedRecordsJson.ReadProperty"/> deliberately keeps
    /// an unrecognised key verbatim so that saving a user's imported manifest cannot corrupt it,
    /// which means a new upscaler arriving from the builder repository is carried silently and
    /// offered to nobody. It does not throw, it does not log, and the app looks exactly as it did.
    /// </para>
    /// <para>
    /// A key with nothing under it is upstream reserving a name it has not shipped for yet, so an
    /// empty list is not a failure — <c>directstorage</c> and <c>directstorage_core</c> have both
    /// sat empty since before this fork. The first entry to appear under one is the failure, and it
    /// is the moment a <see cref="DllTypes.All"/> row and its display name resource are needed.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryPopulatedManifestKey_IsHandledByTheRegistry()
    {
        using var document = ReadShippedManifest();

        var unhandled = PopulatedDllKeys(document.RootElement)
            .Where(x => DllTypes.ForManifestKey(x) is null)
            .ToList();

        Assert.True(
            unhandled.Count == 0,
            $"The shipped manifest has entries under {string.Join(", ", unhandled)}, which DllTypes.All has no row for. " +
            "Everything under those keys is invisible in the app until one is added.");
    }

    /// <summary>
    /// Every manifest key that has something under it, from the records at the top level and from
    /// the known dll hashes, which are keyed the same way.
    /// </summary>
    /// <remarks>
    /// Both are read, because a type can appear in one before the other: the hashes of a dll games
    /// ship with are knowable before there is anything to download.
    /// </remarks>
    static IEnumerable<string> PopulatedDllKeys(JsonElement manifest)
    {
        foreach (var property in manifest.EnumerateObject())
        {
            if (property.NameEquals(KnownDllsKey))
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var knownDll in property.Value.EnumerateObject())
                {
                    if (HasEntries(knownDll.Value))
                    {
                        yield return knownDll.Name;
                    }
                }

                continue;
            }

            if (HasEntries(property.Value))
            {
                yield return property.Name;
            }
        }
    }

    static bool HasEntries(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;
    }

    /// <summary>
    /// The manifest the app ships, linked into this project's output by the csproj. Both manifest
    /// backed tests read it, so where it comes from is stated once.
    /// </summary>
    static JsonDocument ReadShippedManifest()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "static_manifest.json");
        Assert.True(File.Exists(manifestPath), $"Expected the shipped manifest at {manifestPath}.");

        return JsonDocument.Parse(File.ReadAllText(manifestPath));
    }

    /// <summary>The manifest's one property that is not a dll type.</summary>
    const string KnownDllsKey = "known_dlls";
}
