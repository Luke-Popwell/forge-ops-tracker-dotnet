using System.Text.Json;

namespace ForgeOpsTracker.Tests;

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/pii_scrubber_spec.rb.
public class PiiScrubberTests
{
    [Fact]
    public void RedactsAnEmailAddressEmbeddedInFreeText()
    {
        Assert.Equal(
            "undefined method 'name' for [EMAIL FILTERED]",
            PiiScrubber.ScrubString("undefined method 'name' for user@example.com"));
    }

    [Fact]
    public void RedactsAFormattedCreditCardNumberButLeavesAnOrdinaryLongNumericIdAlone()
    {
        Assert.Equal("card [CREDIT CARD FILTERED] declined", PiiScrubber.ScrubString("card 4111-1111-1111-1111 declined"));
        Assert.Equal(
            "Couldn't find Invoice with 'id'=8821445199",
            PiiScrubber.ScrubString("Couldn't find Invoice with 'id'=8821445199"));
    }

    [Fact]
    public void RedactsKnownApiKeyAndTokenFormats()
    {
        Assert.Equal("using [STRIPE KEY FILTERED]", PiiScrubber.ScrubString("using sk_live_" + "4eC39HqLyjWDarjtT1zdp7dc"));
        Assert.Equal("Authorization: [BEARER TOKEN FILTERED]", PiiScrubber.ScrubString("Authorization: Bearer abc123.def456"));
    }

    [Fact]
    public void RedactsTheWholeValueUnderASensitiveLookingKeyRegardlessOfTypeOrCasing()
    {
        var input = new Dictionary<string, object?>
        {
            ["password"] = "hunter2",
            ["API-Key"] = "sk_live_abc",
            ["count"] = 3
        };

        var scrubbed = Assert.IsType<Dictionary<string, object?>>(PiiScrubber.Scrub(input));

        Assert.Equal("[FILTERED]", scrubbed["password"]);
        Assert.Equal("[FILTERED]", scrubbed["API-Key"]);
        Assert.Equal(3, scrubbed["count"]);
    }

    [Fact]
    public void RecursesIntoNestedDictionariesAndLists()
    {
        var input = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["email"] = "ada@example.com" },
            ["notes"] = new List<object?> { "no PII here" }
        };

        var scrubbed = Assert.IsType<Dictionary<string, object?>>(PiiScrubber.Scrub(input));
        var user = Assert.IsType<Dictionary<string, object?>>(scrubbed["user"]);
        var notes = Assert.IsType<List<object?>>(scrubbed["notes"]);

        Assert.Equal("[EMAIL FILTERED]", user["email"]);
        Assert.Equal("no PII here", notes[0]);
    }

    // Fixtures/pii_scrubbing_vectors.json is a byte-identical copy of sdks/pii_scrubbing_vectors.json,
    // generated from the server's own PiiScrubber: see sdks/PII_SCRUBBING.md.
    private static JsonElement Vectors(string kind)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pii_scrubbing_vectors.json");
        return JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty(kind);
    }

    [Fact]
    public void ScrubsCardNumbersAndIbansInEveryVectorExactlyAsTheServerDoes()
    {
        var vectors = Vectors("strings").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            Assert.Equal(vector.GetProperty("scrubbed").GetString(), PiiScrubber.ScrubString(vector.GetProperty("input").GetString()!));
        }
    }

    [Fact]
    public void RedactsBankDetailKeysInEveryVectorExactlyAsTheServerDoes()
    {
        var vectors = Vectors("keys").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            var key = vector.GetProperty("key").GetString()!;
            var scrubbed = Assert.IsType<Dictionary<string, object?>>(PiiScrubber.Scrub(new Dictionary<string, object?> { [key] = "x" }));
            Assert.Equal(vector.GetProperty("redacted").GetBoolean() ? PiiScrubber.Redacted : "x", scrubbed[key]);
        }
    }
}
