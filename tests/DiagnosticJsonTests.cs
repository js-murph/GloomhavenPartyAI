using System.Globalization;
using System.Text.Json;
using GloomhavenPartyAI;

internal static class DiagnosticJsonTests
{
    internal static void EmptyNestedAndNullValuesAreValidJson()
    {
        Check.Equal("{}", new DiagnosticJson().ToString(), "empty object");
        string json = new DiagnosticJson().Add("text", (string)null).Add("object", (DiagnosticJson)null)
            .Add("nested", new DiagnosticJson().Add("yes", true).Add("no", false)).ToString();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Check.Equal(JsonValueKind.Null, root.GetProperty("text").ValueKind, "null string is JSON null");
        Check.Equal(JsonValueKind.Null, root.GetProperty("object").ValueKind, "null object is JSON null");
        Check.True(root.GetProperty("nested").GetProperty("yes").GetBoolean(), "nested true");
        Check.True(!root.GetProperty("nested").GetProperty("no").GetBoolean(), "nested false");
    }

    internal static void KeysAndValuesEscapeQuotesAndBackslashes()
    {
        string key = "key\"\\\n";
        string value = "\"},\"injected\":true,\"tail\":\"\\\r\n";
        string json = new DiagnosticJson().Add(key, value).ToString();
        using JsonDocument document = JsonDocument.Parse(json);
        Check.Equal(1, document.RootElement.EnumerateObject().Count(), "text cannot inject a JSON member");
        Check.Equal(value, document.RootElement.GetProperty(key).GetString(), "escaped key and value round trip");
        Check.Equal("{\"\\\"\\\\\":\"\\\"\\\\\"}", new DiagnosticJson().Add("\"\\", "\"\\").ToString(), "quote and slash wire escapes");
    }

    internal static void AllControlCharactersAndNonAsciiRoundTrip()
    {
        string value = new string(Enumerable.Range(0, 32).Select(n => (char)n).ToArray()) + "\u007f\u00e9\u2028\u2029\u4e2d\uffff\ud83d\ude00";
        string json = new DiagnosticJson().Add(value, value).ToString();
        Check.True(json.All(c => c >= 32 && c <= 126), "wire JSON contains only printable ASCII");
        for (int code = 0; code < 32; code++)
            Check.True(json.Contains($"\\u{code:x4}", StringComparison.Ordinal), $"control U+{code:X4} must be escaped");
        using JsonDocument document = JsonDocument.Parse(json);
        Check.Equal(value, document.RootElement.GetProperty(value).GetString(), "control and Unicode keys and values round trip");
    }

    internal static void NumbersIgnoreCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string culture in new[] { "fr-FR", "de-DE", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                string json = new DiagnosticJson().Add("fraction", -1234.5m).Add("max", decimal.MaxValue)
                    .Add("min", decimal.MinValue).Add("tiny", 0.0000000000000000000000000001m).ToString();
                Check.True(json.Contains("\"fraction\":-1234.5", StringComparison.Ordinal), $"invariant decimal separator under {culture}");
                using JsonDocument document = JsonDocument.Parse(json);
                Check.Equal(-1234.5m, document.RootElement.GetProperty("fraction").GetDecimal(), culture);
                Check.Equal(decimal.MaxValue, document.RootElement.GetProperty("max").GetDecimal(), culture);
                Check.Equal(decimal.MinValue, document.RootElement.GetProperty("min").GetDecimal(), culture);
                Check.Equal(0.0000000000000000000000000001m, document.RootElement.GetProperty("tiny").GetDecimal(), culture);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    internal static void MalformedSurrogatesAreReplacedAndValidPairsPreserved()
    {
        foreach (var (input, expected) in new[]
        {
            ("\ud800", "\ufffd"), ("\udc00", "\ufffd"),
            ("a\ud800z", "a\ufffdz"), ("a\udc00z", "a\ufffdz"),
            ("\ud800\ud800", "\ufffd\ufffd"), ("\udc00\udc00", "\ufffd\ufffd"),
            ("\udc00\ud800", "\ufffd\ufffd"), ("\ud800\ud800\udc00", "\ufffd\ud800\udc00"),
            ("\ud800\udc00\udc00", "\ud800\udc00\ufffd"),
            ("\ud800\udc00", "\ud800\udc00"), ("\udbff\udfff", "\udbff\udfff"),
            ("\ud83d\ude00", "\ud83d\ude00")
        })
        {
            string json = new DiagnosticJson().Add(input, input).ToString();
            using JsonDocument document = JsonDocument.Parse(json);
            Check.Equal(expected, document.RootElement.GetProperty(expected).GetString(), $"UTF-16 sequence {string.Join(" ", input.Select(c => $"{(int)c:X4}"))}");
        }
        Check.Equal("{\"bad\":\"\\ufffd\"}", new DiagnosticJson().Add("bad", "\ud800").ToString(), "replacement is escaped on wire");
    }

    internal static void ToStringDoesNotCloseOrMutateTheBuilder()
    {
        var builder = new DiagnosticJson().Add("first", 1m);
        string snapshot = builder.ToString();
        Check.Equal(snapshot, builder.ToString(), "repeated serialization is stable");
        builder.Add("second", 2m);
        using JsonDocument before = JsonDocument.Parse(snapshot);
        using JsonDocument after = JsonDocument.Parse(builder.ToString());
        Check.Equal(1, before.RootElement.EnumerateObject().Count(), "previous snapshot unchanged");
        Check.Equal(2, after.RootElement.EnumerateObject().Count(), "builder accepts fields after serialization");
        Check.Equal(2m, after.RootElement.GetProperty("second").GetDecimal(), "new field serialized");
    }
}
