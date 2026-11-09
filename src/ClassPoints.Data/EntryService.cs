using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

public sealed record AwardLine(
    string StudentId,
    Rule Rule,
    int RequestedDelta,
    bool ForceOverCap = false,
    string? Note = null,
    IReadOnlyList<string>? NoteTemplateIds = null);

public sealed record AwardOutcome(
    string BatchId,
    int Count,
    int Total,
    IReadOnlyList<Entry> Entries,
    IReadOnlyList<string> Warnings)
{
    public string SummaryText => Count == 1
        ? $"{Entries[0].StudentName} · {Entries[0].RuleLabel} {(Total >= 0 ? "+" : "")}{Total}"
        : $"给 {Count} 人 · {Entries[0].RuleLabel} {(Entries[0].Delta >= 0 ? "+" : "")}{Entries[0].Delta} · 合计 {(Total >= 0 ? "+" : "")}{Total}";
}

public enum ReversePermission
{
    Allowed,
    NeedsPassword,
    Denied,
}

/// <summary>
/// 积分流水：唯一事实表。只追加，总分永远 SUM(delta) 现算。
/// 撤销 = 插入一条 delta 取反、reverses_entry_id 指向原记录的冲销行 + 给原行打 reversed_at。
/// </summary>
public sealed class EntryService(Database db, AuditRepository audit)
{
    public Period CurrentPeriod(PeriodRepository periods) =>
        periods.Active() ?? throw new InvalidOperationException("没有 active 周期");

    // ---------------- 入账 ----------------

    public AwardOutcome ApplyBatch(IReadOnlyList<AwardLine> lines, Member op, string method,
        string periodId, long? occurredAt = null, string? sharedNote = null)
    {
        if (lines.Count == 0) throw new ArgumentException("没有要入账的记录", nameof(lines));

        var batchId = Ids.NewBatchId();
        var now = Clock.NowMs();
        var occurred = occurredAt ?? now;
        var warnings = new List<string>();
        var created = new List<Entry>();

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        foreach (var line in lines)
        {
            var already = line.Rule.HasCap && line.Rule.CapScope == CapScope.Period
                ? Convert.ToInt32(Database.ScalarTx(conn, tx, Sql.StudentRuleTotal,
                    ("$student", line.StudentId), ("$period", periodId), ("$rule", line.Rule.Id)))
                : 0;

            var cap = CapPolicy.Apply(line.Rule, line.RequestedDelta, already, line.ForceOverCap);
            if (!string.IsNullOrEmpty(cap.Message)) warnings.Add(cap.Message);
            if (cap.AppliedDelta == 0 && line.RequestedDelta != 0) continue;

            var entry = new Entry
            {
                Id = Ids.NewUuid7(),
                BatchId = batchId,
                PeriodId = periodId,
                StudentId = line.StudentId,
                RuleId = line.Rule.Id,
                RuleLabel = line.Rule.Label,
                RuleDeltaDefault = line.Rule.Delta,
                Delta = cap.AppliedDelta,
                Source = line.RequestedDelta != line.Rule.Delta ? "custom" : "rule",
                Note = string.IsNullOrWhiteSpace(line.Note) ? sharedNote : line.Note,
                NoteTemplateId = line.NoteTemplateIds is { Count: > 0 } ids ? string.Join(",", ids) : null,
                Capped = cap.Capped,
                OverCap = cap.OverCap,
                OperatorId = op.Id,
                OperatorName = op.DisplayName,
                IdentifyMethod = method,
                OccurredAt = occurred,
                CreatedAt = now,
                Synced = false,
            };

            InsertEntry(conn, tx, entry);
            SyncOutbox.Enqueue(conn, tx, entry.Id, "entry", new { action = "entry", entry });
            created.Add(entry);
        }

        if (created.Count == 0)
        {
            tx.Rollback();
            throw new InvalidOperationException(warnings.FirstOrDefault() ?? "没有可入账的记录");
        }

        // 理由标签使用频次
        foreach (var tpl in created.SelectMany(c => c.NoteTemplateId?.Split(',') ?? Array.Empty<string>()).Distinct())
            Database.Exec(conn, tx, "UPDATE note_templates SET use_count = use_count + 1, last_used_at = $now, updated_at = $now WHERE id = $id",
                ("$id", tpl), ("$now", now));

        tx.Commit();

        // 回填学生姓名，供界面直接显示
        foreach (var e in created)
            e.StudentName = StudentName(conn, e.StudentId);

        return new AwardOutcome(batchId, created.Count, created.Sum(c => c.Delta), created, warnings);
    }

