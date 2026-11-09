using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

public sealed class StudentRepository(Database db)
{
    public List<Student> ListAll(bool includeDeleted = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeDeleted
            ? "SELECT * FROM students ORDER BY seat_no IS NULL, seat_no, name"
            : "SELECT * FROM students WHERE deleted_at IS NULL ORDER BY seat_no IS NULL, seat_no, name";
        using var r = cmd.ExecuteReader();
        var list = new List<Student>();
        while (r.Read()) list.Add(Mappers.Student(r));
        return list;
    }

    public List<Student> ListActive() => ListAll().Where(s => s.Active).ToList();

    public Student? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM students WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Student(r) : null;
    }

    public Student Insert(Student s)
    {
        var now = Clock.NowMs();
        if (string.IsNullOrEmpty(s.Id)) s.Id = Ids.NewUuid7();
        s.CreatedAt = s.CreatedAt == 0 ? now : s.CreatedAt;
        s.UpdatedAt = now;
        s.Rev = 1;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT INTO students (id, name, student_no, seat_no, group_name, active, rev, updated_at, created_at)
            VALUES ($id, $name, $no, $seat, $grp, $active, 1, $updated, $created)
            """,
            ("$id", s.Id), ("$name", s.Name), ("$no", s.StudentNo), ("$seat", s.SeatNo),
            ("$grp", s.GroupName), ("$active", s.Active ? 1 : 0), ("$updated", now), ("$created", s.CreatedAt));
        SyncOutbox.Enqueue(conn, tx, $"students:{s.Id}", "student", new { action = "upsert", student = s });
        tx.Commit();
        return s;
    }

    public void Update(Student s)
    {
        var now = Clock.NowMs();
        s.UpdatedAt = now;
        s.Rev++;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            UPDATE students SET name = $name, student_no = $no, seat_no = $seat, group_name = $grp,
                   active = $active, rev = $rev, updated_at = $updated
            WHERE id = $id
            """,
            ("$id", s.Id), ("$name", s.Name), ("$no", s.StudentNo), ("$seat", s.SeatNo),
            ("$grp", s.GroupName), ("$active", s.Active ? 1 : 0), ("$rev", s.Rev), ("$updated", now));
        SyncOutbox.Enqueue(conn, tx, $"students:{s.Id}", "student", new { action = "upsert", student = s });
        tx.Commit();
    }

    /// <summary>软删除：同步下不能硬删，否则另一台设备会把旧数据推回来让转学生复活。</summary>
    public void SoftDelete(string id)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            UPDATE students SET deleted_at = $at, active = 0, rev = rev + 1, updated_at = $at WHERE id = $id
            """, ("$id", id), ("$at", now));
        SyncOutbox.Enqueue(conn, tx, $"students:{id}", "student", new { action = "delete", id, deletedAt = now });
        tx.Commit();
    }

    public void Restore(string id)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            UPDATE students SET deleted_at = NULL, active = 1, rev = rev + 1, updated_at = $at WHERE id = $id
            """, ("$id", id), ("$at", now));
        SyncOutbox.Enqueue(conn, tx, $"students:{id}", "student", new { action = "restore", id });
        tx.Commit();
    }

    /// <summary>粘贴一列姓名。startSeat 为起始座位号，空行忽略，重名不做拦截（名单里真有同名）。</summary>
    public int ImportNames(IEnumerable<string> lines, int? startSeat = null)
    {
        var names = lines
            .Select(NameNormalizer.StripListIndex)
            .Where(n => n.Length > 0)
            .ToList();
        if (names.Count == 0) return 0;

        var existing = ListAll().Count;
        var seat = startSeat ?? (existing + 1);
        var added = 0;
        foreach (var name in names)
        {
            Insert(new Student { Name = name, SeatNo = seat++ });
            added++;
        }
        return added;
    }

    /// <summary>按顺序重排座位号（格子排布由 seat_cols 决定，座位号从 1 开始连续）。</summary>
    public void ReassignSeats(IReadOnlyList<string> orderedIds)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            Database.Exec(conn, tx, "UPDATE students SET seat_no = $seat, updated_at = $at WHERE id = $id",
                ("$id", orderedIds[i]), ("$seat", i + 1), ("$at", now));
        }
        tx.Commit();
    }
}

