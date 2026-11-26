using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Privacy.IntegrationTests;

/// <summary>
/// A member's data in one service's database, without knowing the service: rows are made from the model for every
/// table that names a member, and the database is searched, column by column, for a member's id.
/// </summary>
public static partial class MemberData
{
    /// <summary>
    /// A column that holds a member's id, by its name: <c>UserId</c> and its like (<c>OwnerUserId</c>), the two sides of a
    /// follow or a block, and who did something (<c>SavedById</c>).
    /// </summary>
    [GeneratedRegex("(UserId|FollowerId|FolloweeId|BlockerId|BlockedId|ById|ModeratorId)$")]
    private static partial Regex MemberColumn();

    /// <summary>
    /// A column that holds a member's name or address beside their id: what a row shows of them.
    /// </summary>
    [GeneratedRegex("(DisplayName|ModeratorName|ByName|ReviewedBy|UpdatedBy|Email)$")]
    private static partial Regex NameColumn();

    /// <summary>
    /// A column whose name says it might be about a person: an id, who did something, a name or an address.
    /// </summary>
    [GeneratedRegex("(Id|By|Name|Email)$")]
    private static partial Regex MaybeAboutAPerson();

    /// <summary>
    /// Text columns whose names might be about a person (<see cref="MaybeAboutAPerson"/>) that this class neither treats
    /// as a member's id or name nor has been told aren't one, as "table.Column". A new such column lands here until
    /// someone says which it is, so it can't slip past the search unnoticed.
    /// </summary>
    /// <param name="notAboutAMember">Column names that are about something else (a show, a provider, a program).</param>
    public static IReadOnlyList<string> Undecided(DbContext db, IReadOnlySet<string> notAboutAMember) => db.Model.GetEntityTypes()
        .Where(t => t.GetTableName() is not null && !t.IsOwned())
        .SelectMany(t => t.GetProperties()
            .Where(p => p.ClrType == typeof(string) && MaybeAboutAPerson().IsMatch(p.Name) && !IsMember(p) && !NameColumn().IsMatch(p.Name)
                && !notAboutAMember.Contains(p.Name))
            .Select(p => $"{t.GetTableName()}.{p.Name}"))
        .Order().ToList();

    /// <summary>
    /// Columns named like a member's id that aren't text, as "table.Column": <see cref="SeedAsync"/> puts a member's id
    /// only in text ones, so one of these would go unfilled and unsearched without anyone noticing.
    /// </summary>
    public static IReadOnlyList<string> NotText(DbContext db) => db.Model.GetEntityTypes()
        .Where(t => t.GetTableName() is not null && !t.IsOwned())
        .SelectMany(t => t.GetProperties()
            .Where(p => p.ClrType != typeof(string) && MemberColumn().IsMatch(p.Name))
            .Select(p => $"{t.GetTableName()}.{p.Name}"))
        .Order().ToList();

    /// <summary>
    /// The name rows are given for a member, so it can be searched for like their id.
    /// </summary>
    public static string NameOf(string memberId) => "name-" + memberId;

    private static int number = 1;

    private static readonly DateTimeOffset Started = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public sealed record Seeded(IReadOnlyList<string> Tables, IReadOnlyList<string> Failures);

    /// <summary>
    /// The tables whose rows name a member, from the model.
    /// </summary>
    public static IReadOnlyList<string> MemberTables(DbContext db) =>
        MemberTypes(db).Select(t => t.GetTableName()!).Distinct().Order().ToList();

    private static IEnumerable<IEntityType> MemberTypes(DbContext db) => db.Model.GetEntityTypes()
        .Where(t => t.GetTableName() is not null && !t.IsOwned() && t.GetProperties().Any(IsMember));

    private static bool IsMember(IProperty property) => property.ClrType == typeof(string) && MemberColumn().IsMatch(property.Name);

