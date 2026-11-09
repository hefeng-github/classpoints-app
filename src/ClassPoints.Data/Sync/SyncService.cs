using System.Text.Json.Nodes;
using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data.Sync;

public sealed record SyncReport(
    bool Ok,
    string? Error,
    int Pushed,
    int Pending,
    int Pulled,
    long Next,
    DateTime At)
{
    /// <summary>建班时把班级码回传给向导显示，其余场景为 null。</summary>
    public string? CreatedCode { get; init; }

    public static SyncReport Skipped(string reason) => new(false, reason, 0, 0, 0, 0, DateTime.Now);
}

/// <summary>
/// 同步编排：先推、再拉。
///
/// 两条铁律（方案 5.8）：
/// 1. 本地写入永远先成功 —— 这个类只读 outbox，绝不在用户点"加一分"的路径上。
/// 2. 拉取失败不报错 —— 界面只用角标显示"待同步 3 条"和"最后同步：2 分钟前"。
///
/// /sync 返回的是库里的原始列名（snake_case），和本地 SQLite 的列名一一对应，
/// 所以这里没有任何字段映射层，直接按列名写回。
/// </summary>
public sealed class SyncService(Database db, SyncOutbox outbox, SyncStore? store = null)
{
    private readonly SyncStore _store = store ?? new SyncStore();
    private SyncClient? _client;
    private bool _busy;

    public const string KeyLastSeq = "last_seq";
    public const string KeyLastPullAt = "last_pull_at";

    public bool IsConfigured => _store.Load() is not null;

    public long LastSeq => ReadState(KeyLastSeq) ?? 0;

    public DateTime LastPullAt
    {
        get
        {
            var ms = ReadState(KeyLastPullAt) ?? 0;
            return ms <= 0 ? DateTime.MinValue : Clock.ToLocal(ms).DateTime;
        }
    }

    public string LastSyncText
    {
        get
        {
            if (!IsConfigured) return "未配置同步";
            var at = LastPullAt;
            if (at == DateTime.MinValue) return "还没同步过";
            var gap = DateTime.Now - at;
            return gap.TotalSeconds < 60 ? "刚刚同步"
                : gap.TotalMinutes < 60 ? $"{gap.TotalMinutes:0} 分钟前"
                : gap.TotalHours < 24 ? $"{gap.TotalHours:0} 小时前"
                : at.ToString("MM-dd HH:mm");
        }
    }

    // ---------------- 配对 ----------------

    public async Task<SyncReport> CreateClassAsync(string baseUrl, string name, string ownerName,
        string ownerPin, string? recoveryCode, string kind, CancellationToken ct = default)
    {
        using var client = new SyncClient(baseUrl);
        var (classId, code, access, refresh) = await client.CreateClassAsync(name, ownerName, ownerPin, recoveryCode, kind, ct);
        _store.Save(new SyncCredentials(baseUrl, classId, access, refresh, code));
        return new SyncReport(true, null, 0, outbox.Count(), 0, 0, DateTime.Now) { CreatedCode = code };
    }

    public async Task<SyncReport> PairAsync(string baseUrl, string code, string secret, string kind,
        CancellationToken ct = default)
    {
        using var client = new SyncClient(baseUrl);
        var (classId, access, refresh) = await client.PairAsync(code, secret, kind, ct);
        _store.Save(new SyncCredentials(baseUrl, classId, access, refresh, code));
        return new SyncReport(true, null, 0, outbox.Count(), 0, 0, DateTime.Now);
    }

    public void SignOut() => _store.Clear();

    // ---------------- 一轮同步 ----------------