public sealed class RuleRepository(Database db)
{
    public List<Rule> ListAll(bool includeArchived = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeArchived
            ? "SELECT * FROM rules WHERE deleted_at IS NULL ORDER BY sort, label"
            : "SELECT * FROM rules WHERE deleted_at IS NULL AND archived_at IS NULL ORDER BY sort, label";
        using var r = cmd.ExecuteReader();
        var list = new List<Rule>();
        while (r.Read()) list.Add(Mappers.Rule(r));
        return list;
    }

    public Rule? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM rules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Rule(r) : null;
    }

    public Rule Save(Rule rule)
    {
        var now = Clock.NowMs();
        var isNew = string.IsNullOrEmpty(rule.Id);
        if (isNew) rule.Id = Ids.NewUuid7();
        if (rule.Sort == 0) rule.Sort = (ListAll(true).LastOrDefault()?.Sort ?? 0) + 10;
        rule.UpdatedAt = now;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (isNew)
        {
            rule.Rev = 1;
            Database.Exec(conn, tx, """
                INSERT INTO rules (id, label, delta, icon, sort, preset_key, cap_value, cap_scope, rev, updated_at)
                VALUES ($id, $label, $delta, $icon, $sort, $preset, $cap, $scope, 1, $now)
                """,
                ("$id", rule.Id), ("$label", rule.Label), ("$delta", rule.Delta), ("$icon", rule.Icon),
                ("$sort", rule.Sort), ("$preset", rule.PresetKey), ("$cap", rule.CapValue),
                ("$scope", rule.CapScope == CapScope.None ? null : (rule.CapScope == CapScope.Single ? "single" : "period")),
                ("$now", now));
        }
        else
        {
            rule.Rev++;
            Database.Exec(conn, tx, """
                UPDATE rules SET label = $label, delta = $delta, icon = $icon, sort = $sort,
                       cap_value = $cap, cap_scope = $scope, rev = $rev, updated_at = $now
                WHERE id = $id
                """,
                ("$id", rule.Id), ("$label", rule.Label), ("$delta", rule.Delta), ("$icon", rule.Icon),
                ("$sort", rule.Sort), ("$cap", rule.CapValue),
                ("$scope", rule.CapScope == CapScope.None ? null : (rule.CapScope == CapScope.Single ? "single" : "period")),
                ("$rev", rule.Rev), ("$now", now));
        }
        SyncOutbox.Enqueue(conn, tx, $"rules:{rule.Id}", "rule", new { action = "upsert", rule });
        tx.Commit();
        return rule;
    }

    public void Archive(string id, bool archived = true)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        // archived_at 允许 NULL（这一行是"恢复"），updated_at 是 NOT NULL，
        // 早先两列共用一个参数，一恢复就崩在约束上 —— 分开传。
        Database.Exec(conn, "UPDATE rules SET archived_at = $archived, updated_at = $now, rev = rev + 1 WHERE id = $id",
            ("$id", id), ("$archived", archived ? now : (object)DBNull.Value), ("$now", now));
    }

    public void Delete(string id)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        Database.Exec(conn, "UPDATE rules SET deleted_at = $at, updated_at = $at, rev = rev + 1 WHERE id = $id",
            ("$id", id), ("$at", now));
    }

    public void Restore(string id)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        Database.Exec(conn, "UPDATE rules SET deleted_at = NULL, updated_at = $at, rev = rev + 1 WHERE id = $id",
            ("$id", id), ("$at", now));
    }

    /// <summary>恢复默认预设：只追加缺失的 preset_key，已存在的一律跳过（哪怕名字和分值都被改过）。</summary>
    public (int Added, int Skipped) RestorePresets()
    {
        var present = new HashSet<string>(ListAll(includeArchived: true)
            .Where(r => r.PresetKey is not null)
            .Select(r => r.PresetKey!));

        (string Id, string Label, int Delta, string Icon, int Sort, string Preset, int? Cap, CapScope Scope)[] presets =
        {
            ("r_speak", "回答问题", 10, IconCatalog.Message, 10, "std.speak", null, CapScope.None),
            ("r_recite", "早读检查背课文", 50, IconCatalog.Reading, 20, "std.recite", 50, CapScope.Single),
            ("r_board", "课上黑板演题", 100, IconCatalog.Edit, 30, "std.board", null, CapScope.None),
        };

        var added = 0;
        var skipped = 0;
        foreach (var p in presets)
        {
            if (present.Contains(p.Preset))
            {
                skipped++;
                continue;
            }
            Save(new Rule
            {
                Id = p.Id,
                Label = p.Label,
                Delta = p.Delta,
                Icon = p.Icon,
                Sort = p.Sort,
                PresetKey = p.Preset,
                CapValue = p.Cap,
                CapScope = p.Scope,
            });
            added++;
        }
        return (added, skipped);
    }
}

