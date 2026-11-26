using Financials.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Financials.API.Services;

/// <summary>
/// Brings across what payments and affiliates kept before they moved here: the subscriptions, receipts, checkouts,
/// applied provider events and payment settings from Identity's database, and the affiliate programs, clicks and
/// sales from Catalog's. Run once at start, in the same transaction as nothing else.
/// <list type="bullet">
/// <item><b>Only into empty tables:</b> each group (payments, affiliates) is copied only while this database has none of
/// it, so a second start, or a deployment that began here, copies nothing.</item>
/// <item><b>Only where there's something to copy:</b> without the old database's connection string
/// (<c>ConnectionStrings:identitydb</c>, <c>catalogdb</c>), or where its old tables aren't there, the group is skipped.
/// Given the connection string, the old database has to open: a failure stops the start rather than be passed over.</item>
/// <item><b>The old tables are left as they are:</b> their services no longer read them; a deployment drops them once it
/// has checked the copy.</item>
/// <item><b>Clicks gain what they now carry:</b> the show's title as Catalog has it today, and the listing's type and
/// link by name, as Catalog's events give them.</item>
/// </list>
/// </summary>
public static class LegacyImport
{
    private static readonly string[] PaymentTables =
        ["payment_settings", "checkout_sessions", "subscriptions", "payment_receipts", "processed_payment_events"];

    // Catalog kept the listing's type and the link's kind as numbers (its WatchProviderType and WatchLink enums).
    private const string Clicks = """
        select c."Id", c."ShowId", left(coalesce(s."Title", '(removed show)'), 256) as "ShowTitle", c."Service",
               coalesce((array['Stream','Free','Rent','Buy'])[c."Type" + 1], c."Type"::text) as "Type",
               coalesce((array['Site','Search','Title'])[c."Link" + 1], c."Link"::text) as "Link",
               c."Region", c."Surface", c."Plan", c."Program", c."ClickedAtUtc"
        from outbound_clicks c left join shows s on s."Id" = c."ShowId"
        """;

    public static async Task RunAsync(FinancialsContext db, IConfiguration configuration, ILogger logger, CancellationToken ct)
    {
        if (configuration.GetConnectionString("identitydb") is { Length: > 0 } identity
            && !await db.Subscriptions.AnyAsync(ct) && !await db.CheckoutSessions.AnyAsync(ct) && !await db.Receipts.AnyAsync(ct)
            && !await db.PaymentSettings.AnyAsync(ct))
        {
            await CopyAsync(db, identity, PaymentTables.Select(t => (Table: t, Select: $"select * from {t}")).ToList(), "payments", logger, ct);
        }
        if (configuration.GetConnectionString("catalogdb") is { Length: > 0 } catalog
            && !await db.AffiliatePrograms.AnyAsync(ct) && !await db.OutboundClicks.AnyAsync(ct))
        {
            // Clicks before the sales that refer to them.
            await CopyAsync(db, catalog,
                [("affiliate_programs", "select * from affiliate_programs"), ("outbound_clicks", Clicks), ("affiliate_conversions", "select * from affiliate_conversions")],
                "affiliates", logger, ct);
        }
    }

    /// <summary>
    /// Copies each table's rows from the old database into the table of the same name here, all or none.
    /// </summary>
    internal static async Task CopyAsync(FinancialsContext db, string sourceConnectionString, IReadOnlyList<(string Table, string Select)> tables,
        string what, ILogger logger, CancellationToken ct)
    {
        // An old database that can't be opened stops the start: were the service to carry on, its first new row would
        // make these tables look filled, and what was kept before would never be copied.
        await using var source = new NpgsqlConnection(sourceConnectionString);
        await source.OpenAsync(ct);
        foreach (var (table, _) in tables)
        {
            await using var exists = new NpgsqlCommand("select to_regclass(@name) is not null", source);
            exists.Parameters.AddWithValue("name", $"public.{table}");
            if (await exists.ExecuteScalarAsync(ct) is not true)
            {
                // A database that never had them: a deployment that began with Financials owning this.
                return;
            }
        }

        var target = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = target.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await target.OpenAsync(ct);
        }
        try
        {
            await using var transaction = await target.BeginTransactionAsync(ct);
            var copied = new List<string>();
            foreach (var (table, select) in tables)
            {
                var rows = 0;
                await using var read = new NpgsqlCommand(select, source);
                await using var reader = await read.ExecuteReaderAsync(ct);
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var insert = $"insert into {table} ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) values ({string.Join(", ", columns.Select((_, i) => $"${i + 1}"))})";
                // One command for the table, its values replaced row by row: a clicks table can be long.
                await using var write = new NpgsqlCommand(insert, target, transaction);
                write.Parameters.AddRange(columns.Select(_ => new NpgsqlParameter()).ToArray());
                while (await reader.ReadAsync(ct))
                {
                    for (var i = 0; i < columns.Count; i++)
                    {
                        write.Parameters[i].Value = reader.GetValue(i);
                    }
                    await write.ExecuteNonQueryAsync(ct);
                    rows++;
                }
                if (rows > 0)
                {
                    copied.Add($"{rows} from {table}");
                }
            }
            await transaction.CommitAsync(ct);
            if (copied.Count > 0)
            {
                logger.LogInformation("Copied {What} from where they were kept before: {Copied}", what, string.Join(", ", copied));
            }
        }
        finally
        {
            if (opened)
            {
                await target.CloseAsync();
            }
        }
    }
}
