namespace DLSS_Swapper.Signing;

/// <summary>
/// What a dll's signature says about where it came from - not only whether it verifies.
/// </summary>
/// <remarks>
/// The old answer was a bool from WinVerifyTrust, and a true meant "chains to some trusted root".
/// That is a statement about the certificate, not about the file: a dll validly signed by anyone
/// at all read as trusted, and there is no shortage of anyone. These are the answers a swap or an
/// import actually needs to tell apart.
/// </remarks>
public enum SignatureVerdict
{
    /// <summary>No signature, and the header never had one.</summary>
    Unsigned,

    /// <summary>The header declares a signature the file no longer holds. It was altered after signing.</summary>
    SignatureRemoved,

    /// <summary>A signature is present but Windows will not verify it.</summary>
    Invalid,

    /// <summary>Valid, but signed by somebody other than the vendor that makes this kind of dll.</summary>
    SignedByOtherPublisher,

    /// <summary>Valid, and signed by the vendor that makes this kind of dll.</summary>
    SignedByVendor,
}

/// <summary>
/// The verdict and, when there was a signature to read, who signed.
/// </summary>
public readonly record struct SignatureCheck(SignatureVerdict Verdict, string? Publisher)
{
    /// <summary>The one outcome a swap or import may proceed on without the user overriding.</summary>
    public bool IsTrustedForVendor => Verdict == SignatureVerdict.SignedByVendor;
}
