using System.Linq;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// A dll built for one processor is not swapped in over one built for another.
/// </summary>
/// <remarks>
/// <para>
/// Windows will not load a 32-bit dll into a 64-bit process or the reverse, and a game that finds
/// the wrong one where it expected its upscaler crashes on startup - long after the swap reported
/// success and with nothing pointing back at it. Every dll the executor moves is a PE file, so the
/// question costs two bytes of header and is asked before anything is staged.
/// </para>
/// <para>
/// A header that cannot be read is not held against a file. The check exists to catch a known
/// contradiction, not to demand proof.
/// </para>
/// </remarks>
public class DllSwapExecutorArchitectureTests
{
    const string Source = @"C:\library\nvngx_dlss.dll";
    const string Target = @"C:\game\nvngx_dlss.dll";
    const string SecondTarget = @"C:\game\bin\nvngx_dlss.dll";
    static readonly string Backup = DllSwapExecutor.GetBackupPath(Target);

    static readonly byte[] X64 = PeMachineTests.Pe(0x8664);
    static readonly byte[] X86 = PeMachineTests.Pe(0x014C);

    [Fact]
    public void AThirtyTwoBitDllIsNotSwappedOverASixtyFourBitOne()
    {
        var fs = new FakeFileSystem()
            .AddBinaryFile(Source, X86)
            .AddBinaryFile(Target, X64);

        var result = new DllSwapExecutor(fs).Swap(Source, new[] { Target });

        Assert.False(result.Success);
        Assert.Equal(SwapFailure.ArchitectureMismatch, result.Failure);
        Assert.Equal(Target, result.FailedPath);

        // Nothing was touched: no backup, no staging file, the target is exactly what it was.
        Assert.Equal(X64, fs.ReadBytes(Target));
        Assert.False(fs.FileExists(Backup));
        Assert.Equal(2, fs.AllPaths.Count);

        // And the log line says which was which.
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("32-bit x86", warning);
        Assert.Contains("64-bit x64", warning);
    }

    [Fact]
    public void TheSameArchitectureGoesThrough()
    {
        var newer = PeMachineTests.Pe(0x8664);
        newer[0xF0] = 0x42;   // different bytes, same machine

        var fs = new FakeFileSystem()
            .AddBinaryFile(Source, newer)
            .AddBinaryFile(Target, X64);

        var result = new DllSwapExecutor(fs).Swap(Source, new[] { Target });

        Assert.True(result.Success);
        Assert.Equal(newer, fs.ReadBytes(Target));
        Assert.Equal(X64, fs.ReadBytes(Backup));
    }

    [Fact]
    public void ASourceWhoseHeaderCannotBeReadIsNotRefused()
    {
        // Not a PE at all. Whether it is a valid dll is the hash and signature checks' question,
        // asked before the executor is reached; this check only knows about a contradiction it can see.
        var fs = new FakeFileSystem()
            .AddFile(Source, "not a portable executable")
            .AddBinaryFile(Target, X64);

        var result = new DllSwapExecutor(fs).Swap(Source, new[] { Target });

        Assert.True(result.Success);
    }

    [Fact]
    public void ATargetWhoseHeaderCannotBeReadIsNotRefused()
    {
        var fs = new FakeFileSystem()
            .AddBinaryFile(Source, X64)
            .AddFile(Target, "stub");

        var result = new DllSwapExecutor(fs).Swap(Source, new[] { Target });

        Assert.True(result.Success);
    }

    [Fact]
    public void OneMismatchedLocationStopsTheWholeBatchBeforeAnythingIsStaged()
    {
        // Two copies of the dll in one game, one of them the wrong kind. Neither is touched: a game
        // half swapped is the state the executor exists to prevent.
        var fs = new FakeFileSystem()
            .AddBinaryFile(Source, X64)
            .AddBinaryFile(Target, X64)
            .AddBinaryFile(SecondTarget, X86);

        var result = new DllSwapExecutor(fs).Swap(Source, new[] { Target, SecondTarget });

        Assert.False(result.Success);
        Assert.Equal(SwapFailure.ArchitectureMismatch, result.Failure);
        Assert.Equal(SecondTarget, result.FailedPath);

        Assert.Equal(X64, fs.ReadBytes(Target));
        Assert.Equal(X86, fs.ReadBytes(SecondTarget));
        Assert.False(fs.FileExists(Backup));
        Assert.Equal(3, fs.AllPaths.Count);
    }
}
