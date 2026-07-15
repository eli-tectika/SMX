using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Smx.Domain;

/// A single, comparable price for one catalog/supplier row: US dollars per gram of the COMPOUND. The
/// <see cref="Currency"/> is carried explicitly and is always "USD" — there is no other value it can hold,
/// because <see cref="PriceParse"/> refuses everything else rather than convert it.
public sealed record Quote(double UsdPerGram, string Currency);

/// Turns the free-text <c>price</c> and <c>pack</c> that live on a minority of catalog products
/// ("$115.00", "500 mg") into one number procurement can act on — and, far more often, REFUSES to.
///
/// The seeded supplier <c>pricing</c> is mostly the literal word "Quote"; a handful of catalog rows carry a
/// non-USD figure ("¥800", "Published (CNY)"). A Cost stage that turned "Quote" into a number, averaged an
/// unparseable row, or silently read a yuan figure as dollars would be fabricating the single number a
/// purchase order is cut against. Every branch here fails in the same direction: refuse, and name why. None
/// substitutes a default, a guessed currency, or an FX rate this system does not have.
public static class PriceParse
{
    // A non-USD currency, by SYMBOL or by ISO/informal CODE, anywhere in the string. Runs FIRST, so a "¥800"
    // or a "Published (CNY)" is refused as a currency before any attempt to read a number out of it — a code
    // with no number attached ("Published (CNY)") is still a foreign-currency row, not free text. USD/$ are
    // deliberately absent from this set: they are the one thing we accept.
    private static readonly Regex NonUsdCurrency = new(
        @"[¥€£₩₹¢]|\b(?:CNY|RMB|YEN|JPY|EUR|GBP|CHF|CAD|AUD|HKD|SGD|INR|KRW|TWD|NZD|MXN|BRL|ZAR|SEK|NOK|DKK|PLN|RUB|AED|SAR)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A USD price and nothing else: a leading "$" or "USD" (so "USD 66" is accepted), then a plain decimal.
    // Anchored end-to-end and WITHOUT a thousands separator on purpose — a "$1,000" captures "1" and then
    // fails the anchor, which routes it to a refusal instead of a silent read of "1". The sign is captured so
    // a "$-5" parses and is then rejected as non-positive, rather than slipping past as unmatched.
    private static readonly Regex UsdPrice = new(
        @"^\s*(?:\$|USD)\s*([+-]?[0-9]*\.?[0-9]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UsdMarker = new(@"\$|\bUSD\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A pack mass: a plain decimal + a unit the catalog actually uses (mg | g | kg), full-match so a "grams"
    // or an "each" or a "$1e400 g" cannot partial-match into a bogus number. The alternation lists "mg"/"kg"
    // before "g" so "500 mg" reads as milligrams, not "500 m" + grams.
    private static readonly Regex Pack = new(
        @"^\s*([0-9]*\.?[0-9]+)\s*(mg|kg|g)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <returns>Grams for a parseable, POSITIVE pack; <c>null</c> otherwise. Null is the divide-by-zero guard
    /// and the "we could not read it" signal alike — <see cref="Parse"/> turns either into a refusal.</returns>
    public static double? PackGrams(string? pack)
    {
        if (string.IsNullOrWhiteSpace(pack)) return null;

        var m = Pack.Match(pack);
        if (!m.Success) return null;

        // InvariantCulture + NumberStyles.Float: a decimal POINT is allowed, a thousands separator is not
        // (Float excludes AllowThousands), so a comma-decimal read the other way — a 1000x mis-mass — cannot
        // sneak through. The regex already excluded a comma, but the parse holds the same line.
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var qty))
            return null;
        if (!double.IsFinite(qty) || qty <= 0) return null;   // "0 g" -> null: never divide by zero downstream.

        var grams = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "mg" => qty / 1000.0,
            "kg" => qty * 1000.0,
            _    => qty,          // "g"
        };
        return double.IsFinite(grams) ? grams : null;
    }

    /// <param name="price">Free text off a catalog/supplier row: "$115.00", "USD 66", "Quote", "¥800", "".</param>
    /// <param name="pack">The pack size that <paramref name="price"/> is for: "500 mg", "25g", "1 kg".</param>
    /// <returns>A USD-per-gram <see cref="Quote"/>, or a null quote and a human-readable reason. The three
    /// refusal families each own a distinct word — "no price" (free text), "currency" (non-USD or
    /// un-currencied number), "pack" (unreadable pack) — so a caller can tell them apart and no test can pass
    /// on the wrong branch.</returns>
    public static (Quote? Quote, string? Error) Parse(string? price, string? pack)
    {
        if (string.IsNullOrWhiteSpace(price))
            return (null, "no price: the price field is empty.");

        var shown = price.Trim();

        // 1. A non-USD currency is refused before anything else. This system has no FX rate and no date to
        //    apply one to, and inventing either is exactly the fabricated number this class exists to prevent.
        if (NonUsdCurrency.IsMatch(price))
            return (null, $"unsupported currency: '{shown}' is not in USD, and this system has no FX rate " +
                          "(or a date for one) to convert it — refusing rather than invent a number.");

        // 2. A clean USD amount.
        var m = UsdPrice.Match(price);
        if (m.Success)
        {
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dollars)
                || !double.IsFinite(dollars) || dollars <= 0)
                return (null, $"the USD price '{shown}' is not a positive, finite amount.");

            var grams = PackGrams(pack);
            if (grams is not > 0)
                return (null, $"the pack size '{FormatPack(pack)}' could not be read as grams — expected a " +
                              "value like '25 g', '500 mg', or '1 kg'.");

            var perGram = dollars / grams.Value;
            if (!double.IsFinite(perGram))
                return (null, $"the pack size '{FormatPack(pack)}' yields a non-finite $/g — refusing it.");

            return (new Quote(perGram, "USD"), null);
        }

        // 3. No clean USD amount. Distinguish the reasons so each refusal owns its own word.
        if (UsdMarker.IsMatch(price))
            // A "$" is present but the amount is unreadable: "$1,000" (a thousands separator is ambiguous),
            // "$Infinity", "$abc". Refuse rather than read the leading digit and drop the rest.
            return (null, $"the USD price '{shown}' could not be read as a plain decimal amount " +
                          "(a thousands separator like '1,000' is ambiguous) — refusing rather than guess.");

        if (price.Any(char.IsDigit))
            // A bare number with no symbol at all ("66"). Its currency is a guess, and a number whose
            // currency you are guessing is not a price.
            return (null, $"unsupported currency: '{shown}' has no recognizable currency symbol ($ or USD); a " +
                          "number whose currency we would be guessing is not a price.");

        // Free text with no number at all: "Quote", "n/a", "Catalog (login)". Most of the seeded data.
        return (null, $"no price: '{shown}' is free text, not a number.");
    }

    private static string FormatPack(string? pack) => string.IsNullOrWhiteSpace(pack) ? "" : pack.Trim();
}
