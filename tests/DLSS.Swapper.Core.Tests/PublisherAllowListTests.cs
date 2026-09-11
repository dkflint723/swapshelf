using DLSS_Swapper.Data;
using DLSS_Swapper.Signing;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// "Signed" has to mean "signed by the vendor", or it means very little.
/// </summary>
/// <remarks>
/// The names are the ones measured on a real library. The whole value of the list is that it is
/// exact, so most of these tests are about what it refuses.
/// </remarks>
public class PublisherAllowListTests
{
    [Theory]
    [InlineData(DllVendor.Nvidia, "NVIDIA Corporation")]
    [InlineData(DllVendor.Amd, "Advanced Micro Devices")]
    [InlineData(DllVendor.Intel, "Intel Corporation")]
    public void TheMeasuredSignerOfEachVendorIsAccepted(DllVendor vendor, string subject)
    {
        Assert.True(PublisherAllowList.IsAcceptedPublisher(vendor, subject));
    }

    [Fact]
    public void AmdWithoutIncIsTheRealOne()
    {
        // The one a guess gets wrong. Every FSR dll on disk is signed "Advanced Micro Devices",
        // full stop, and a list built from the company's legal name would have refused all of them.
        Assert.True(PublisherAllowList.IsAcceptedPublisher(DllVendor.Amd, "Advanced Micro Devices"));
        Assert.True(PublisherAllowList.IsAcceptedPublisher(DllVendor.Amd, "Advanced Micro Devices, Inc."));
    }

    [Theory]
    [InlineData("nvidia corporation")]
    [InlineData("  NVIDIA Corporation  ")]
    [InlineData("NVIDIA Corporation.")]
    public void CaseSpaceAndATrailingStopAreForgiven(string subject)
    {
        Assert.True(PublisherAllowList.IsAcceptedPublisher(DllVendor.Nvidia, subject));
    }

    [Theory]
    [InlineData("NVIDIA Corp")]
    [InlineData("NVIDIA")]
    [InlineData("NVIDIA Corporation Ltd")]
    [InlineData("Not NVIDIA Corporation")]
    [InlineData("N V I D I A Corporation")]
    public void AnythingElseIsRefused(string subject)
    {
        // A rule loose enough to accept "NVIDIA Corp" would accept the next thing too.
        Assert.False(PublisherAllowList.IsAcceptedPublisher(DllVendor.Nvidia, subject));
    }

    [Fact]
    public void AVendorDoesNotAcceptAnotherVendorsSigner()
    {
        // The whole point: a validly signed file from the wrong company is not the vendor's file.
        Assert.False(PublisherAllowList.IsAcceptedPublisher(DllVendor.Nvidia, "Intel Corporation"));
        Assert.False(PublisherAllowList.IsAcceptedPublisher(DllVendor.Amd, "NVIDIA Corporation"));
        Assert.False(PublisherAllowList.IsAcceptedPublisher(DllVendor.Intel, "Advanced Micro Devices"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoNameIsRefused(string? subject)
    {
        Assert.False(PublisherAllowList.IsAcceptedPublisher(DllVendor.Nvidia, subject));
    }

    [Fact]
    public void AnUnknownVendorAcceptsNobody()
    {
        // A dll type added without a vendor fails closed rather than trusting every signer.
        Assert.False(PublisherAllowList.IsAcceptedPublisher(DllVendor.Unknown, "NVIDIA Corporation"));
        Assert.False(PublisherAllowList.IsAcceptedPublisher(DllVendor.Unknown, "Intel Corporation"));
    }

    [Theory]
    [InlineData(DllVendor.Nvidia, "NVIDIA Corporation")]
    [InlineData(DllVendor.Amd, "Advanced Micro Devices")]
    [InlineData(DllVendor.Intel, "Intel Corporation")]
    [InlineData(DllVendor.Unknown, "an unknown vendor")]
    public void TheExpectedNameIsWhatAMessageShouldSay(DllVendor vendor, string expected)
    {
        Assert.Equal(expected, PublisherAllowList.ExpectedPublisher(vendor));
    }

    [Fact]
    public void OnlyAVendorSignatureCountsAsTrusted()
    {
        Assert.True(new SignatureCheck(SignatureVerdict.SignedByVendor, "NVIDIA Corporation").IsTrustedForVendor);
        Assert.False(new SignatureCheck(SignatureVerdict.SignedByOtherPublisher, "Someone Else").IsTrustedForVendor);
        Assert.False(new SignatureCheck(SignatureVerdict.Invalid, null).IsTrustedForVendor);
        Assert.False(new SignatureCheck(SignatureVerdict.SignatureRemoved, null).IsTrustedForVendor);
        Assert.False(new SignatureCheck(SignatureVerdict.Unsigned, null).IsTrustedForVendor);
    }
}
