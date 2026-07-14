using System.Text.RegularExpressions;
using Smx.Domain;

namespace Smx.Domain.Tests;

/// The seeded catalog is what Discovery proposes candidates FROM. A bad CAS in it is a bad CAS in a
/// recommendation. This test is the regression guard for the 15492-49-8 defect: it fails the build if an
/// invalid CAS is ever reintroduced by a reference-data regeneration.
public class SeedCasIntegrityTests
{
    [Fact]
    public void EverySeededCasNumberPassesItsCheckDigit()
    {
        var seedDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Smx.Functions", "Reference", "Seed");
        Assert.True(Directory.Exists(seedDir), $"seed directory not found: {Path.GetFullPath(seedDir)}");

        var invalid = new List<string>();
        foreach (var file in new[] { "catalog-products.json", "catalog-elements.json" })
        {
            var text = File.ReadAllText(Path.Combine(seedDir, file));
            foreach (Match m in Regex.Matches(text, @"\b\d{2,7}-\d{2}-\d\b"))
                if (!CasNumber.IsValid(m.Value))
                    invalid.Add($"{file}: {m.Value}");
        }

        Assert.True(invalid.Count == 0,
            "Invalid CAS numbers in the seeded catalog (check digit failed):\n  " + string.Join("\n  ", invalid.Distinct()));
    }
}