    private static void InsertEntry(SqliteConnection conn, SqliteTransaction tx, Entry e)
    {
        Database.Exec(conn, tx, Sql.InsertEntry,
            ("$id", e.Id), ("$batch", e.BatchId), ("$period", e.PeriodId), ("$student", e.StudentId),
            ("$rule", e.RuleId), ("$label", e.RuleLabel), ("$ruleDefault", e.RuleDeltaDefault),
            ("$delta", e.Delta), ("$source", e.Source), ("$note", e.Note), ("$noteTpl", e.NoteTemplateId),
            ("$capped", e.Capped ? 1 : 0), ("$overCap", e.OverCap ? 1 : 0),
            ("$opId", e.OperatorId), ("$opName", e.OperatorName), ("$method", e.IdentifyMethod),
            ("$occurred", e.OccurredAt), ("$created", e.CreatedAt),
            // 冲销行必须带上它指向哪一条：界面靠它判"已撤销"，
            // uq_entries_reversal 靠它保证同一批不会被撤两次，同步也靠它让另一端认出来
            ("$reverses", e.ReversesEntryId), ("$reversedAt", e.ReversedAt), ("$reversedBy", e.ReversedBy),
            ("$synced", e.Synced ? 1 : 0));
    }

    private static string StudentName(SqliteConnection conn, string studentId)
    {
        var v = Database.Scalar(conn, "SELECT name FROM students WHERE id = $id", ("$id", studentId));
        return v is DBNull or null ? "" : v.ToString()!;
    }

    // ---------------- 撤销 ----------------

    /// <summary>
    /// 撤销权限：班委 5 分钟内的自己的批次不用密码；撤别人的、撤跨天的都要密码。
    /// 密码在这里不是身份认证，是防误操作闸。
    /// </summary>
    public static ReversePermission CheckReversePermission(IReadOnlyList<Entry> rows, Member actor, bool isAdmin, long nowMs)
    {
        if (rows.Count == 0) return ReversePermission.Denied;
        if (isAdmin || actor.IsManager) return ReversePermission.Allowed;

        var allMine = rows.All(r => r.OperatorId == actor.Id);
        var within5 = rows.All(r => nowMs - r.CreatedAt <= 5 * 60 * 1000);
        if (allMine && within5) return ReversePermission.Allowed;
        return ReversePermission.NeedsPassword;
    }

    public List<Entry> BatchRows(string batchId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.BatchRows;
        cmd.Parameters.AddWithValue("$batch", batchId);
        using var r = cmd.ExecuteReader();
        var list = new List<Entry>();
        while (r.Read()) list.Add(Mappers.Entry(r));
        return list;
    }

    public int ReversibleCount(string batchId)
    {
        using var conn = db.Open();
        return Convert.ToInt32(Database.Scalar(conn, Sql.ReversibleInBatch, ("$batch", batchId)));
    }

    public bool IsBatchPending(string batchId)
    {
        using var conn = db.Open();
        var pending = Convert.ToInt32(Database.Scalar(conn,
            "SELECT COUNT(*) FROM entries WHERE batch_id = $b AND synced = 0", ("$b", batchId)));
        return pending > 0;
    }

