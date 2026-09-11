using System;
using System.IO;
using DLSS_Swapper.Pe;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// Reading a PE header for whether its signature is missing, present, or cut off.
/// </summary>
/// <remarks>
/// <para>
/// Built from synthetic headers rather than real dlls, because the case that matters - a
/// certificate table pointing past the end of the file - is exactly the case no shipped binary
/// exhibits, and a test that needs a tampered NVIDIA dll on disk is not a test anybody will run.
/// </para>
/// <para>
/// The layout is the documented one: MZ at 0, e_lfanew at 0x3C, "PE\0\0" at e_lfanew, the optional
/// header 24 bytes later, its magic deciding where the data directories start, and the certificate
/// table as the fifth directory entry holding a FILE OFFSET and a size.
/// </para>
/// </remarks>
public class PeSignatureTableTests
{
    const int PeOffset = 0x80;

    /// <summary>A minimal header in a buffer of the given total length.</summary>
    static byte[] Build(long totalLength, bool pe32Plus, uint certificateOffset, uint certificateSize, int numberOfDirectories = 16)
    {
        var bytes = new byte[totalLength];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Write(bytes, 0x3C, PeOffset);

        bytes[PeOffset] = (byte)'P';
        bytes[PeOffset + 1] = (byte)'E';

        var optional = PeOffset + 24;
        Write(bytes, optional, pe32Plus ? (ushort)0x20B : (ushort)0x10B);

        var countOffset = optional + (pe32Plus ? 108 : 92);
        var directories = optional + (pe32Plus ? 112 : 96);
        Write(bytes, countOffset, numberOfDirectories);

        var certificateEntry = directories + 4 * 8;
        Write(bytes, certificateEntry, (int)certificateOffset);
        Write(bytes, certificateEntry + 4, (int)certificateSize);
        return bytes;
    }

    static void Write(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    static void Write(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    static PeSignatureTableState InspectBytes(byte[] bytes)
    {
        using (var stream = new MemoryStream(bytes))
        {
            return PeSignatureTable.Inspect(stream);
        }
    }

    [Fact]
    public void ASignatureThatEndsExactlyAtTheEndOfTheFileIsPresent()
    {
        // The normal shape of a signed file: the certificate table is the last thing in it.
        var bytes = Build(totalLength: 10_000, pe32Plus: true, certificateOffset: 8_000, certificateSize: 2_000);

        Assert.Equal(PeSignatureTableState.Present, InspectBytes(bytes));
    }

    [Fact]
    public void ATableThatBeginsWhereTheFileEndsIsTruncated()
    {
        // The case this exists for. A real dll declared a 10,352 byte signature at an offset equal
        // to its own length: the header remembered the signature and the file no longer had it.
        var bytes = Build(totalLength: 10_000, pe32Plus: true, certificateOffset: 10_000, certificateSize: 10_352);

        Assert.Equal(PeSignatureTableState.Truncated, InspectBytes(bytes));
    }

    [Fact]
    public void ATableShortByOneByteIsTruncated()
    {
        var bytes = Build(totalLength: 10_000, pe32Plus: true, certificateOffset: 8_000, certificateSize: 2_001);

        Assert.Equal(PeSignatureTableState.Truncated, InspectBytes(bytes));
    }

    [Fact]
    public void NoDeclaredTableMeansNeverSigned()
    {
        var bytes = Build(totalLength: 10_000, pe32Plus: true, certificateOffset: 0, certificateSize: 0);

        Assert.Equal(PeSignatureTableState.NoTable, InspectBytes(bytes));
    }

    [Fact]
    public void AHeaderWithFewerThanFiveDirectoriesHasNoTable()
    {
        // A directory count of four means entry 4 does not exist, whatever bytes happen to sit there.
        var bytes = Build(totalLength: 10_000, pe32Plus: true, certificateOffset: 8_000, certificateSize: 2_000, numberOfDirectories: 4);

        Assert.Equal(PeSignatureTableState.NoTable, InspectBytes(bytes));
    }

    [Fact]
    public void Pe32IsReadFromItsOwnOffsets()
    {
        // 32-bit optional headers are 16 bytes shorter. Reading them at the 64-bit offsets would
        // land on the wrong directory and give a confident wrong answer.
        var bytes = Build(totalLength: 10_000, pe32Plus: false, certificateOffset: 10_000, certificateSize: 100);

        Assert.Equal(PeSignatureTableState.Truncated, InspectBytes(bytes));
    }

    [Fact]
    public void NotAPeFileSaysSo()
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'P';
        bytes[1] = (byte)'K';

        Assert.Equal(PeSignatureTableState.NotPe, InspectBytes(bytes));
    }

    [Fact]
    public void AnMzStubWithoutAPeHeaderIsNotPe()
    {
        var bytes = Build(totalLength: 10_000, pe32Plus: true, certificateOffset: 0, certificateSize: 0);
        bytes[PeOffset] = (byte)'X';

        Assert.Equal(PeSignatureTableState.NotPe, InspectBytes(bytes));
    }

    [Fact]
    public void AFileTooShortToHoldAHeaderIsUnreadable()
    {
        var bytes = new byte[] { (byte)'M', (byte)'Z', 0, 0 };

        Assert.Equal(PeSignatureTableState.Unreadable, InspectBytes(bytes));
    }

    [Fact]
    public void AMissingFileIsUnreadableRatherThanAnException()
    {
        var path = Path.Combine(Path.GetTempPath(), "swapshelf-pe-tests", Guid.NewGuid().ToString("N"), "gone.dll");

        Assert.Equal(PeSignatureTableState.Unreadable, PeSignatureTable.Inspect(path));
    }
}