    /// <summary>
    /// 推完再拉。返回的报告只用于界面角标，不用来判断业务成败 ——
    /// 断网了本地照样记分，这才是这个应用能不能用的分界线。
    /// </summary>
    public async Task<SyncReport> RunOnceAsync(int maxPages = 5, CancellationToken ct = default)
    {
        if (_busy) return SyncReport.Skipped("上一轮还没跑完");
        var credentials = _store.Load();
        if (credentials is null) return SyncReport.Skipped("还没配对");

        _busy = true;
        try
        {
            _client = new SyncClient(credentials.BaseUrl, credentials.Access);
            var pushed = 0;
            string? pushError = null;

            // ---- 推 ----
            var due = outbox.Due(200);
            if (due.Count > 0)
            {
                var outcome = await _client.PushAsync(due, ct);

                if (outcome.AuthExpired)
                {
                    var refreshed = await TryRefreshAsync(credentials, ct);
                    if (refreshed is null)
                        return new SyncReport(false, "同步凭据失效，请重新配对", 0, outbox.Count(), 0, LastSeq, DateTime.Now);

                    _client.SetAccess(refreshed.Access);
                    credentials = refreshed;
                    outcome = await _client.PushAsync(due, ct);
                }

                foreach (var id in outcome.Done) outbox.Remove(id);
                foreach (var id in outcome.Failed)
                {
                    var item = due.FirstOrDefault(i => i.Id == id);
                    if (item is not null) outbox.Backoff(item);
                }
                pushed = outcome.Done.Count;
                pushError = outcome.Failed.Count > 0 ? outcome.LastError : null;
            }

            // ---- 拉 ----
            var pulled = 0;
            var next = LastSeq;
            for (var page = 0; page < maxPages; page++)
            {
                var batch = await _client.PullAsync(next, 200, ct);
                var hasMore = false;
                var applied = Apply(batch, out hasMore);
                pulled += applied;
                next = batch["next"]?.GetValue<long>() ?? next;
                WriteState(KeyLastSeq, next);
                WriteState(KeyLastPullAt, Clock.NowMs());
                if (!hasMore) break;
            }

            // 拉取成功但推送有失败时，也要让界面看到原因，而不是假装一切正常
            return new SyncReport(pushError is null, pushError, pushed, outbox.Count(), pulled, next, DateTime.Now);
        }
        catch (SyncException ex) when (ex.IsAuthFailure)
        {
            return new SyncReport(false, "令牌过期，下一轮会静默续期", 0, outbox.Count(), 0, LastSeq, DateTime.Now);
        }
        catch (Exception ex)
        {
            // 断网、超时、服务端 5xx：都不算错误，下一轮再来
            return new SyncReport(false, ex.Message, 0, outbox.Count(), 0, LastSeq, DateTime.Now);
        }
        finally
        {
            _client?.Dispose();
            _client = null;
            _busy = false;
        }
    }

