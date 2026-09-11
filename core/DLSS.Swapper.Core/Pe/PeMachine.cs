using System;
using System.IO;

namespace DLSS_Swapper.Pe;

/// <summary>
/// The processor architecture a PE file was built for, as its header declares it.
/// </summary>
public enum PeMachine
{
    /// <summary>Not a PE file, too short to say, or a header that declares no machine.</summary>
    Unknown,

    /// <summary>32-bit x86 (IMAGE_FILE_MACHINE_I386).</summary>
    X86,

    /// <summary>64-bit x64 (IMAGE_FILE_MACHINE_AMD64).</summary>
    X64,

    /// <summary>64-bit ARM (IMAGE_FILE_MACHINE_ARM64).</summary>
    Arm64,

    /// <summary>32-bit ARM, Thumb or not (IMAGE_FILE_MACHINE_ARM / ARMNT / THUMB).</summary>
    Arm,

    /// <summary>A machine type this app has no name for. Still a real value, and still compared.</summary>
    Other,
}

/// <summary>
/// Reads the machine field of a PE header.
/// </summary>
/// <remarks>
/// <para>
/// Every dll the app swaps is a PE file, and the game that loads it is one too. A 64-bit game
/// cannot load a 32-bit dll, or the other way round; Windows refuses at load time with an error the
/// game usually turns into a crash on startup, well after the swap looked like a success. The check
/// is two bytes at a known offset, so it is made before anything is written.
/// </para>
/// <para>
/// Pure and header-only, like <see cref="PeSignatureTable"/>, so it lives in the core library and is
/// exercised without a real dll.
/// </para>
/// </remarks>
public static class PeHeader
{
    const int HeaderBytesToRead = 4096;

    /// <summary>The machine the file at <paramref name="path"/> was built for. <see cref="PeMachine.Unknown"/> for a file that cannot be read.</summary>
    public static PeMachine ReadMachine(string path)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return ReadMachine(stream);
            }
        }
        catch (IOException)
        {
            return PeMachine.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return PeMachine.Unknown;
        }
    }

    public static PeMachine ReadMachine(Stream stream)
    {
        if (stream.CanSeek == false)
        {
            return PeMachine.Unknown;
        }

        var toRead = (int)Math.Min(stream.Length, HeaderBytesToRead);
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

        return ReadMachine(header.AsSpan(0, read));
    }

    /// <summary>
    /// The rule itself, over the first bytes of the file.
    /// </summary>
    public static PeMachine ReadMachine(ReadOnlySpan<byte> header)
    {
        // The DOS header is 0x40 bytes and ends with e_lfanew, the offset of the PE signature.
        if (header.Length < 0x40 || header[0] != 0x4D || header[1] != 0x5A)
        {
            return PeMachine.Unknown;
        }

        var peOffset = header[0x3C] | (header[0x3D] << 8) | (header[0x3E] << 16) | (header[0x3F] << 24);

        // "PE\0\0", then IMAGE_FILE_HEADER, whose first field is Machine.
        if (peOffset < 0 || peOffset + 6 > header.Length)
        {
            return PeMachine.Unknown;
        }

        if (header[peOffset] != 0x50 || header[peOffset + 1] != 0x45 || header[peOffset + 2] != 0 || header[peOffset + 3] != 0)
        {
            return PeMachine.Unknown;
        }

        var machine = (ushort)(header[peOffset + 4] | (header[peOffset + 5] << 8));

        switch (machine)
        {
            case 0x0000:
                return PeMachine.Unknown;
            case 0x014C:
                return PeMachine.X86;
            case 0x8664:
                return PeMachine.X64;
            case 0xAA64:
                return PeMachine.Arm64;
            case 0x01C0:
            case 0x01C2:
            case 0x01C4:
                return PeMachine.Arm;
            default:
                return PeMachine.Other;
        }
    }

    /// <summary>A short name for logs and messages. Not localised: it names a processor, not a sentence.</summary>
    public static string Describe(PeMachine machine)
    {
        switch (machine)
        {
            case PeMachine.X86:
                return "32-bit x86";
            case PeMachine.X64:
                return "64-bit x64";
            case PeMachine.Arm64:
                return "ARM64";
            case PeMachine.Arm:
                return "32-bit ARM";
            case PeMachine.Other:
                return "another architecture";
            default:
                return "an unknown architecture";
        }
    }
}
