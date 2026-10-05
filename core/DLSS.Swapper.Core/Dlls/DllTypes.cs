using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DLSS_Swapper.Data;

namespace DLSS_Swapper.Dlls;

/// <summary>
/// The swappable dll types the app knows about.
/// </summary>
/// <remarks>
/// <para>
/// Adding support for a new upscaler should be a single entry in <see cref="All"/> plus whatever
/// the manifest builder needs on its side.
/// </para>
/// <para>
/// <see cref="GameAssetType"/> contains many more values than appear here. Only these are ones a
/// user can swap; the rest are backup counterparts or types the app records but does not offer.
/// </para>
/// </remarks>
public static class DllTypes
{
    public static ImmutableArray<DllTypeDefinition> All { get; } =
    [
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.DLSS,
            BackupAssetType = GameAssetType.DLSS_BACKUP,
            FileName = "nvngx_dlss.dll",
            ManifestKey = "dlss",
            Vendor = DllVendor.Nvidia,
            DisplayNameResourceKey = "General_Name_DLSS",
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.DLSS_G,
            BackupAssetType = GameAssetType.DLSS_G_BACKUP,
            FileName = "nvngx_dlssg.dll",
            ManifestKey = "dlss_g",
            Vendor = DllVendor.Nvidia,
            DisplayNameResourceKey = "General_Name_DLSS_G",
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.DLSS_D,
            BackupAssetType = GameAssetType.DLSS_D_BACKUP,
            FileName = "nvngx_dlssd.dll",
            ManifestKey = "dlss_d",
            Vendor = DllVendor.Nvidia,
            DisplayNameResourceKey = "General_Name_DLSS_D",
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.DLSS_NR,
            BackupAssetType = GameAssetType.DLSS_NR_BACKUP,
            FileName = "nvngx_dlssnr.dll",
            ManifestKey = "dlss_nr",
            Vendor = DllVendor.Nvidia,
            DisplayNameResourceKey = "General_Name_DLSS_NR",

            // Treated as shipped by no game, because in practice the copies in game folders were put
            // there by mods. NBA 2K27 does ship 310.8.0.0, and 3.0.7.0 briefly treated NR as shipped
            // on that strength. On a real library every copy turned out to be modded in - genuine
            // NVIDIA files copied in by hand, patched ones, and ~230 KB unsigned stubs - so saving
            // "originals" recorded each mod as what the game came with, at ~160 MB a copy, for Restore
            // to put back. Nothing in the file says whether a game shipped it: a genuine signature
            // is just as true of one copied in. Somebody who owns a game that does ship it can get
            // the file back by verifying the game through its store.
            GamesShipThisDll = false,

            // Not in any upstream manifest: NVIDIA has not published it as a download - the DLSS SDK
            // (310.9.1) and Streamline SDK (2.14.1) packages carry no nvngx_dlssnr.dll - so there is
            // nothing to download and its versions arrive only by importing the file. The manifest
            // key is still named, so if the builder ever does publish one it lands here rather than
            // in an unrecognised bucket nothing reads.
            ExpectedInUpstreamManifest = false,
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.FSR_31_DX12,
            BackupAssetType = GameAssetType.FSR_31_DX12_BACKUP,
            FileName = "amd_fidelityfx_dx12.dll",
            ManifestKey = "fsr_31_dx12",
            Vendor = DllVendor.Amd,
            VersionFromInternalName = true,
            DisplayNameResourceKey = "General_Name_FSR_31_DX12",
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.FSR_31_VK,
            BackupAssetType = GameAssetType.FSR_31_VK_BACKUP,
            FileName = "amd_fidelityfx_vk.dll",
            ManifestKey = "fsr_31_vk",
            Vendor = DllVendor.Amd,
            VersionFromInternalName = true,
            DisplayNameResourceKey = "General_Name_FSR_31_VK",
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.XeSS,
            BackupAssetType = GameAssetType.XeSS_BACKUP,
            FileName = "libxess.dll",
            ManifestKey = "xess",
            Vendor = DllVendor.Intel,
            DisplayNameResourceKey = "General_Name_XeSS",
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.XeSS_FG,
            BackupAssetType = GameAssetType.XeSS_FG_BACKUP,
            FileName = "libxess_fg.dll",
            ManifestKey = "xess_fg",
            Vendor = DllVendor.Intel,
            DisplayNameResourceKey = "General_Name_XeSS_FG",
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.XeSS_DX11,
            BackupAssetType = GameAssetType.XeSS_DX11_BACKUP,
            FileName = "libxess_dx11.dll",
            ManifestKey = "xess_dx11",
            Vendor = DllVendor.Intel,
            DisplayNameResourceKey = "General_Name_XeSS_DX11",
        },
        new DllTypeDefinition()
        {
            AssetType = GameAssetType.XeLL,
            BackupAssetType = GameAssetType.XeLL_BACKUP,
            FileName = "libxell.dll",
            ManifestKey = "xell",
            Vendor = DllVendor.Intel,
            DisplayNameResourceKey = "General_Name_XeLL",
        },
    ];

    static readonly Dictionary<GameAssetType, DllTypeDefinition> _byAssetType =
        All.ToDictionary(x => x.AssetType);

    // Game directories are scanned case insensitively, matching how Windows treats them.
    static readonly Dictionary<string, DllTypeDefinition> _byFileName =
        All.ToDictionary(x => x.FileName, System.StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<string, DllTypeDefinition> _byManifestKey =
        All.ToDictionary(x => x.ManifestKey, System.StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<GameAssetType, DllTypeDefinition> _byBackupAssetType =
        All.ToDictionary(x => x.BackupAssetType);

    /// <summary>The definition for an asset type, or null if it is not a swappable one.</summary>
    public static DllTypeDefinition? ForAssetType(GameAssetType assetType)
    {
        return _byAssetType.TryGetValue(assetType, out var definition) ? definition : null;
    }

    /// <summary>
    /// The definition for an asset type or for its backup counterpart.
    /// </summary>
    /// <remarks>
    /// Code that treats a dll and its ".dlsss" backup as the same thing, such as deciding whether a
    /// file is one the app recognises, needs both to land on the same definition.
    /// </remarks>
    public static DllTypeDefinition? ForAssetTypeIncludingBackup(GameAssetType assetType)
    {
        if (_byAssetType.TryGetValue(assetType, out var definition))
        {
            return definition;
        }

        return _byBackupAssetType.TryGetValue(assetType, out var backupDefinition) ? backupDefinition : null;
    }

    /// <summary>True when this is the backup counterpart of a swappable type.</summary>
    public static bool IsBackupAssetType(GameAssetType assetType) => _byBackupAssetType.ContainsKey(assetType);

    /// <summary>The definition for a dll file name, or null if it is not one we swap.</summary>
    public static DllTypeDefinition? ForFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return _byFileName.TryGetValue(fileName, out var definition) ? definition : null;
    }

    /// <summary>The definition for a manifest key, or null if the manifest carries something we don't handle.</summary>
    public static DllTypeDefinition? ForManifestKey(string? manifestKey)
    {
        if (string.IsNullOrWhiteSpace(manifestKey))
        {
            return null;
        }

        return _byManifestKey.TryGetValue(manifestKey, out var definition) ? definition : null;
    }
}