    private async Task<SyncCredentials?> TryRefreshAsync(SyncCredentials credentials, CancellationToken ct)
    {
        try
        {
            var (access, refresh) = await _client!.RefreshAsync(credentials.Refresh, ct);
            var next = credentials with { Access = access, Refresh = refresh };
            _store.Save(next);
            return next;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------- 落库 ----------------

    /// <summary>
    /// 把一页 /sync 写进本地库。顺序有讲究：先父后子，否则外键会挡。
    /// 整页一个事务：拉到一半断电，游标也不会先跑。
    /// </summary>
    private int Apply(JsonObject batch, out bool hasMore)
    {
        hasMore = batch["hasMore"]?.GetValue<bool>() ?? false;
        var applied = 0;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        applied += ApplyPeriods(conn, tx, Ar(batch, "periods"));
        applied += ApplyStudents(conn, tx, Ar(batch, "students"));
        applied += ApplyMembers(conn, tx, Ar(batch, "members"));
        applied += ApplyRules(conn, tx, Ar(batch, "rules"));
        applied += ApplyNotes(conn, tx, Ar(batch, "note_templates"));
        applied += ApplySchemes(conn, tx, Ar(batch, "schemes"));
        applied += ApplyExams(conn, tx, Ar(batch, "exams"));
        applied += ApplyScores(conn, tx, Ar(batch, "exam_scores"));
        applied += ApplyWeights(conn, tx, Ar(batch, "scheme_weights"));
        applied += ApplyEntries(conn, tx, Ar(batch, "entries"));
        applied += ApplyTombstones(conn, tx, batch["deleted"] as JsonObject);

        tx.Commit();
        return applied;
    }

    private static JsonArray Ar(JsonObject batch, string key) => batch[key] as JsonArray ?? new JsonArray();

    private static int ApplyPeriods(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;

            // 本地已有 active 周期时，远端那个 active 进不来（uq_period_active 是全局唯一）。
            // 先把本地的归档掉 —— 远端说谁是当前周期，就以远端为准。
            if (S(o, "status") == "active")
                Database.Exec(conn, tx,
                    "UPDATE periods SET status = 'archived', end_at = COALESCE(end_at, $at) WHERE status = 'active' AND id <> $id",
                    ("$at", L(o, "start_at")), ("$id", S(o, "id")));

            Database.Exec(conn, tx, """
                INSERT INTO periods (id, name, term_key, start_at, end_at, status, created_at)
                VALUES ($id, $name, $term, $start, $end, $status, $created)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name, term_key = excluded.term_key, start_at = excluded.start_at,
                  end_at = excluded.end_at, status = excluded.status
                """,
                ("$id", S(o, "id")), ("$name", S(o, "name")), ("$term", S(o, "term_key")),
                ("$start", L(o, "start_at")), ("$end", L(o, "end_at")), ("$status", S(o, "status")),
                ("$created", L(o, "created_at")));
            n++;
        }
        return n;
    }

    private static int ApplyStudents(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT INTO students (id, name, student_no, seat_no, group_name, active, rev, updated_at, deleted_at, created_at)
                VALUES ($id, $name, $no, $seat, $grp, $active, $rev, $updated, $deleted, $created)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name, student_no = excluded.student_no, seat_no = excluded.seat_no,
                  group_name = excluded.group_name, active = excluded.active, rev = excluded.rev,
                  updated_at = excluded.updated_at, deleted_at = excluded.deleted_at
                """,
                ("$id", S(o, "id")), ("$name", S(o, "name")), ("$no", S(o, "student_no")),
                ("$seat", L(o, "seat_no")), ("$grp", S(o, "group_name")), ("$active", B(o, "active")),
                ("$rev", L(o, "rev")), ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")),
                ("$created", L(o, "created_at")));
            n++;
        }
        return n;
    }

    private static int ApplyMembers(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT INTO members (id, display_name, role, pin_hash, created_at, revoked_at, rev, updated_at, deleted_at)
                VALUES ($id, $name, $role, $pin, $created, $revoked, $rev, $updated, $deleted)
                ON CONFLICT(id) DO UPDATE SET
                  display_name = excluded.display_name, role = excluded.role, pin_hash = excluded.pin_hash,
                  revoked_at = excluded.revoked_at, rev = excluded.rev, updated_at = excluded.updated_at,
                  deleted_at = excluded.deleted_at
                """,
                ("$id", S(o, "id")), ("$name", S(o, "display_name")),
                ("$role", MemberRoleNames.Parse(S(o, "role")).ToDb()), ("$pin", S(o, "pin_hash")),
                ("$created", L(o, "created_at")), ("$revoked", L(o, "revoked_at")), ("$rev", L(o, "rev")),
                ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")));
            n++;
        }
        return n;
    }

