using System.Text.Json;

namespace Analytics.Data;

/// <summary>
/// The show documents' JSON contract, in one place: the worker serializes with <see cref="Options"/> and every reader
/// (typed or by jsonb path) deserializes with the same, so the camelCase paths the SQL readers use can't drift from
/// the records. A path constant here is the only place a property name is spelled as JSON.
/// </summary>
public static class InsightDocuments
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T document) => JsonSerializer.Serialize(document, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static ShowInsights? Read(ShowInsightsRow row) => Deserialize<ShowInsights>(row.Document);

    /// <summary>The jsonb keys of the parts readers select by path, as <see cref="Options"/> names them.</summary>
    public static class Paths
    {
        public static readonly string Title = Name(nameof(ShowInsights.Title));
        public static readonly string Tags = Name(nameof(ShowInsights.Tags));
        public static readonly string Dna = Name(nameof(ShowInsights.Dna));
        public static readonly string Traits = Name(nameof(ShowDna.Traits));
        public static readonly string SharedCast = Name(nameof(ShowInsights.SharedCast));
        public static readonly string GetsGood = Name(nameof(ShowInsights.GetsGood));
        public static readonly string Turn = Name(nameof(Analytics.Data.GetsGood.Turn));
        public static readonly string FromTheStart = Name(nameof(Analytics.Data.GetsGood.FromTheStart));
        public static readonly string Outlook = Name(nameof(ShowInsights.Outlook));
        public static readonly string SeasonVerdict = Name(nameof(ShowInsights.SeasonVerdict));
        public static readonly string Number = Name(nameof(Analytics.Data.SeasonVerdict.Number));
        public static readonly string FinaleAiredAtUtc = Name(nameof(Analytics.Data.SeasonVerdict.FinaleAiredAtUtc));
        public static readonly string Landing = Name(nameof(ShowInsights.Landing));
        public static readonly string WorthIt = Name(nameof(ShowInsights.WorthIt));
        public static readonly string Key = Name(nameof(ShowTag.Key));
        public static readonly string Value = Name(nameof(DnaTrait.Value));

        private static string Name(string property) => Options.PropertyNamingPolicy!.ConvertName(property);
    }
}
