namespace Smx.Domain;

/// A CAS Registry Number carries a check digit, and that makes it self-validating — the one identifier in
/// this system that can be proved wrong without consulting anything.
///
/// Why we care: an agent that reads a CAS off a web snippet can transpose a digit, and nothing downstream
/// would notice. The regulatory screen would clear the WRONG substance, dosing would compute against the
/// wrong molecular weight, and procurement would order it. This is the cheapest possible guard against the
/// system's headline harm, and it applies to catalog candidates too — it is how the invalid Sc(TMHD)3 entry
/// (15492-49-8, check digit should be 6) was found in the seeded catalog.
///
/// Format: 2-7 digits, hyphen, 2 digits, hyphen, 1 check digit.
/// Check: sum of each digit multiplied by its position from the right (1-based), mod 10.
///   1314-36-9 → 6*1 + 3*2 + 4*3 + 1*4 + 3*5 + 1*6 = 49 → 49 mod 10 = 9 ✓
public static class CasNumber
{
    public static bool IsValid(string? cas)
    {
        if (string.IsNullOrWhiteSpace(cas)) return false;

        var parts = cas.Trim().Split('-');
        if (parts.Length != 3) return false;
        if (parts[0].Length is < 2 or > 7 || parts[1].Length != 2 || parts[2].Length != 1) return false;
        if (!parts.All(p => p.All(char.IsAsciiDigit))) return false;

        var digits = parts[0] + parts[1];
        var checkDigit = parts[2][0] - '0';

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
            sum += (digits.Length - i) * (digits[i] - '0');

        return sum % 10 == checkDigit;
    }
}