    /// <summary>
    /// 情况 #1：还在 outbox 里、30 秒内的记录，直接物理删除 —— 等于这事没发生过。
    /// 超过 30 秒一律走冲销行，宁可多一条记录，也不要把分数算错。
    /// </summary>
    public bool TryPhysicalUndo(string batchId, long nowMs)
    {
        using var conn = db.Open();
        var createdAt = Database.Scalar(conn, "SELECT MIN(created_at) FROM entries WHERE batch_id = $b", ("$b", batchId));
        if (createdAt is DBNull or null) return false;
        var created = Convert.ToInt64(createdAt);
        if (nowMs - created > 30_000) return false;
        if (!IsBatchPending(batchId)) return false;

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM entries WHERE batch_id = $b";
            cmd.Parameters.AddWithValue("$b", batchId);
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read()) ids.Add(r.GetString(0));
            r.Close();
            foreach (var id in ids)
            {
                Database.Exec(conn, tx, "DELETE FROM outbox WHERE id = $id", ("$id", id));
                Database.Exec(conn, tx, "DELETE FROM entries WHERE id = $id", ("$id", id));
            }
        }
        audit.Add("undo_pending", target: batchId, detail: "未推送批次物理撤销");
        tx.Commit();
        return true;
    }

    /// <summary>撤销一整批。先插冲销行，后打标记 —— 反过来的话失败会留下「标了已撤销但没有冲销行」的账。</summary>
    public int ReverseBatch(string batchId, Member actor, string method)
    {
        var now = Clock.NowMs();
        var rows = BatchRows(batchId).Where(r => !r.IsReversal && !r.IsReversed).ToList();
        if (rows.Count == 0) return 0;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        foreach (var row in rows)
        {
            var reversal = new Entry
            {
                Id = Ids.NewUuid7(),
                BatchId = row.BatchId,
                PeriodId = row.PeriodId,
                StudentId = row.StudentId,
                RuleId = row.RuleId,
                RuleLabel = row.RuleLabel,
                RuleDeltaDefault = row.RuleDeltaDefault,
                Delta = -row.Delta,
                Source = row.Source,
                Note = row.Note,
                NoteTemplateId = row.NoteTemplateId,
                Capped = false,
                OverCap = row.OverCap,
                OperatorId = actor.Id,
                OperatorName = actor.DisplayName,
                IdentifyMethod = method,
                OccurredAt = now,
                CreatedAt = now,
                ReversesEntryId = row.Id,
                Synced = false,
            };
            InsertEntry(conn, tx, reversal);
            SyncOutbox.Enqueue(conn, tx, reversal.Id, "reverse", new { action = "entry", entry = reversal });
        }

        Database.Exec(conn, tx, Sql.MarkBatchReversed,
            ("$batch", batchId), ("$at", now), ("$by", actor.DisplayName));
        Database.Exec(conn, tx,
            "INSERT INTO audit_log (at, actor_id, action, target, detail) VALUES ($at, $a, 'revoke_batch', $t, $d)",
            ("$at", now), ("$a", actor.Id), ("$t", batchId), ("$d", $"撤销 {rows.Count} 条"));

        tx.Commit();
        return rows.Count;
    }

    public int ReverseSingle(string entryId, Member actor, string method)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.EntryById;
        cmd.Parameters.AddWithValue("$id", entryId);
        Entry? row;
        using (var r = cmd.ExecuteReader())
            row = r.Read() ? Mappers.Entry(r) : null;

        if (row is null || row.IsReversal || row.IsReversed) return 0;

        using var tx = conn.BeginTransaction();
        var reversal = new Entry
        {
            Id = Ids.NewUuid7(),
            BatchId = row.BatchId,
            PeriodId = row.PeriodId,
            StudentId = row.StudentId,
            RuleId = row.RuleId,
            RuleLabel = row.RuleLabel,
            RuleDeltaDefault = row.RuleDeltaDefault,
            Delta = -row.Delta,
            Source = row.Source,
            Note = row.Note,
            NoteTemplateId = row.NoteTemplateId,
            Capped = false,
            OverCap = row.OverCap,
            OperatorId = actor.Id,
            OperatorName = actor.DisplayName,
            IdentifyMethod = method,
            OccurredAt = now,
            CreatedAt = now,
            ReversesEntryId = row.Id,
            Synced = false,
        };
        InsertEntry(conn, tx, reversal);
        Database.Exec(conn, tx, Sql.MarkSingleReversed,
            ("$id", entryId), ("$at", now), ("$by", actor.DisplayName));
        SyncOutbox.Enqueue(conn, tx, reversal.Id, "reverse", new { action = "entry", entry = reversal });
        Database.Exec(conn, tx,
            "INSERT INTO audit_log (at, actor_id, action, target, detail) VALUES ($at, $a, 'revoke_entry', $t, $d)",
            ("$at", now), ("$a", actor.Id), ("$t", entryId), ("$d", $"撤销单条 {row.Delta}"));
        tx.Commit();
        return 1;
    }

    /// <summary>撤错了用「重做」：一条干净的新记录，不做「撤销的撤销」。</summary>
    public Entry Redo(Entry original, Member actor, string method, string periodId)
    {
        var now = Clock.NowMs();
        var entry = new Entry
        {
            Id = Ids.NewUuid7(),
            BatchId = Ids.NewBatchId(),
            PeriodId = periodId,
            StudentId = original.StudentId,
            RuleId = original.RuleId,
            RuleLabel = original.RuleLabel,
            RuleDeltaDefault = original.RuleDeltaDefault,
            Delta = original.Delta,
            Source = original.Source,
            Note = string.IsNullOrWhiteSpace(original.Note) ? "重做" : original.Note + " · 重做",
            Capped = false,
            OverCap = false,
            OperatorId = actor.Id,
            OperatorName = actor.DisplayName,
            IdentifyMethod = method,
            OccurredAt = now,
            CreatedAt = now,
            Synced = false,
            StudentName = original.StudentName,
        };

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        InsertEntry(conn, tx, entry);
        SyncOutbox.Enqueue(conn, tx, entry.Id, "entry", new { action = "entry", entry });
        tx.Commit();
        return entry;
    }

    /// <summary>入账后 5 秒内可补标签：写进整批记录。</summary>
    public void AppendNote(string batchId, string note, string? templateId)
    {
        using var conn = db.Open();
        Database.Exec(conn, "UPDATE entries SET note = $note, note_template_id = $tpl WHERE batch_id = $b AND reverses_entry_id IS NULL",
            ("$b", batchId), ("$note", note), ("$tpl", templateId));
        if (templateId is not null)
            Database.Exec(conn, "UPDATE note_templates SET use_count = use_count + 1, last_used_at = $now WHERE id = $id",
                ("$id", templateId), ("$now", Clock.NowMs()));
    }

    // ---------------- 查询 ----------------

    public Dictionary<string, int> PeriodTotals(string periodId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.PeriodTotals;
        cmd.Parameters.AddWithValue("$period", periodId);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    public Dictionary<string, int> TermTotals(string termKey)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.TermTotals;
        cmd.Parameters.AddWithValue("$term", termKey);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    public Dictionary<string, int> TodayDeltas(string periodId, DateTime day)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.TodayDeltas;
        cmd.Parameters.AddWithValue("$period", periodId);
        cmd.Parameters.AddWithValue("$from", Clock.StartOfDay(day));
        cmd.Parameters.AddWithValue("$to", Clock.StartOfDay(day.AddDays(1)));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    public int PeriodPointsAwarded(string periodId)
    {
        using var conn = db.Open();
        return Convert.ToInt32(Database.Scalar(conn,
            "SELECT COALESCE(SUM(delta), 0) FROM entries WHERE period_id = $p", ("$p", periodId)));
    }

    public List<Entry> Entries(string periodId, int limit = 200, int offset = 0)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.EntriesByPeriod;
        cmd.Parameters.AddWithValue("$period", periodId);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var r = cmd.ExecuteReader();
        var list = new List<Entry>();
        while (r.Read()) list.Add(Mappers.Entry(r));
        return list;
    }

    public List<Entry> EntriesForStudent(string studentId, int limit = 20)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.EntriesByStudent;
        cmd.Parameters.AddWithValue("$student", studentId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Entry>();
        while (r.Read()) list.Add(Mappers.Entry(r));
        return list;
    }

    public List<(string Label, int Count, int Points)> RuleDistribution(string periodId, string? studentId = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = studentId is null ? Sql.ClassRuleDistribution : Sql.RuleDistribution;
        cmd.Parameters.AddWithValue("$period", periodId);
        if (studentId is not null) cmd.Parameters.AddWithValue("$student", studentId);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, int, int)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    }

    public List<(string Group, int Points, int Members)> GroupTotals(string periodId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.GroupTotals;
        cmd.Parameters.AddWithValue("$period", periodId);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, int, int)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    }

    public int StudentRuleTotal(string studentId, string ruleId, string periodId)
    {
        using var conn = db.Open();
        return Convert.ToInt32(Database.Scalar(conn, Sql.StudentRuleTotal,
            ("$student", studentId), ("$period", periodId), ("$rule", ruleId)));
    }
}