    /// <summary>
    /// Adds one row for <paramref name="memberId"/> to every table that names a member, with whatever the row needs (the
    /// rows it refers to are made too). A row that couldn't be made from the model alone is reported, not skipped quietly.
    /// </summary>
    public static async Task<Seeded> SeedAsync(DbContext db, string memberId, CancellationToken ct)
    {
        var (tables, failures) = (new List<string>(), new List<string>());
        foreach (var type in MemberTypes(db).ToList())
        {
            try
            {
                Row(db, type, memberId, []);
                await db.SaveChangesAsync(ct);
                tables.Add(type.GetTableName()!);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or NpgsqlException)
            {
                failures.Add($"{type.GetTableName()}: {(ex.InnerException ?? ex).Message.Split('\n')[0]}");
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return new Seeded(tables, failures);
    }

    private static object Row(DbContext db, IEntityType type, string memberId, HashSet<IEntityType> making)
    {
        making.Add(type);
        var row = type.ClrType.GetConstructor(Type.EmptyTypes) is not null
            ? Activator.CreateInstance(type.ClrType)!
            : RuntimeHelpers.GetUninitializedObject(type.ClrType);
        var entry = db.Entry(row);
        foreach (var property in type.GetProperties().Where(p => !p.IsShadowProperty() || p.IsForeignKey()))
        {
            if (property.IsForeignKey() || property.ValueGenerated != ValueGenerated.Never && property.IsPrimaryKey() && property.ClrType != typeof(Guid))
            {
                // Set from the row it refers to, below; or made up by the database.
                continue;
            }
            if (Value(property, memberId) is { } value)
            {
                entry.Property(property.Name).CurrentValue = value;
            }
        }
        // The rows this one refers to, unless that would go round in a circle and the reference may be left empty.
        foreach (var key in type.GetForeignKeys())
        {
            if (making.Contains(key.PrincipalEntityType) && !key.IsRequired)
            {
                continue;
            }
            var principal = making.Contains(key.PrincipalEntityType) && key.PrincipalEntityType == type
                ? row
                : Row(db, key.PrincipalEntityType, memberId, making);
            var principalEntry = db.Entry(principal);
            foreach (var (dependent, target) in key.Properties.Zip(key.PrincipalKey.Properties))
            {
                entry.Property(dependent.Name).CurrentValue = principalEntry.Property(target.Name).CurrentValue;
            }
        }
        db.Add(row);
        making.Remove(type);
        return row;
    }

    /// <summary>
    /// A value the column will take: the member's id where it names a member, else something of the right type and
    /// unlike any other row's, so unique indexes don't meet. Null leaves a column that may be empty as it is.
    /// </summary>
    private static object? Value(IProperty property, string memberId)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (IsMember(property))
        {
            return memberId;
        }
        if (property.ClrType == typeof(string) && NameColumn().IsMatch(property.Name))
        {
            return NameOf(memberId);
        }
        if (property.IsNullable && !property.IsKey())
        {
            return null;
        }
        var next = Interlocked.Increment(ref number);
        return type switch
        {
            // A text column kept as JSON takes only JSON.
            _ when type == typeof(string) && property.GetColumnType() is "jsonb" or "json" => "{}",
            _ when type == typeof(string) => Token(property.GetMaxLength()),
            _ when type == typeof(Guid) => Guid.NewGuid(),
            _ when type == typeof(int) => next,
            _ when type == typeof(long) => (long)next,
            _ when type == typeof(short) => (short)(next % 30000),
            _ when type == typeof(double) => 1.0,
            _ when type == typeof(float) => 1f,
            _ when type == typeof(decimal) => 1m,
            _ when type == typeof(bool) => false,
            // Each its own whole second, so a time found somewhere else is this row's and no other's.
            _ when type == typeof(DateTime) => Started.UtcDateTime.AddSeconds(-next),
            _ when type == typeof(DateTimeOffset) => Started.AddSeconds(-next),
            _ when type == typeof(DateOnly) => DateOnly.FromDateTime(DateTime.UtcNow),
            _ when type == typeof(byte[]) => new byte[] { 1 },
            _ when type == typeof(List<string>) => new List<string>(),
            _ when type == typeof(string[]) => Array.Empty<string>(),
            _ when type == typeof(List<Guid>) => new List<Guid>(),
            _ when type == typeof(List<int>) => new List<int>(),
            _ when type.IsEnum => Enum.GetValues(type).GetValue(0),
            _ => null
        };
    }

    private static string Token(int? maxLength)
    {
        var token = Guid.NewGuid().ToString("N");
        return maxLength is { } max && max < token.Length ? token[..max] : token;
    }

    /// <summary>
    /// Every place the database mentions <paramref name="memberId"/> or the name its rows were given
    /// (<see cref="NameOf"/>, which has the id in it): any column of any type whose text has the id anywhere inside.
    /// Each as "table.column", with how many rows.
    /// </summary>
    public static async Task<Dictionary<string, long>> MentionsAsync(DbContext db, string memberId, CancellationToken ct)
    {
        var connection = await OpenAsync(db, ct);
        // Every column, whatever its type, read as text: an id kept as a uuid, in a list or inside JSON is found too.
        var columns = new List<(string Table, string Column)>();
        await using (var list = new NpgsqlCommand("select table_name, column_name from information_schema.columns where table_schema = 'public'", connection))
        await using (var reader = await list.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var found = new Dictionary<string, long>();
        foreach (var (table, column) in columns)
        {
            await using var count = new NpgsqlCommand($"select count(*) from \"{table}\" where \"{column}\"::text like '%' || @id || '%'", connection);
            count.Parameters.AddWithValue("id", memberId);
            if ((long)(await count.ExecuteScalarAsync(ct))! is > 0 and var rows)
            {
                found[$"{table}.{column}"] = rows;
            }
        }
        return found;
    }

    /// <summary>
    /// For each table that names a member, the values in <paramref name="memberId"/>'s rows that no other row has: ids
    /// and the made-up text <see cref="SeedAsync"/> filled in. Finding one of them somewhere (an export) shows the row
    /// got there. Times count too, since <see cref="SeedAsync"/> gives each its own second; numbers and switches don't, and
    /// neither does a column that points at another row.
    /// </summary>
    public static async Task<Dictionary<string, List<string>>> DistinctiveAsync(DbContext db, string memberId, CancellationToken ct)
    {
        var connection = await OpenAsync(db, ct);
        var (found, held) = (new Dictionary<string, List<string>>(), new Dictionary<string, HashSet<string>>());
        foreach (var type in MemberTypes(db))
        {
            var table = type.GetTableName()!;
            var where = string.Join(" or ", type.GetProperties().Where(IsMember).Select(p => $"\"{p.GetColumnName()}\" = @id"));
            // Not the columns that point at another row: that row's id found in an export says nothing of this one.
            var pointers = type.GetProperties().Where(p => p.IsForeignKey()).Select(p => p.GetColumnName()).ToHashSet();
            await using var read = new NpgsqlCommand($"select * from \"{table}\" where {where}", connection);
            read.Parameters.AddWithValue("id", memberId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            var values = found.TryGetValue(table, out var known) ? known : found[table] = [];
            var all = held.TryGetValue(table, out var before) ? before : held[table] = [];
            while (await reader.ReadAsync(ct))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    string[] read1 = reader.GetValue(i) switch
                    {
                        Guid id => [id.ToString()],
                        string text when text.Length >= 16 && !text.Contains(memberId, StringComparison.Ordinal) && !text.StartsWith('{') => [text],
                        DateTime at => Stamps(at),
                        DateTimeOffset at => Stamps(at.UtcDateTime),
                        _ => []
                    };
                    all.UnionWith(read1);
                    if (!pointers.Contains(reader.GetName(i)))
                    {
                        values.AddRange(read1);
                    }
                }
            }
        }
        // Only what one table alone holds, in any column: an id two tables share (a vote and the watch it hangs on)
        // found in an export shows one of them got there, not which.
        var shared = held.Values.SelectMany(values => values).GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var values in found.Values)
        {
            values.RemoveAll(shared.Contains);
        }
        return found;
    }

    private static async Task<NpgsqlConnection> OpenAsync(DbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        return (NpgsqlConnection)db.Database.GetDbConnection();
    }

    /// <summary>
    /// A time as JSON writes it, quotes and all, so only the whole value matches: as a UTC time, as one with an offset,
    /// and as one that says neither.
    /// </summary>
    private static string[] Stamps(DateTime at)
    {
        var text = at.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture);
        return [$"\"{text}Z\"", $"\"{text}+00:00\"", $"\"{text}\""];
    }

    /// <summary>
    /// Any service's context, from its options.
    /// </summary>
    public static TContext Create<TContext>(DbContextOptions<TContext> options) where TContext : DbContext =>
        (TContext)Activator.CreateInstance(typeof(TContext), options)!;
}
