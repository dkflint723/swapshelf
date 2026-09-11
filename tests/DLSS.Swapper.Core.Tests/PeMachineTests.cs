using System;
using System.IO;
using DLSS_Swapper.Pe;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// Reading which processor a PE file was built for.
/// </summary>
/// <remarks>
/// Synthetic headers, for the same reason as the signature table tests: the layout is documented
/// and small, and no shipped dll exhibits the broken shapes that matter here.
/// </remarks>
public class PeMachineTests
{
    internal const int PeOffset = 0x80;

    /// <summary>A DOS stub pointing at a PE signature followed by the given machine value.</summary>
    internal static byte[] Pe(ushort machine, int totalLength = 0x100)
    {
        var bytes = new byte[totalLength];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        bytes[0x3C] = PeOffset;

        bytes[PeOffset] = (byte)'P';
        bytes[PeOffset + 1] = (byte)'E';
        bytes[PeOffset + 4] = (byte)machine;
        bytes[PeOffset + 5] = (byte)(machine >> 8);
        return bytes;
    }

    [Theory]
    [InlineData((ushort)0x8664, PeMachine.X64)]
    [InlineData((ushort)0x014C, PeMachine.X86)]
    [InlineData((ushort)0xAA64, PeMachine.Arm64)]
    [InlineData((ushort)0x01C4, PeMachine.Arm)]
    [InlineData((ushort)0x0200, PeMachine.Other)]    // Itanium: real, named by nobody here, still a value
    [InlineData((ushort)0x0000, PeMachine.Unknown)]
    public void TheMachineFieldIsReadAsDocumented(ushort value, PeMachine expected)
    {
        Assert.Equal(expected, PeHeader.ReadMachine(Pe(value)));
    }

    [Fact]
    public void TheStreamAndSpanOverloadsAgree()
    {
        var bytes = Pe(0x8664);

        using (var stream = new MemoryStream(bytes))
        {
            Assert.Equal(PeMachine.X64, PeHeader.ReadMachine(stream));
        }
    }

    [Fact]
    public void AFileThatIsNotAPeIsUnknownNotAnError()
    {
        Assert.Equal(PeMachine.Unknown, PeHeader.ReadMachine(System.Text.Encoding.UTF8.GetBytes("this is a text file that happens to be long enough to hold a DOS header, sixty-four bytes and more")));
        Assert.Equal(PeMachine.Unknown, PeHeader.ReadMachine(Array.Empty<byte>()));
        Assert.Equal(PeMachine.Unknown, PeHeader.ReadMachine(new byte[] { (byte)'M', (byte)'Z' }));
    }

    [Fact]
    public void ADosHeaderPointingPastTheEndIsUnknown()
    {
        var bytes = Pe(0x8664);
        bytes[0x3C] = 0xF0;
        bytes[0x3D] = 0xFF;   // e_lfanew far beyond the buffer

        Assert.Equal(PeMachine.Unknown, PeHeader.ReadMachine(bytes));
    }

    [Fact]
    public void ADosHeaderWithoutAPeSignatureBehindItIsUnknown()
    {
        var bytes = Pe(0x8664);
        bytes[PeOffset] = (byte)'N';

        Assert.Equal(PeMachine.Unknown, PeHeader.ReadMachine(bytes));
    }

    [Fact]
    public void AMissingFileIsUnknown()
    {
        Assert.Equal(PeMachine.Unknown, PeHeader.ReadMachine(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".dll")));
    }

    [Fact]
    public void EveryMachineHasAName()
    {
        foreach (var machine in Enum.GetValues<PeMachine>())
        {
            Assert.False(string.IsNullOrWhiteSpace(PeHeader.Describe(machine)));
        }

        Assert.Equal("64-bit x64", PeHeader.Describe(PeMachine.X64));
        Assert.Equal("32-bit x86", PeHeader.Describe(PeMachine.X86));
    }
}