    private static int ApplyRules(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT INTO rules (id, label, delta, icon, sort, preset_key, cap_value, cap_scope, archived_at, rev, updated_at, deleted_at)
                VALUES ($id, $label, $delta, $icon, $sort, $preset, $cap, $scope, $archived, $rev, $updated, $deleted)
                ON CONFLICT(id) DO UPDATE SET
                  label = excluded.label, delta = excluded.delta, icon = excluded.icon, sort = excluded.sort,
                  preset_key = excluded.preset_key, cap_value = excluded.cap_value, cap_scope = excluded.cap_scope,
                  archived_at = excluded.archived_at, rev = excluded.rev, updated_at = excluded.updated_at,
                  deleted_at = excluded.deleted_at
                """,
                ("$id", S(o, "id")), ("$label", S(o, "label")), ("$delta", L(o, "delta") ?? 0),
                ("$icon", S(o, "icon")), ("$sort", L(o, "sort") ?? 0), ("$preset", S(o, "preset_key")),
                ("$cap", L(o, "cap_value")), ("$scope", S(o, "cap_scope")), ("$archived", L(o, "archived_at")),
                ("$rev", L(o, "rev") ?? 1), ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")));
            n++;
        }
        return n;
    }

    private static int ApplyNotes(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT INTO note_templates (id, text, use_count, last_used_at, archived_at, rev, updated_at, deleted_at)
                VALUES ($id, $text, $count, $last, $archived, $rev, $updated, $deleted)
                ON CONFLICT(id) DO UPDATE SET
                  text = excluded.text, use_count = excluded.use_count, last_used_at = excluded.last_used_at,
                  archived_at = excluded.archived_at, rev = excluded.rev, updated_at = excluded.updated_at,
                  deleted_at = excluded.deleted_at
                """,
                ("$id", S(o, "id")), ("$text", S(o, "text")), ("$count", L(o, "use_count") ?? 1),
                ("$last", L(o, "last_used_at")), ("$archived", L(o, "archived_at")), ("$rev", L(o, "rev") ?? 1),
                ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")));
            n++;
        }
        return n;
    }

    private static int ApplySchemes(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            if (B(o, "is_active") == 1)
                Database.Exec(conn, tx, "UPDATE schemes SET is_active = 0");

            Database.Exec(conn, tx, """
                INSERT INTO schemes (id, name, exam_weight, daily_weight, normalize, is_active, created_at)
                VALUES ($id, $name, $exam, $daily, $norm, $active, $created)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name, exam_weight = excluded.exam_weight, daily_weight = excluded.daily_weight,
                  normalize = excluded.normalize, is_active = excluded.is_active
                """,
                ("$id", S(o, "id")), ("$name", S(o, "name")), ("$exam", D(o, "exam_weight")),
                ("$daily", D(o, "daily_weight")), ("$norm", B(o, "normalize")), ("$active", B(o, "is_active")),
                ("$created", L(o, "created_at")));
            n++;
        }
        return n;
    }

    private static int ApplyExams(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT INTO exams (id, period_id, name, full_score, exam_date, imported_at, source_file)
                VALUES ($id, $period, $name, $full, $date, $imported, $src)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name, full_score = excluded.full_score, exam_date = excluded.exam_date,
                  imported_at = excluded.imported_at, source_file = excluded.source_file
                """,
                ("$id", S(o, "id")), ("$period", S(o, "period_id")), ("$name", S(o, "name")),
                ("$full", D(o, "full_score") ?? 750), ("$date", S(o, "exam_date")),
                ("$imported", L(o, "imported_at")), ("$src", S(o, "source_file")));
            n++;
        }
        return n;
    }

    private static int ApplyScores(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT OR REPLACE INTO exam_scores (exam_id, student_id, score, absent)
                VALUES ($exam, $student, $score, $absent)
                """,
                ("$exam", S(o, "exam_id")), ("$student", S(o, "student_id")),
                ("$score", D(o, "score")), ("$absent", B(o, "absent")));
            n++;
        }
        return n;
    }

    private static int ApplyWeights(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT OR REPLACE INTO scheme_weights (scheme_id, exam_id, weight)
                VALUES ($scheme, $exam, $weight)
                """,
                ("$scheme", S(o, "scheme_id")), ("$exam", S(o, "exam_id")), ("$weight", D(o, "weight") ?? 0));
            n++;
        }
        return n;
    }

    /// <summary>
    /// 流水只追加：先 INSERT OR IGNORE（重拉不会重复计分），再单独更新撤销标记。
    /// 注意不写 seq —— 本地 seq 是本地自增的，服务端那个全局 seq 直接抄过来一定撞主键。
    /// </summary>
    private static int ApplyEntries(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            var id = S(o, "id");
            if (string.IsNullOrEmpty(id)) continue;

            Database.Exec(conn, tx, """
                INSERT OR IGNORE INTO entries
                  (id, batch_id, period_id, student_id, rule_id, rule_label, rule_delta_default, delta, source,
                   note, note_template_id, capped, over_cap, operator_id, operator_name, identify_method,
                   occurred_at, created_at, reverses_entry_id, reversed_at, reversed_by, synced)
                VALUES ($id, $batch, $period, $student, $rule, $label, $def, $delta, $source,
                   $note, $noteId, $capped, $over, $opId, $opName, $method,
                   $occurred, $created, $reverses, $reversed, $reversedBy, 1)
                """,
                ("$id", id), ("$batch", S(o, "batch_id")), ("$period", S(o, "period_id")),
                ("$student", S(o, "student_id")), ("$rule", S(o, "rule_id")), ("$label", S(o, "rule_label")),
                ("$def", L(o, "rule_delta_default")), ("$delta", L(o, "delta") ?? 0),
                ("$source", S(o, "source") ?? "rule"), ("$note", S(o, "note")),
                ("$noteId", S(o, "note_template_id")), ("$capped", B(o, "capped")), ("$over", B(o, "over_cap")),
                ("$opId", S(o, "operator_id") ?? "unknown"), ("$opName", S(o, "operator_name") ?? "未知"),
                ("$method", S(o, "identify_method")), ("$occurred", L(o, "occurred_at")),
                ("$created", L(o, "created_at")), ("$reverses", S(o, "reverses_entry_id")),
                ("$reversed", L(o, "reversed_at")), ("$reversedBy", S(o, "reversed_by")));

            // 撤销发生在远端时，本地这条要跟着变成"已撤销"
            var reversedAt = L(o, "reversed_at");
            if (reversedAt is not null)
                Database.Exec(conn, tx,
                    "UPDATE entries SET reversed_at = $at, reversed_by = $by, synced = 1 WHERE id = $id",
                    ("$id", id), ("$at", reversedAt), ("$by", S(o, "reversed_by")));
            n++;
        }
        return n;
    }

    /// <summary>服务端说没有的行，本地也得删 —— 否则会一直留着一份幽灵数据。</summary>
    private static int ApplyTombstones(SqliteConnection conn, SqliteTransaction tx, JsonObject? deleted)
    {
        if (deleted is null) return 0;
        var n = 0;

        (string Table, string Key)[] map =
        [
            ("students", "students"),
            ("rules", "rules"),
            ("members", "members"),
            ("note_templates", "note_templates"),
            ("exams", "exams"),
            ("schemes", "schemes"),
            ("periods", "periods"),
        ];

        foreach (var (jsonKey, table) in map)
        {
            if (deleted[jsonKey] is not JsonArray ids) continue;
            foreach (var id in ids)
            {
                var key = id?.GetValue<string>();
                if (string.IsNullOrEmpty(key)) continue;
                Database.Exec(conn, tx, $"DELETE FROM {table} WHERE id = $id", ("$id", key));
                n++;
            }
        }

        // 成绩的 row_id 是 exam:student
        if (deleted["exam_scores"] is JsonArray scores)
        {
            foreach (var id in scores)
            {
                var raw = id?.GetValue<string>();
                if (string.IsNullOrEmpty(raw)) continue;
                var parts = raw.Split(':', 2);
                if (parts.Length != 2) continue;
                Database.Exec(conn, tx, "DELETE FROM exam_scores WHERE exam_id = $e AND student_id = $s",
                    ("$e", parts[0]), ("$s", parts[1]));
                n++;
            }
        }

        // 流水：只删"已经同步过"的行。本地刚记、还没推上去的那条不能因为服务端没有就抹掉。
        if (deleted["entries"] is JsonArray entries)
        {
            foreach (var id in entries)
            {
                var key = id?.GetValue<string>();
                if (string.IsNullOrEmpty(key)) continue;
                Database.Exec(conn, tx, "DELETE FROM entries WHERE id = $id AND synced = 1", ("$id", key));
                n++;
            }
        }

        return n;
    }

    // ---------------- 状态读写 ----------------

    private long? ReadState(string key)
    {
        using var conn = db.Open();
        var raw = Database.Scalar(conn, Sql.SyncStateGet, ("$key", key));
        return raw is string s && long.TryParse(s, out var v) ? v : null;
    }

    private void WriteState(string key, long value)
    {
        using var conn = db.Open();
        Database.Exec(conn, Sql.SyncStateSet, ("$key", key), ("$value", value.ToString()));
    }

    // ---------------- JSON 取值 ----------------

    private static string? S(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? L(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        return null;
    }

    private static double? D(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, out var p)) return p;
        return null;
    }

    private static int B(JsonObject o, string key) => (int)(L(o, key) ?? 0);
}