public sealed class MemberRepository(Database db)
{
    public List<Member> ListAll(bool includeRevoked = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeRevoked
            ? "SELECT * FROM members WHERE deleted_at IS NULL ORDER BY role, display_name"
            : "SELECT * FROM members WHERE deleted_at IS NULL AND revoked_at IS NULL ORDER BY role, display_name";
        using var r = cmd.ExecuteReader();
        var list = new List<Member>();
        while (r.Read()) list.Add(Mappers.Member(r));
        return list;
    }

    public Member? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM members WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Member(r) : null;
    }

    public Member? Owner()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM members WHERE role = 'owner' AND deleted_at IS NULL LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Member(r) : null;
    }

    public Member Save(Member m, string? pin = null)
    {
        var now = Clock.NowMs();
        var isNew = string.IsNullOrEmpty(m.Id);
        if (isNew) m.Id = Ids.NewUuid7();
        m.UpdatedAt = now;
        if (!string.IsNullOrEmpty(pin)) m.PinHash = PasswordHasher.Hash(pin);

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (isNew)
        {
            m.CreatedAt = m.CreatedAt == 0 ? now : m.CreatedAt;
            m.Rev = 1;
            Database.Exec(conn, tx, """
                INSERT INTO members (id, display_name, role, pin_hash, created_at, rev, updated_at)
                VALUES ($id, $name, $role, $pin, $created, 1, $now)
                """,
                ("$id", m.Id), ("$name", m.DisplayName), ("$role", m.Role.ToDb()),
                ("$pin", m.PinHash), ("$created", m.CreatedAt), ("$now", now));
        }
        else
        {
            m.Rev++;
            Database.Exec(conn, tx, """
                UPDATE members SET display_name = $name, role = $role, pin_hash = $pin, rev = $rev, updated_at = $now
                WHERE id = $id
                """,
                ("$id", m.Id), ("$name", m.DisplayName), ("$role", m.Role.ToDb()),
                ("$pin", m.PinHash), ("$rev", m.Rev), ("$now", now));
        }
        SyncOutbox.Enqueue(conn, tx, $"members:{m.Id}", "member", new { action = "upsert", member = m });
        tx.Commit();
        return m;
    }

    public void Revoke(string id)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        Database.Exec(conn, "UPDATE members SET revoked_at = $at, rev = rev + 1, updated_at = $at WHERE id = $id",
            ("$id", id), ("$at", now));
    }

    public Member? VerifyPin(string pin)
    {
        foreach (var m in ListAll())
            if (PasswordHasher.Verify(pin, m.PinHash)) return m;
        return null;
    }

    // ---------- 身份凭证 ----------

    public List<Identity> Identities(string? memberId = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = memberId is null
            ? "SELECT * FROM identities ORDER BY created_at DESC"
            : "SELECT * FROM identities WHERE member_id = $m ORDER BY created_at DESC";
        if (memberId is not null) cmd.Parameters.AddWithValue("$m", memberId);
        using var r = cmd.ExecuteReader();
        var list = new List<Identity>();
        while (r.Read()) list.Add(Mappers.Identity(r));
        return list;
    }

    public string AddCard(string memberId, string rawUid, string? label)
    {
        var id = Ids.NewUuid7();
        var credential = Crypto.HashCredential(rawUid);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT OR REPLACE INTO identities (id, member_id, method, credential, label, created_at)
            VALUES ($id, $m, 'nfc', $cred, $label, $now)
            """,
            ("$id", id), ("$m", memberId), ("$cred", credential), ("$label", label ?? "校园卡"), ("$now", Clock.NowMs()));
        SyncOutbox.Enqueue(conn, tx, $"identities:{id}", "member", new
        {
            action = "identity",
            identity = new Identity { Id = id, MemberId = memberId, Method = "nfc", Credential = credential, Label = label, CreatedAt = Clock.NowMs() },
        });
        tx.Commit();
        return id;
    }

    /// <summary>卡丢了立即吊销：这张卡的哈希立刻失效，补一张新卡改一行数据。</summary>
    public void RevokeCard(string identityId)
    {
        using var conn = db.Open();
        Database.Exec(conn, "DELETE FROM identities WHERE id = $id", ("$id", identityId));
    }

    public Member? FindByCard(string rawUid)
    {
        var credential = Crypto.HashCredential(rawUid);
        Member? member;
        using (var conn = db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT m.* FROM identities i JOIN members m ON m.id = i.member_id
                WHERE i.method = 'nfc' AND i.credential = $cred AND m.revoked_at IS NULL AND m.deleted_at IS NULL
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$cred", credential);
            using var r = cmd.ExecuteReader();
            member = r.Read() ? Mappers.Member(r) : null;
        }

        if (member is null) return null;

        using (var conn = db.Open())
            Database.Exec(conn, "UPDATE identities SET last_used_at = $at WHERE credential = $cred",
                ("$at", Clock.NowMs()), ("$cred", credential));

        return member;
    }

    /// <summary>同一身份 1 分钟内刷 10 次以上 → 异常频率告警。</summary>
    public bool IsFrequentUse(string memberId)
    {
        using var conn = db.Open();
        var from = Clock.NowMs() - 60_000;
        var count = Convert.ToInt32(Database.Scalar(conn,
            "SELECT COUNT(*) FROM entries WHERE operator_id = $m AND created_at >= $from",
            ("$m", memberId), ("$from", from)));
        return count >= 10;
    }
}

public sealed class NoteTemplateRepository(Database db)
{
    /// <summary>按使用频次排序，最多显示 6 个；频次每周重算一次，别实时（按钮会在两次点击之间换位置）。</summary>
    public List<NoteTemplate> Top(int take = 6)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM note_templates
            WHERE deleted_at IS NULL AND archived_at IS NULL
            ORDER BY use_count DESC, last_used_at DESC
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$take", take);
        using var r = cmd.ExecuteReader();
        var list = new List<NoteTemplate>();
        while (r.Read()) list.Add(Mappers.Note(r));
        return list;
    }

    public List<NoteTemplate> All()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM note_templates WHERE deleted_at IS NULL ORDER BY use_count DESC, text";
        using var r = cmd.ExecuteReader();
        var list = new List<NoteTemplate>();
        while (r.Read()) list.Add(Mappers.Note(r));
        return list;
    }

    public NoteTemplate Add(string text)
    {
        var clean = NameNormalizer.Clean(text);
        var now = Clock.NowMs();
        var note = new NoteTemplate { Id = Ids.NewUuid7(), Text = clean, UseCount = 1, LastUsedAt = now, Rev = 1, UpdatedAt = now };
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT INTO note_templates (id, text, use_count, last_used_at, rev, updated_at)
            VALUES ($id, $text, 1, $now, 1, $now)
            """, ("$id", note.Id), ("$text", note.Text), ("$now", now));
        SyncOutbox.Enqueue(conn, tx, $"notes:{note.Id}", "note", new { action = "upsert", note });
        tx.Commit();
        return note;
    }

    public void IncrementUse(IEnumerable<string> ids)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        foreach (var id in ids.Distinct())
            Database.Exec(conn, "UPDATE note_templates SET use_count = use_count + 1, last_used_at = $now, updated_at = $now WHERE id = $id",
                ("$id", id), ("$now", now));
    }

    public void Delete(string id)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        Database.Exec(conn, "UPDATE note_templates SET deleted_at = $now, updated_at = $now WHERE id = $id",
            ("$id", id), ("$now", now));
    }
}

