using Smx.Domain;

namespace Smx.Domain.Tests;

public class CasNumberTests
{
    // Real CAS numbers from the seeded catalog.
    [Theory]
    [InlineData("1314-36-9")]   // yttrium oxide
    [InlineData("80326-98-3")]  // yttrium 2-ethylhexanoate
    [InlineData("7732-18-5")]   // water — the textbook example
    [InlineData("15492-49-6")]  // Sc(TMHD)3, anhydrous — the CORRECTED value from Task 0
    public void ValidCas_IsAccepted(string cas) => Assert.True(CasNumber.IsValid(cas));

    // The check digit is the whole point: a single transposed digit must not pass.
    [Theory]
    [InlineData("15492-49-8")]  // the typo Task 0 removed from the catalog
    [InlineData("1314-36-8")]
    [InlineData("7732-18-4")]
    public void WrongCheckDigit_IsRejected(string cas) => Assert.False(CasNumber.IsValid(cas));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-cas")]
    [InlineData("1314-36")]        // missing the check digit
    [InlineData("1314-3-69")]      // middle group must be exactly 2 digits
    [InlineData("1-36-9")]         // first group must be 2..7 digits
    [InlineData("12345678-36-9")]  // first group too long
    [InlineData("1314-36-99")]     // check digit must be a single digit
    public void Malformed_IsRejected(string? cas) => Assert.False(CasNumber.IsValid(cas));
}
