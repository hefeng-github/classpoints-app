using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

/// <summary>
/// 事件计数。跟积分流水是两套东西，故意不共用：
///
/// - 积分要参与总评、要能冲销、要进 Excel，任何一条 0 分的记录都会在这些地方
///   多出一个 if；事件只关心"几次"。
/// - 次数永远现算 SUM(value)，允许 value 直接写成 +3/-2，"改成 5 次"就是补一条差值，
///   不需要在流水上改历史（改历史在同步下必然打架）。
/// - 只存本机。这是刻意取舍：云端同步要连带改 Worker、D1 建表、拉取协议，
///   而事件计数几乎都是在大屏上现场点的，跨机合并的收益远小于引入的复杂度。
///   要跨机看，用「整包导出 / 导入」把数据整份带过去。
/// </summary>
public sealed class CounterRepository(Database db)
{
    // ---------------- 事件定义 ----------------

    public List<Counter> ListAll(bool includeArchived = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeArchived
            ? "SELECT * FROM counters WHERE deleted_at IS NULL ORDER BY sort, label"
            : "SELECT * FROM counters WHERE deleted_at IS NULL AND archived_at IS NULL ORDER BY sort, label";
        using var r = cmd.ExecuteReader();
        var list = new List<Counter>();
        while (r.Read()) list.Add(Mappers.Counter(r));
        return list;
    }

