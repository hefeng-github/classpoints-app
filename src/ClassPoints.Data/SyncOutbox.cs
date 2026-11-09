using System.Text.Json;
using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

public sealed record OutboxItem(string Id, string Kind, string Payload, int Attempts, long NextAt);

/// <summary>
/// 待推送队列。写本地成功即入队，推成功才出队。
/// 断网、断电后靠它重放；entries 用客户端 UUID 做主键，重传天然幂等。
/// </summary>
public sealed class SyncOutbox(Database db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Enqueue(string id, string kind, object payload)
    {
        using var conn = db.Open();
        Enqueue(conn, null, id, kind, payload);
    }

    public static void Enqueue(SqliteConnection conn, SqliteTransaction? tx, string id, string kind, object payload)
    {
        var now = Clock.NowMs();
        var json = JsonSerializer.Serialize(payload, Json);
        Database.Exec(conn, tx, Sql.EnqueueOutbox,
            ("$id", id), ("$kind", kind), ("$payload", json), ("$next", now), ("$now", now));
    }

    public int Count()
    {
        using var conn = db.Open();
        return Convert.ToInt32(Database.Scalar(conn, Sql.OutboxCount));
    }

    public List<OutboxItem> Due(int limit = 50)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.DueOutbox;
        cmd.Parameters.AddWithValue("$now", Clock.NowMs());
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<OutboxItem>();
        while (r.Read())
            list.Add(new OutboxItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4)));
        return list;
    }

    public void Remove(string id)
    {
        using var conn = db.Open();
        Database.Exec(conn, Sql.DeleteOutbox, ("$id", id));
    }

    public void Backoff(OutboxItem item)
    {
        var delay = System.Math.Min(60_000, 1000 * (int)System.Math.Pow(2, System.Math.Min(6, item.Attempts)));
        using var conn = db.Open();
        Database.Exec(conn, Sql.BumpOutboxAttempt, ("$id", item.Id), ("$next", Clock.NowMs() + delay));
    }
}

internal static class ReaderExtensions
{
    public static long GetLong(this SqliteDataReader r, string col) => r.GetInt64(r.GetOrdinal(col));
    public static int GetInt(this SqliteDataReader r, string col) => r.GetInt32(r.GetOrdinal(col));
    public static double GetDouble(this SqliteDataReader r, string col) => r.GetDouble(r.GetOrdinal(col));
    public static string GetStr(this SqliteDataReader r, string col) => r.GetString(r.GetOrdinal(col));

