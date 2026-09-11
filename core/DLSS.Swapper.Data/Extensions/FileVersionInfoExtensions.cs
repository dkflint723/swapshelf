using System;
using System.Diagnostics;
using System.IO;
using DLSS_Swapper.Swapping;

namespace DLSS_Swapper.Extensions;

internal static class FileVersionInfoExtensions
{
    internal static string GetMD5Hash(this FileVersionInfo fileVersionInfo)
    {
        try
        {
            using (var fileStream = File.OpenRead(fileVersionInfo.FileName))
            {
                return fileStream.GetMD5Hash();
            }
        }
        catch (Exception err)
        {
            Logger.Error(err, $"{fileVersionInfo.FileName}");
            Debugger.Break();
        }

        return string.Empty;
    }

    /// <summary>Both digests of the file, from one read. Empty strings when it could not be read.</summary>
    internal static FileDigests GetDigests(this FileVersionInfo fileVersionInfo)
    {
        try
        {
            using (var fileStream = File.OpenRead(fileVersionInfo.FileName))
            {
                return FileHashes.Compute(fileStream);
            }
        }
        catch (Exception err)
        {
            Logger.Error(err, $"{fileVersionInfo.FileName}");
        }

        return new FileDigests(string.Empty, string.Empty);
    }

    internal static string GetFormattedFileVersion(this FileVersionInfo fileVersionInfo)
    {
        return $"{fileVersionInfo.FileMajorPart}.{fileVersionInfo.FileMinorPart}.{fileVersionInfo.FileBuildPart}.{fileVersionInfo.FilePrivatePart}";
    }

    internal static ulong GetFileVersionNumber(this FileVersionInfo fileVersionInfo)
    {
        return ((ulong)fileVersionInfo.FileMajorPart << 48) +
                ((ulong)fileVersionInfo.FileMinorPart << 32) +
                ((ulong)fileVersionInfo.FileBuildPart << 16) +
                ((ulong)fileVersionInfo.FilePrivatePart);
    }
}
