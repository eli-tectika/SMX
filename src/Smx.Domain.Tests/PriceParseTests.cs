using Smx.Domain;

namespace Smx.Domain.Tests;

public class PriceParseTests
{
    [Theory]
    [InlineData("$115.00", "500 mg", 230.0)]     // $115 / 0.5 g
    [InlineData("$66", "25g", 2.64)]
    [InlineData("$340", "100 g", 3.4)]
    public void Parse_ConvertsAPriceAndAPackIntoDollarsPerGram(string price, string pack, double perGram)
    {
        var (quote, error) = PriceParse.Parse(price, pack);
        Assert.Null(error);
        Assert.Equal(perGram, quote!.UsdPerGram, 2);
        Assert.Equal("USD", quote.Currency);
    }

    [Theory]
    [InlineData("Quote")]
    [InlineData("Catalog (login)")]
    [InlineData("n/a")]
    [InlineData("")]
    public void Parse_REFUSES_AFreeTextPrice_RatherThanInventANumber(string price)
    {
        // Most of the seeded supplier data says exactly this. "Quote" is not a price, and a Cost stage that
        // turned it into one would be fabricating the single number procurement acts on.
        var (quote, error) = PriceParse.Parse(price, "25 g");
        Assert.Null(quote);
        Assert.Contains("no price", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_REFUSES_ANonDollarCurrency_AndNeverConvertsIt()
    {
        // A CNY figure is not comparable to a USD one, and this system has no FX rate, no date for it, and
        // no business inventing either. Refusing is the honest answer; converting is a fabricated number.
        var (quote, error) = PriceParse.Parse("¥800", "25 g");
        Assert.Null(quote);
        Assert.Contains("currency", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_REFUSES_AnUnparseablePack()
    {
        var (quote, error) = PriceParse.Parse("$115.00", "each");
        Assert.Null(quote);
        Assert.Contains("pack", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_REFUSES_AZeroPack_RatherThanDivideByZero() =>
        Assert.Null(PriceParse.Parse("$115.00", "0 g").Quote);

    [Theory]
    [InlineData("500 mg", 0.5)]
    [InlineData("25g", 25.0)]
    [InlineData("1 kg", 1000.0)]
    [InlineData("100 G", 100.0)]
    public void PackGrams_HandlesTheUnitsTheCatalogActuallyUses(string pack, double grams) =>
        Assert.Equal(grams, PriceParse.PackGrams(pack)!.Value, 6);
}