    public static string? GetStrOrNull(this SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static long? GetLongOrNull(this SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    public static int? GetIntOrNull(this SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    public static double? GetDoubleOrNull(this SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    public static bool GetBool(this SqliteDataReader r, string col) => r.GetInt32(r.GetOrdinal(col)) != 0;

    public static bool HasColumn(this SqliteDataReader r, string col)
    {
        for (var i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public static class Mappers
{
    public static Student Student(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        Name = r.GetStr("name"),
        StudentNo = r.GetStrOrNull("student_no"),
        SeatNo = r.GetIntOrNull("seat_no"),
        GroupName = r.GetStrOrNull("group_name"),
        Active = r.GetBool("active"),
        Rev = r.GetInt("rev"),
        UpdatedAt = r.GetLong("updated_at"),
        DeletedAt = r.GetLongOrNull("deleted_at"),
        CreatedAt = r.GetLong("created_at"),
    };

    public static Rule Rule(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        Label = r.GetStr("label"),
        Delta = r.GetInt("delta"),
        Icon = r.GetStrOrNull("icon") ?? IconCatalog.Default,
        Sort = r.GetInt("sort"),
        PresetKey = r.GetStrOrNull("preset_key"),
        CapValue = r.GetIntOrNull("cap_value"),
        CapScope = r.GetStrOrNull("cap_scope") switch
        {
            "single" => CapScope.Single,
            "period" => CapScope.Period,
            _ => CapScope.None,
        },
        ArchivedAt = r.GetLongOrNull("archived_at"),
        Rev = r.GetInt("rev"),
        UpdatedAt = r.GetLong("updated_at"),
        DeletedAt = r.GetLongOrNull("deleted_at"),
    };

    public static Member Member(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        DisplayName = r.GetStr("display_name"),
        Role = MemberRoleNames.Parse(r.GetStrOrNull("role")),
        PinHash = r.GetStrOrNull("pin_hash"),
        CreatedAt = r.GetLong("created_at"),
        RevokedAt = r.GetLongOrNull("revoked_at"),
        Rev = r.GetInt("rev"),
        UpdatedAt = r.GetLong("updated_at"),
        DeletedAt = r.GetLongOrNull("deleted_at"),
    };

    public static Identity Identity(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        MemberId = r.GetStr("member_id"),
        Method = r.GetStr("method"),
        Credential = r.GetStr("credential"),
        Label = r.GetStrOrNull("label"),
        CreatedAt = r.GetLong("created_at"),
        LastUsedAt = r.GetLongOrNull("last_used_at"),
    };

    public static NoteTemplate Note(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        Text = r.GetStr("text"),
        UseCount = r.GetInt("use_count"),
        LastUsedAt = r.GetLong("last_used_at"),
        ArchivedAt = r.GetLongOrNull("archived_at"),
        Rev = r.GetInt("rev"),
        UpdatedAt = r.GetLong("updated_at"),
        DeletedAt = r.GetLongOrNull("deleted_at"),
    };

    public static Period Period(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        Name = r.GetStr("name"),
        TermKey = r.GetStr("term_key"),
        StartAt = r.GetLong("start_at"),
        EndAt = r.GetLongOrNull("end_at"),
        Status = r.GetStr("status") == "archived" ? PeriodStatus.Archived : PeriodStatus.Active,
        CreatedAt = r.GetLong("created_at"),
    };

    public static Entry Entry(SqliteDataReader r) => new()
    {
        Seq = r.GetLong("seq"),
        Id = r.GetStr("id"),
        BatchId = r.GetStr("batch_id"),
        PeriodId = r.GetStr("period_id"),
        StudentId = r.GetStr("student_id"),
        RuleId = r.GetStrOrNull("rule_id"),
        RuleLabel = r.GetStrOrNull("rule_label"),
        RuleDeltaDefault = r.GetIntOrNull("rule_delta_default"),
        Delta = r.GetInt("delta"),
        Source = r.GetStrOrNull("source") ?? "rule",
        Note = r.GetStrOrNull("note"),
        NoteTemplateId = r.GetStrOrNull("note_template_id"),
        Capped = r.GetBool("capped"),
        OverCap = r.GetBool("over_cap"),
        OperatorId = r.GetStr("operator_id"),
        OperatorName = r.GetStr("operator_name"),
        IdentifyMethod = r.GetStrOrNull("identify_method"),
        OccurredAt = r.GetLong("occurred_at"),
        CreatedAt = r.GetLong("created_at"),
        ReversesEntryId = r.GetStrOrNull("reverses_entry_id"),
        ReversedAt = r.GetLongOrNull("reversed_at"),
        ReversedBy = r.GetStrOrNull("reversed_by"),
        Synced = r.GetBool("synced"),
        StudentName = r.HasColumn("student_name") ? r.GetStrOrNull("student_name") ?? "" : "",
    };

    public static Exam Exam(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        PeriodId = r.GetStr("period_id"),
        Name = r.GetStr("name"),
        FullScore = r.GetDouble("full_score"),
        ExamDate = r.GetStrOrNull("exam_date"),
        ImportedAt = r.GetLong("imported_at"),
        SourceFile = r.GetStrOrNull("source_file"),
    };

    public static Scheme Scheme(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        Name = r.GetStr("name"),
        ExamWeight = r.GetDouble("exam_weight"),
        DailyWeight = r.GetDouble("daily_weight"),
        Normalize = r.GetBool("normalize"),
        IsActive = r.GetBool("is_active"),
        CreatedAt = r.GetLong("created_at"),
    };

    public static Counter Counter(SqliteDataReader r) => new()
    {
        Id = r.GetStr("id"),
        Label = r.GetStr("label"),
        Icon = r.GetStrOrNull("icon") ?? IconCatalog.Flag,
        Sort = r.GetInt("sort"),
        PresetKey = r.GetStrOrNull("preset_key"),
        ArchivedAt = r.GetLongOrNull("archived_at"),
        Rev = r.GetInt("rev"),
        UpdatedAt = r.GetLong("updated_at"),
        DeletedAt = r.GetLongOrNull("deleted_at"),
    };

    public static CounterTick CounterTick(SqliteDataReader r) => new()
    {
        Seq = r.GetLong("seq"),
        Id = r.GetStr("id"),
        CounterId = r.GetStr("counter_id"),
        StudentId = r.GetStr("student_id"),
        PeriodId = r.GetStr("period_id"),
        Value = r.GetInt("value"),
        Note = r.GetStrOrNull("note"),
        OperatorId = r.GetStr("operator_id"),
        OperatorName = r.GetStr("operator_name"),
        OccurredAt = r.GetLong("occurred_at"),
        CreatedAt = r.GetLong("created_at"),
        StudentName = r.HasColumn("student_name") ? r.GetStrOrNull("student_name") ?? "" : "",
        CounterLabel = r.HasColumn("counter_label") ? r.GetStrOrNull("counter_label") ?? "" : "",
    };
}
