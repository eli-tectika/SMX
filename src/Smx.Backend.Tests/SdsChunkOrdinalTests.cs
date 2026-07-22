using System.Text;
using Smx.Domain.Documents;

namespace Smx.Backend.Tests;

public class SdsChunkOrdinalTests
{
    // Mirrors DedupKey.ForChunk: base64url(registryId) + "-" + ordinal.
    private static string Key(string registryId, int ordinal) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(registryId)).Replace('+', '-').Replace('/', '_')
        + "-" + ordinal;

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(148)]
    public void RecoversTheOrdinal(int ordinal)
    {
        var key = Key("7761-88-8|sigma|2024-03-11", ordinal);
        Assert.Equal(ordinal, SdsChunkOrdinal.From(key));
    }

    // base64url uses '-' as a character, so the ordinal is after the LAST '-', not the first.
    //
    // A registryId built only from DedupKey.Norm's alphabet (lowercase letters, digits, '-', '|',
    // collapsed spaces) can never itself trigger this: every byte in that range stays below the
    // bit pattern base64 maps to '+' (62), so the encoded body never contains a stray '-'. The
    // ">>>" here exists purely to force one, so this test exercises the LAST-not-FIRST behaviour
    // instead of vacuously passing on a body that happens to have no embedded dash at all.
    [Fact]
    public void SplitsOnTheLastDashNotTheFirst()
    {
        var key = Key("7761-88-8|sigma>>>2024-03-11", 12);
        Assert.Contains('-', key[..key.LastIndexOf('-')]);   // guard: the prefix really does contain a dash
        Assert.Equal(12, SdsChunkOrdinal.From(key));
    }

    // An unparseable key must sort last rather than throw or collide at 0 — one malformed key should
    // not silently reorder an entire safety data sheet.
    [Theory]
    [InlineData("nodash")]
    [InlineData("abc-notanumber")]
    [InlineData("")]
    public void UnparseableKeysSortLast(string key)
    {
        Assert.Equal(int.MaxValue, SdsChunkOrdinal.From(key));
    }
}