    public Counter? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM counters WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Counter(r) : null;
    }

    public Counter Save(Counter counter)
    {
        var now = Clock.NowMs();
        var isNew = string.IsNullOrEmpty(counter.Id);
        if (isNew) counter.Id = Ids.NewUuid7();
        if (counter.Sort == 0) counter.Sort = (ListAll(true).LastOrDefault()?.Sort ?? 0) + 10;
        counter.Icon = IconCatalog.Normalize(counter.Icon);
        counter.UpdatedAt = now;

        using var conn = db.Open();
        if (isNew)
        {
            counter.Rev = 1;
            Database.Exec(conn, """
                INSERT INTO counters (id, label, icon, sort, preset_key, rev, updated_at)
                VALUES ($id, $label, $icon, $sort, $preset, 1, $now)
                """,
                ("$id", counter.Id), ("$label", counter.Label), ("$icon", counter.Icon),
                ("$sort", counter.Sort), ("$preset", counter.PresetKey), ("$now", now));
        }
        else
        {
            counter.Rev++;
            Database.Exec(conn, """
                UPDATE counters SET label = $label, icon = $icon, sort = $sort, rev = $rev, updated_at = $now
                WHERE id = $id
                """,
                ("$id", counter.Id), ("$label", counter.Label), ("$icon", counter.Icon),
                ("$sort", counter.Sort), ("$rev", counter.Rev), ("$now", now));
        }
        return counter;
    }

    public void Archive(string id, bool archived = true)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        // archived_at 可以写 NULL（恢复归档），updated_at 不行 —— 两列必须分开传参
        Database.Exec(conn, "UPDATE counters SET archived_at = $archived, updated_at = $now, rev = rev + 1 WHERE id = $id",
            ("$id", id), ("$archived", archived ? now : (object)DBNull.Value), ("$now", now));
    }

    /// <summary>停用而不删除：删了以后历史次数还在表里，界面上却查不到名字，那是最难解释的一种数据。</summary>
    public void Delete(string id)
    {
        var now = Clock.NowMs();
        using var conn = db.Open();
        Database.Exec(conn, "UPDATE counters SET archived_at = $at, updated_at = $at, rev = rev + 1 WHERE id = $id",
            ("$id", id), ("$at", now));
    }

    // ---------------- 次数 ----------------

    /// <summary>整班整周期：key = (counterId, studentId)。一次查全，界面不做 N×M 次查询。</summary>
    public Dictionary<(string CounterId, string StudentId), int> Counts(string periodId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.CounterCounts;
        cmd.Parameters.AddWithValue("$period", periodId);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<(string, string), int>();
        while (r.Read()) map[(r.GetStr("cid"), r.GetStr("sid"))] = r.GetInt("n");
        return map;
    }

    public Dictionary<string, int> CountsForStudent(string periodId, string studentId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.CounterCountsForStudent;
        cmd.Parameters.AddWithValue("$period", periodId);
        cmd.Parameters.AddWithValue("$student", studentId);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>();
        while (r.Read()) map[r.GetStr("cid")] = r.GetInt("n");
        return map;
    }

    public int Total(string periodId, string studentId, string counterId)
    {
        using var conn = db.Open();
        return Convert.ToInt32(Database.Scalar(conn, Sql.CounterTotalForStudent,
            ("$period", periodId), ("$student", studentId), ("$counter", counterId)));
    }

    /// <summary>某类事件全班这个周期一共几次。</summary>
    public int ClassTotal(string periodId, string counterId)
    {
        using var conn = db.Open();
        return Convert.ToInt32(Database.Scalar(conn, Sql.CounterClassTotal,
            ("$period", periodId), ("$counter", counterId)));
    }

    /// <summary>次数合计（跨事件类型），用于"这个学生一共被记了几次"。</summary>
    public Dictionary<string, int> ClassTotalsByStudent(string periodId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT student_id AS sid, SUM(value) AS n FROM counter_ticks WHERE period_id = $p GROUP BY sid";
        cmd.Parameters.AddWithValue("$p", periodId);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>();
        while (r.Read()) map[r.GetStr("sid")] = r.GetInt("n");
        return map;
    }

    /// <summary>
    /// 记一次。value 只让界面传 ±1；返回记完之后的次数。
    /// 减到 0 就不再往下减：负数次数没有语义，只会让人怀疑数据坏了。
    /// </summary>
    public int AddTick(string counterId, string studentId, string periodId, int value, Member op, string? note = null)
    {
        if (value == 0) return Total(periodId, studentId, counterId);

        var now = Clock.NowMs();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var current = Convert.ToInt32(Database.ScalarTx(conn, tx, Sql.CounterTotalForStudent,
            ("$period", periodId), ("$student", studentId), ("$counter", counterId)));

        var applied = value;
        if (current + applied < 0) applied = -current;
        if (applied == 0)
        {
            tx.Rollback();
            return current;
        }

        InsertTick(conn, tx, counterId, studentId, periodId, applied, op, note, now);
        tx.Commit();
        return current + applied;
    }

    /// <summary>直接把次数改成某个值（家访记录、纸质名单补录时用）。差多少就补一条多少的流水。</summary>
    public int SetCount(string counterId, string studentId, string periodId, int target, Member op, string? note = null)
    {
        target = System.Math.Max(0, target);
        var now = Clock.NowMs();

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var current = Convert.ToInt32(Database.ScalarTx(conn, tx, Sql.CounterTotalForStudent,
            ("$period", periodId), ("$student", studentId), ("$counter", counterId)));

        var diff = target - current;
        if (diff == 0)
        {
            tx.Rollback();
            return current;
        }

        InsertTick(conn, tx, counterId, studentId, periodId, diff, op, note ?? "手工改为 " + target + " 次", now);
        tx.Commit();
        return target;
    }

    /// <summary>撤销最近一次：找出这个学生这类事件最后一条流水，补一条等量的反向记录。</summary>
    public int UndoLast(string counterId, string studentId, string periodId, Member op)
    {
        using var conn = db.Open();
        string? lastId = null;
        var lastValue = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, value FROM counter_ticks
                WHERE period_id = $p AND student_id = $s AND counter_id = $c
                ORDER BY seq DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$p", periodId);
            cmd.Parameters.AddWithValue("$s", studentId);
            cmd.Parameters.AddWithValue("$c", counterId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                lastId = r.GetString(0);
                lastValue = r.GetInt32(1);
            }
        }

        if (lastId is null || lastValue == 0) return Total(periodId, studentId, counterId);

        var now = Clock.NowMs();
        using var tx = conn.BeginTransaction();
        // 补反向记录，而不是删掉那一条：删行会让"什么时候记的、谁记的"一起消失
        InsertTick(conn, tx, counterId, studentId, periodId, -lastValue, op, "撤销上一步", now);
        tx.Commit();
        return Total(periodId, studentId, counterId);
    }

    private static void InsertTick(SqliteConnection conn, SqliteTransaction tx,
        string counterId, string studentId, string periodId, int value, Member op, string? note, long now)
    {
        Database.Exec(conn, tx, """
            INSERT INTO counter_ticks
              (id, counter_id, student_id, period_id, value, note, operator_id, operator_name, occurred_at, created_at)
            VALUES ($id, $c, $s, $p, $v, $note, $op, $opName, $now, $now)
            """,
            ("$id", Ids.NewUuid7()), ("$c", counterId), ("$s", studentId), ("$p", periodId),
            ("$v", value), ("$note", note), ("$op", op.Id), ("$opName", op.DisplayName), ("$now", now));
    }

    // ---------------- 明细 ----------------

    public List<CounterTick> Recent(string periodId, int limit = 200)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.CounterTicksRecent;
        cmd.Parameters.AddWithValue("$period", periodId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<CounterTick>();
        while (r.Read()) list.Add(Mappers.CounterTick(r));
        return list;
    }

    public List<CounterTick> TicksForStudent(string studentId, string periodId, int limit = 50)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.CounterTicksForStudent;
        cmd.Parameters.AddWithValue("$student", studentId);
        cmd.Parameters.AddWithValue("$period", periodId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<CounterTick>();
        while (r.Read()) list.Add(Mappers.CounterTick(r));
        return list;
    }

    /// <summary>把事件计数写进 Excel/打印的附注，先只给一行摘要。</summary>
    public string SummaryForStudent(string periodId, string studentId)
    {
        var counters = ListAll(true);
        var counts = CountsForStudent(periodId, studentId);
        var parts = counters
            .Where(c => counts.GetValueOrDefault(c.Id) > 0)
            .Select(c => $"{c.Label} {counts[c.Id]} 次")
            .ToList();
        return parts.Count == 0 ? "" : string.Join(" · ", parts);
    }
}
