using System;
using System.IO;

namespace DLSS_Swapper.Pe;

/// <summary>
/// What a PE file's own header says about its Authenticode signature.
/// </summary>
public enum PeSignatureTableState
{
    /// <summary>Not a PE file at all - no MZ/PE signature where one should be.</summary>
    NotPe,

    /// <summary>Too short or too damaged to read the header that would say.</summary>
    Unreadable,

    /// <summary>The header declares no certificate table. The file was never signed, or was rebuilt without one.</summary>
    NoTable,

    /// <summary>The header declares a certificate table and the file holds it. Whether it verifies is WinTrust's question.</summary>
    Present,

    /// <summary>The header declares a certificate table that runs past the end of the file. The signature was cut off.</summary>
    Truncated,
}

/// <summary>
/// Reads the one thing WinVerifyTrust cannot tell you: whether a file that fails to verify ever had
/// a signature, and if so whether it is still there.
/// </summary>
/// <remarks>
/// <para>
/// WinVerifyTrust reports a missing signature and a damaged one with the same code, and the app
/// used to log both as "an unknown error occurred trying to verify the signature". For one dll
/// that turned out to mean: the PE header pointed at a 10,352 byte signature beginning at exactly
/// the file's length. Zero bytes of it were present. The file had been altered after NVIDIA
/// signed it and the signature stripped off the end - and the message said nothing anyone could
/// act on.
/// </para>
/// <para>
/// The certificate table is data directory entry 4. Unlike every other directory its first field
/// is a file offset rather than a virtual address, which is what makes this check possible with
/// nothing but the header and the file length.
/// </para>
/// <para>
/// Pure and header-only, so it lives in the core library and can be exercised without a real dll.
/// </para>
/// </remarks>
public static class PeSignatureTable
{
    const int HeaderBytesToRead = 4096;

    public static PeSignatureTableState Inspect(string path)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return Inspect(stream);
            }
        }
        catch (IOException)
        {
            return PeSignatureTableState.Unreadable;
        }
        catch (UnauthorizedAccessException)
        {
            return PeSignatureTableState.Unreadable;
        }
    }

    public static PeSignatureTableState Inspect(Stream stream)
    {
        if (stream.CanSeek == false)
        {
            return PeSignatureTableState.Unreadable;
        }

        var length = stream.Length;
        var toRead = (int)Math.Min(length, HeaderBytesToRead);
        var header = new byte[toRead];

        stream.Position = 0;
        var read = 0;
        while (read < toRead)
        {
            var got = stream.Read(header, read, toRead - read);
            if (got <= 0)
            {
                break;
            }
            read += got;
        }

        return Inspect(header.AsSpan(0, read), length);
    }

    /// <summary>
    /// The rule itself, over the first few kilobytes and the total length.
    /// </summary>
    public static PeSignatureTableState Inspect(ReadOnlySpan<byte> header, long fileLength)
    {
        // Not enough to say anything at all.
        if (header.Length < 2)
        {
            return PeSignatureTableState.Unreadable;
        }

        // "MZ" - NotPe is for bytes that say so, not for a file too short to have said either way.
        if (header[0] != 0x4D || header[1] != 0x5A)
        {
            return PeSignatureTableState.NotPe;
        }

        if (header.Length < 0x40)
        {
            return PeSignatureTableState.Unreadable;
        }

        var peOffset = ReadInt32(header, 0x3C);
        if (peOffset < 0 || peOffset + 24 > header.Length)
        {
            return PeSignatureTableState.Unreadable;
        }

        // "PE\0\0"
        if (header[peOffset] != 0x50 || header[peOffset + 1] != 0x45 || header[peOffset + 2] != 0 || header[peOffset + 3] != 0)
        {
            return PeSignatureTableState.NotPe;
        }

        var optionalHeader = peOffset + 24;
        if (optionalHeader + 2 > header.Length)
        {
            return PeSignatureTableState.Unreadable;
        }

        var magic = ReadUInt16(header, optionalHeader);
        int numberOfRvaAndSizesOffset;
        int dataDirectoriesOffset;
        switch (magic)
        {
            case 0x10B: // PE32
                numberOfRvaAndSizesOffset = optionalHeader + 92;
                dataDirectoriesOffset = optionalHeader + 96;
                break;
            case 0x20B: // PE32+
                numberOfRvaAndSizesOffset = optionalHeader + 108;
                dataDirectoriesOffset = optionalHeader + 112;
                break;
            default:
                return PeSignatureTableState.NotPe;
        }

        if (numberOfRvaAndSizesOffset + 4 > header.Length)
        {
            return PeSignatureTableState.Unreadable;
        }

        // The certificate table is entry 4. A header may legitimately declare fewer entries.
        var numberOfRvaAndSizes = ReadInt32(header, numberOfRvaAndSizesOffset);
        if (numberOfRvaAndSizes < 5)
        {
            return PeSignatureTableState.NoTable;
        }

        var certificateEntry = dataDirectoriesOffset + 4 * 8;
        if (certificateEntry + 8 > header.Length)
        {
            return PeSignatureTableState.Unreadable;
        }

        var certificateOffset = (uint)ReadInt32(header, certificateEntry);
        var certificateSize = (uint)ReadInt32(header, certificateEntry + 4);

        if (certificateSize == 0)
        {
            return PeSignatureTableState.NoTable;
        }

        var end = (long)certificateOffset + certificateSize;
        return end <= fileLength ? PeSignatureTableState.Present : PeSignatureTableState.Truncated;
    }

    static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        return bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24);
    }

    static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
    {
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }
}