public sealed class PeriodRepository(Database db)
{
    public Period? Active()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM periods WHERE status = 'active' LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Period(r) : null;
    }

    public List<Period> Archived()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM periods WHERE status = 'archived' ORDER BY start_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<Period>();
        while (r.Read()) list.Add(Mappers.Period(r));
        return list;
    }

    public Period? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM periods WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Period(r) : null;
    }

    public int EntryCount(string periodId)
    {
        using var conn = db.Open();
        return Convert.ToInt32(Database.Scalar(conn,
            "SELECT COUNT(*) FROM entries WHERE period_id = $p AND reverses_entry_id IS NULL", ("$p", periodId)));
    }

    /// <summary>重置 = 归档当前周期 + 新建 active。entries 一行不删。</summary>
    public Period Rollover(string name, bool newTerm)
    {
        var now = Clock.NowMs();
        var active = Active() ?? throw new InvalidOperationException("没有 active 周期");
        var termKey = newTerm ? Database.TermKeyFor(DateTime.Now) : active.TermKey;
        var next = new Period
        {
            Id = Ids.NewUuid7(),
            Name = name,
            TermKey = termKey,
            StartAt = now,
            Status = PeriodStatus.Active,
            CreatedAt = now,
        };

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, "UPDATE periods SET status = 'archived', end_at = $now WHERE id = $id",
            ("$id", active.Id), ("$now", now));
        Database.Exec(conn, tx, """
            INSERT INTO periods (id, name, term_key, start_at, status, created_at)
            VALUES ($id, $name, $term, $start, 'active', $now)
            """,
            ("$id", next.Id), ("$name", next.Name), ("$term", next.TermKey),
            ("$start", next.StartAt), ("$now", now));
        SyncOutbox.Enqueue(conn, tx, $"periods:{next.Id}", "period",
            new { action = "rollover", archivedId = active.Id, period = next, endAt = now });
        tx.Commit();
        return next;
    }

    public void Rename(string id, string name)
    {
        using var conn = db.Open();
        Database.Exec(conn, "UPDATE periods SET name = $name WHERE id = $id", ("$id", id), ("$name", name));
    }
}

public sealed class AuditRepository(Database db)
{
    public void Add(string action, string? actorId = null, string? target = null, string? detail = null)
    {
        using var conn = db.Open();
        Database.Exec(conn, "INSERT INTO audit_log (at, actor_id, action, target, detail) VALUES ($at, $a, $act, $t, $d)",
            ("$at", Clock.NowMs()), ("$a", actorId), ("$act", action), ("$t", target), ("$d", detail));
    }

    public List<AuditLogItem> Recent(int take = 100)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM audit_log ORDER BY at DESC LIMIT $take";
        cmd.Parameters.AddWithValue("$take", take);
        using var r = cmd.ExecuteReader();
        var list = new List<AuditLogItem>();
        while (r.Read())
            list.Add(new AuditLogItem
            {
                Id = r.GetLong("id"),
                At = r.GetLong("at"),
                ActorId = r.GetStrOrNull("actor_id"),
                Action = r.GetStr("action"),
                Target = r.GetStrOrNull("target"),
                Detail = r.GetStrOrNull("detail"),
            });
        return list;
    }
}
