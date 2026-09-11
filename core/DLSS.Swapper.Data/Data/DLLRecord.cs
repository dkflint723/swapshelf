using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DLSS_Swapper.Dlls;
using DLSS_Swapper.Extensions;
using DLSS_Swapper.Helpers;
using DLSS_Swapper.Helpers.FSR31;
using DLSS_Swapper.Swapping;

namespace DLSS_Swapper.Data;

public class DLLRecord : IComparable<DLLRecord>, INotifyPropertyChanged
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("version_number")]
    public ulong VersionNumber { get; set; }

    [JsonPropertyName("internal_name")]
    public string InternalName { get; set; } = string.Empty;

    [JsonPropertyName("internal_name_extra")]
    public string InternalNameExtra { get; set; } = string.Empty;

    [JsonPropertyName("additional_label")]
    public string AdditionalLabel { get; set; } = string.Empty;

    [JsonPropertyName("md5_hash")]
    public string MD5Hash { get; set; } = string.Empty;

    /// <summary>
    /// This hash is not guaranteed to be the same as the hash on the zip on the disk.
    /// It is used during download to validate a successful download. However if you
    /// import a DLL that exists in the manifest we will then create the zip for that
    /// file. Doing so will cause the new generateed zip hash and this entry in the
    /// manifest to differ.
    /// </summary>
    [JsonPropertyName("zip_md5_hash")]
    public string ZipMD5Hash { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the dll, upper-case hex. Empty for a manifest entry, which carries none; filled in
    /// on import, and on the first download or swap of a manifest entry - when it is also remembered
    /// locally, so it is still here after the next manifest load.
    /// </summary>
    [JsonPropertyName("sha256_hash")]
    public string Sha256Hash { get; set; } = string.Empty;

    /// <summary>True when this record has no SHA-256 to compare, or <paramref name="actualSha256"/> is it.</summary>
    internal bool MatchesSha256(string actualSha256)
    {
        return string.IsNullOrEmpty(Sha256Hash) || FileHashes.HexEquals(Sha256Hash, actualSha256);
    }

    /// <summary>Records the SHA-256 of this record's file the first time it is seen, here and in the local store.</summary>
    internal void RememberSha256(string actualSha256)
    {
        if (string.IsNullOrEmpty(Sha256Hash) == false || string.IsNullOrEmpty(actualSha256))
        {
            return;
        }

        Sha256Hash = actualSha256;
        LocalDigestStore.Remember(MD5Hash, actualSha256);
    }

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("file_description")]
    public string FileDescription { get; set; } = string.Empty;

    [JsonPropertyName("signed_datetime")]
    public DateTime SignedDateTime { get; set; } = DateTime.MinValue;

    [JsonPropertyName("is_signature_valid")]
    public bool IsSignatureValid { get; set; }

    [JsonPropertyName("is_dev_file")]
    public bool IsDevFile { get; set; } = false;

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("zip_file_size")]
    public long ZipFileSize { get; set; }

    [JsonIgnore]
    public string FullName
    {
        get
        {
            if (string.IsNullOrEmpty(AdditionalLabel))
            {
                return Version;
            }

            return $"{Version} - {AdditionalLabel}";
        }
    }

    /// <summary>
    /// The fork's curated sentence about this exact build, or null for the many versions with
    /// nothing to say. Stamped by DLLManager from recommended.json whenever a manifest loads.
    /// </summary>
    [JsonIgnore]
    public string? RecommendationNote { get; set; }

    [JsonIgnore]
    public bool IsRecommended => RecommendationNote is not null;

    string _displayVersion = string.Empty;
    [JsonIgnore]
    public string DisplayVersion
    {
        get
        {
            // return cached version.
            if (string.IsNullOrWhiteSpace(_displayVersion) == false)
            {
                return _displayVersion;
            }

            // If FSR the display version is the internal version
            if (AssetType == GameAssetType.FSR_31_DX12 || AssetType == GameAssetType.FSR_31_VK ||
                AssetType == GameAssetType.FSR_31_DX12_BACKUP || AssetType == GameAssetType.FSR_31_VK_BACKUP)
            {
                if (string.IsNullOrEmpty(InternalName) == false)
                {
                    _displayVersion = InternalName;
                    return _displayVersion;
                }

                if (LocalRecord is not null)
                {
                    var latestVersion = FSR31Helper.GetLatestVersion(LocalRecord.ExpectedPath);
                    if (string.IsNullOrWhiteSpace(latestVersion) == false)
                    {
                        _displayVersion = latestVersion;
                        return _displayVersion;
                    }
                }

                // If this isn't loaded we fall back to the existing stuff.
            }


            var version = Version.AsSpan();

            // Remove all the .0's, such that 2.5.0.0 becomes 2.5
            while (version.EndsWith(".0"))
            {
                version = version.Slice(0, version.Length - 2);
            }

            _displayVersion = version.ToString();

            // If the value is a single value, eg 1, make it 1.0
            if (_displayVersion.Length == 1)
            {
                _displayVersion = $"{_displayVersion}.0";
            }

            return _displayVersion;
        }
    }

    Version? _displayVersionVersion;
    [JsonIgnore]
    public Version DisplayVersionVersion
    {
        get
        {
            if (_displayVersionVersion is not null)
            {
                return _displayVersionVersion;
            }

            try
            {
                var version = new Version(DisplayVersion);
                _displayVersionVersion = version;
                return version;
            }
            catch (Exception err)
            {
                Logger.Error(err, $"Failed to parse display version ({DisplayVersion}) into a Version object.");
                return new Version(0, 0, 0, 0);
            }
        }
    }


    /// <summary>
    /// Returns the display version (eg 2.5.0.0 slimmed down to 2.5) and prefixes with v, and suffix with additional label if it exists.
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var devString = IsDevFile ? " (Debug)" : string.Empty;


            if (AssetType == GameAssetType.FSR_31_DX12 || AssetType == GameAssetType.FSR_31_VK ||
                AssetType == GameAssetType.FSR_31_DX12_BACKUP || AssetType == GameAssetType.FSR_31_VK_BACKUP)
            {
                    return $"v{DisplayVersion}{devString} (v{Version})";
            }

            if (string.IsNullOrEmpty(AdditionalLabel))
            {
                return $"v{DisplayVersion}{devString}";
            }

            return $"v{DisplayVersion}{devString} ({AdditionalLabel})";
        }
    }

    LocalRecord? _localRecord;

    [JsonIgnore]
    public LocalRecord? LocalRecord
    {
        get => _localRecord;
        set
        {
            _localRecord = value;
            NotifyPropertyChanged();
        }
    }

    [JsonIgnore]
    public GameAssetType AssetType { get; set; } = GameAssetType.Unknown;

    int _gamesUsingCount;

    /// <summary>
    /// How many games have this exact dll in place, as of the last time anything changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held rather than worked out on demand, because the row cannot ask the question itself. The
    /// upscalers page used to bind straight to a function over this record, and an <c>x:Bind</c> to
    /// a function re-evaluates when its ARGUMENTS change - the asset type, the hash, the version.
    /// None of those change when a dll is swapped into a game. What changed was the games list,
    /// which was never an argument, so the count was a snapshot taken the moment the row was first
    /// drawn and then kept for the life of the page.
    /// </para>
    /// <para>
    /// The effect looked like a bug in imported dlls, because those are the ones somebody imports
    /// and then immediately swaps: the row is drawn while the file is in no games at all, reads
    /// "Not used" correctly, and never says anything else. Downloaded dlls have the same problem
    /// and hide it, because most of two hundred rows really are unused and "Not used" stays right
    /// by accident.
    /// </para>
    /// <para>
    /// A plain property with a notification moves the question to where the answer changes:
    /// <see cref="DLLManager.RefreshGamesUsingCounts"/> sets it whenever the games do.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public int GamesUsingCount
    {
        get => _gamesUsingCount;
        set
        {
            if (_gamesUsingCount == value)
            {
                return;
            }

            _gamesUsingCount = value;
            NotifyPropertyChanged();
        }
    }

    [JsonIgnore]
    public DLLRecordModelTranslationProperties TranslationProperties { get; } = new DLLRecordModelTranslationProperties();

    public int CompareTo(DLLRecord? other)
    {
        if (other is null)
        {
            return -1;
        }

        if (string.IsNullOrWhiteSpace(MD5Hash) == false && MD5Hash == other.MD5Hash)
        {
            return 0;
        }

        if ((AssetType == GameAssetType.FSR_31_DX12 && other.AssetType == GameAssetType.FSR_31_DX12) ||
            (AssetType == GameAssetType.FSR_31_VK && other.AssetType == GameAssetType.FSR_31_VK))
        {
            if (string.IsNullOrEmpty(InternalName) == false && string.IsNullOrEmpty(other.InternalName) == false)
            {
                if (InternalName != other.InternalName)
                {
                    return other.InternalName.CompareTo(InternalName);
                }
            }
        }

        if (VersionNumber == other.VersionNumber)
        {
            if (IsDevFile == other.IsDevFile)
            {
                return other.AdditionalLabel.CompareTo(AdditionalLabel);
            }

            return IsDevFile.CompareTo(other.IsDevFile);
        }

        return other.VersionNumber.CompareTo(VersionNumber);
    }

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion

    private CancellationTokenSource? _cancellationTokenSource;

    internal void CancelDownload()
    {
        var cancellation = _cancellationTokenSource;
        _cancellationTokenSource = null;

        cancellation?.Cancel();

        // A linked source registers a callback on the token it was linked to, and that registration
        // outlives the download until this is called. Downloading a hundred dlls in one run left a
        // hundred of them attached to the caller's token.
        cancellation?.Dispose();
    }

    /// <summary>
    /// Downloads this dll.
    /// </summary>
    /// <param name="callerCancellationToken">
    /// Lets a caller stop the download. Without this a bulk run could only stop between dlls, so
    /// cancelling during a download of tens of megabytes appeared to do nothing until it finished.
    /// </param>
    internal async Task<(bool Success, string Message, bool Cancelled)> DownloadAsync(CancellationToken callerCancellationToken = default)
    {
        if (string.IsNullOrEmpty(DownloadUrl))
        {
            return (false, "Invalid download URL.", false);
        }

        if (LocalRecord is null)
        {
            return (false, "Local record is null.", false);
        }

        // Through CancelDownload rather than cancelling in place, so the one it replaces is
        // disposed rather than dropped.
        CancelDownload();

        // Linked so the existing per record cancel button still works alongside the caller's.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        _cancellationTokenSource = cancellation;
        var cancellationToken = cancellation.Token;

        var fileDownloader = new FileDownloader(DownloadUrl);
        var tempZipFile = Path.Combine(Storage.GetTemp(), $"{fileDownloader.Guid.ToString("D").ToUpper()}.zip");

        try
        {
            LocalRecord.FileDownloader = fileDownloader;
            NotifyPropertyChanged(nameof(LocalRecord));


            using (var fileStream = new FileStream(tempZipFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileDownloader.BufferSize, true))
            {
                var didDownload = await LocalRecord.FileDownloader.DownloadFileToStreamAsync(fileStream, cancellationToken).ConfigureAwait(false);

                if (didDownload == false)
                {
                    throw new Exception("Could not download file.");
                }

                if (ZipMD5Hash != fileStream.GetMD5Hash())
                {
                    throw new Exception("Downloaded file was invalid.");
                }

                fileStream.Position = 0;

                using (var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Read, true))
                {
                    DLLManager.HandleExtractFromZip(zipArchive, this);

                    // The zip matched the manifest; now the dll inside it has to. Extraction used
                    // to be taken on trust, so a wrong dll in a right zip was never noticed here.
                    VerifyExtractedDll();
                }
            }

            UiThread.Run(() =>
            {
                LocalRecord.IsDownloaded = true;
                NotifyPropertyChanged(nameof(LocalRecord));
            });

            return (true, string.Empty, false);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UiThread.Run(() =>
            {
                LocalRecord.IsDownloaded = false;
                NotifyPropertyChanged(nameof(LocalRecord));
            });

            return (false, string.Empty, true);
        }
        catch (Exception err)
        {
            Logger.Error(err);

            Debugger.Break();
            UiThread.Run(() =>
            {
                LocalRecord.IsDownloaded = false;
                LocalRecord.HasDownloadError = true;
                LocalRecord.DownloadErrorMessage = ResourceHelper.GetFormattedResourceTemplate("DllRecord_CouldNotDownloadAssetTypeTemplate", DLLManager.Instance.GetAssetTypeName(AssetType));
                NotifyPropertyChanged(nameof(LocalRecord));
            });

            return (false, err.Message, false);
        }
        finally
        {
            UiThread.Run(() =>
            {
                LocalRecord.FileDownloader = null;
                NotifyPropertyChanged(nameof(LocalRecord));
            });

            // Remove temp file.
            try
            {
                File.Delete(tempZipFile);
            }
            catch (Exception)
            {
                // NOOP
            }

            // Only when it is still ours. A second download started while this one was finishing
            // has already replaced the field, and disposing it here would cancel that one instead.
            if (ReferenceEquals(_cancellationTokenSource, cancellation))
            {
                _cancellationTokenSource = null;
            }

            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Checks the dll just extracted against this record - MD5 always, SHA-256 when the record has
    /// one - and learns the SHA-256 when it does not. A file that does not match is deleted and
    /// the download reported as invalid, so nothing unverified stays in the library.
    /// </summary>
    void VerifyExtractedDll()
    {
        if (LocalRecord is null)
        {
            return;
        }

        FileDigests digests;
        using (var stream = new FileStream(LocalRecord.ExpectedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            digests = FileHashes.Compute(stream);
        }

        if (FileHashes.HexEquals(MD5Hash, digests.Md5) == false || MatchesSha256(digests.Sha256) == false)
        {
            try
            {
                File.Delete(LocalRecord.ExpectedPath);
            }
            catch (Exception err)
            {
                Logger.Warning($"Could not remove the invalid download at {LocalRecord.ExpectedPath}: {err.Message}");
            }

            throw new Exception("Downloaded file was invalid.");
        }

        RememberSha256(digests.Sha256);
    }

    internal string GetRecordSimpleType()
    {
        return DllTypes.ForAssetType(AssetType)?.ManifestKey ?? string.Empty;
    }

    internal void CopyFrom(DLLRecord newDllRecord)
    {
        Version = newDllRecord.Version;
        VersionNumber = newDllRecord.VersionNumber;
        InternalName = newDllRecord.InternalName;
        AdditionalLabel = newDllRecord.AdditionalLabel;
        MD5Hash = newDllRecord.MD5Hash;
        ZipMD5Hash = newDllRecord.ZipMD5Hash;
        // A manifest reload carries no SHA-256; one already learned is not forgotten for it.
        Sha256Hash = string.IsNullOrEmpty(newDllRecord.Sha256Hash) ? Sha256Hash : newDllRecord.Sha256Hash;
        DownloadUrl = newDllRecord.DownloadUrl;
        FileDescription = newDllRecord.FileDescription;
        SignedDateTime = newDllRecord.SignedDateTime;
        IsSignatureValid = newDllRecord.IsSignatureValid;
        IsDevFile = newDllRecord.IsDevFile;
        FileSize = newDllRecord.FileSize;
        ZipFileSize = newDllRecord.ZipFileSize;
        LocalRecord = newDllRecord.LocalRecord;
        AssetType = newDllRecord.AssetType;

        NotifyPropertyChanged(nameof(FullName));
        _displayVersion = string.Empty;
        NotifyPropertyChanged(nameof(DisplayVersion));
        NotifyPropertyChanged(nameof(DisplayName));
    }

}
