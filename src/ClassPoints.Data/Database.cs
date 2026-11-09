using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

/// <summary>
/// 本地库。路径自己拼 %LOCALAPPDATA% —— unpackaged 应用不能用 ApplicationData.Current（会抛 COMException）。
/// 程序可能装在 Program Files，普通用户没有写权限，所以库绝不放程序目录。
/// </summary>
public sealed class Database
{
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassPoints");

    public static string DefaultDbPath => Path.Combine(RootDir, "classpoints.db");
    public static string BackupRoot => Path.Combine(RootDir, "backups");
    public static string SessionPath => Path.Combine(RootDir, "session.bin");
    public static string RecoveryPath => Path.Combine(RootDir, "recovery.bin");
    public static string LogDir => Path.Combine(RootDir, "logs");

    private readonly string _connectionString;

    public Database(string? dbPath = null)
    {
        DbPath = dbPath ?? DefaultDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Directory.CreateDirectory(BackupRoot);
        Directory.CreateDirectory(LogDir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Pooling = true,
            DefaultTimeout = 5,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string DbPath { get; }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.Pragmas;
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>建表与写种子分两步：建表看 user_version，种子看 settings.seeded。
    /// 混在一起会出事——用户把预设全删了，重启一次它们会全部复活。</summary>
    public void Initialize()
    {
        using var conn = Open();

        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version"));
        Exec(conn, Sql.CreateTables);

        // 先补种子再迁移：迁移里要按"有没有种过"判断，新库和旧库走同一条路
        if (GetSetting(conn, "seeded") is null)
        {
            using var tx = conn.BeginTransaction();
            Seed(conn, tx);
            tx.Commit();
        }

        Migrate(conn, version);

        if (version < Sql.SchemaVersion)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {Sql.SchemaVersion}";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 逐版迁移。每段都要能重复执行（幂等）：这一步出错就打不开库，
    /// 而老师那边看到的只是"点了没反应"，所以宁可多做一次无副作用的检查。
    /// </summary>
    private static void Migrate(SqliteConnection conn, int version)
    {
        // v2：图标从 emoji 换成 Segoe MDL2 字形；新增事件计数。
        // 图标归一化每次都做：行数只有个位数，还能顺手修好从别处同步过来的旧数据。
        NormalizeIcons(conn);

        if (GetSetting(conn, "counters_seeded") is null)
            SeedCounters(conn, null);

        if (version < 2) SetSetting(conn, null, "schema_note", "v2: 图标字体化 + 事件计数");
    }

    private static void NormalizeIcons(SqliteConnection conn)
    {
        var rows = new List<(string Id, string Icon)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, COALESCE(icon, '') FROM rules";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        }

        foreach (var (id, icon) in rows)
        {
            var glyph = IconCatalog.Normalize(icon);
            if (glyph != icon)
                Exec(conn, "UPDATE rules SET icon = $icon WHERE id = $id", ("$id", id), ("$icon", glyph));
        }
    }

    /// <summary>事件预设：只开两个最常用的。老师要别的自己加，删了也不会复活（有 counters_seeded 标记）。</summary>
    public static readonly (string Id, string Label, string Icon, string Preset, int Sort)[] CounterPresets =
    {
        ("c_late", "迟到", IconCatalog.Flag, "std.late", 10),
        ("c_homework", "作业未交", IconCatalog.Document, "std.homework", 20),
        ("c_discipline", "课堂违纪", IconCatalog.Warning, "std.discipline", 30),
    };

    public static void SeedCounters(SqliteConnection conn, SqliteTransaction? tx)
    {
        var now = Clock.NowMs();
        foreach (var c in CounterPresets)
        {
            Exec(conn, tx, """
                INSERT OR IGNORE INTO counters (id, label, icon, sort, preset_key, rev, updated_at)
                VALUES ($id, $label, $icon, $sort, $preset, 1, $now)
                """,
                ("$id", c.Id), ("$label", c.Label), ("$icon", c.Icon),
                ("$sort", c.Sort), ("$preset", c.Preset), ("$now", now));
        }
        SetSetting(conn, tx, "counters_seeded", "1");
    }

    private static void Seed(SqliteConnection conn, SqliteTransaction tx)
    {
        var now = Clock.NowMs();

        // 只有三条预设，不加别的
        (string Id, string Label, int Delta, string Icon, int Sort, string Preset, int? Cap, string? Scope)[] rules =
        {
            ("r_speak", "回答问题", 10, IconCatalog.Message, 10, "std.speak", null, null),
            ("r_recite", "早读检查背课文", 50, IconCatalog.Reading, 20, "std.recite", 50, "single"),
            ("r_board", "课上黑板演题", 100, IconCatalog.Edit, 30, "std.board", null, null),
        };

        foreach (var r in rules)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO rules (id, label, delta, icon, sort, preset_key, cap_value, cap_scope, rev, updated_at)
                VALUES ($id, $label, $delta, $icon, $sort, $preset, $cap, $scope, 1, $now)
                """;
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.Parameters.AddWithValue("$label", r.Label);
            cmd.Parameters.AddWithValue("$delta", r.Delta);
            cmd.Parameters.AddWithValue("$icon", r.Icon);
            cmd.Parameters.AddWithValue("$sort", r.Sort);
            cmd.Parameters.AddWithValue("$preset", r.Preset);
            cmd.Parameters.AddWithValue("$cap", (object?)r.Cap ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$scope", (object?)r.Scope ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        // 常用理由标签：内置约 12 个通用标签，可增可删
        string[] notes =
        {
            "背诵流利", "略有卡顿", "一题多解", "纠错到位", "主动补做", "思路清晰",
            "书写工整", "帮助同学", "课堂违纪", "作业未交", "迟到", "值日认真",
        };
        var i = 0;
        foreach (var text in notes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO note_templates (id, text, use_count, last_used_at, rev, updated_at)
                VALUES ($id, $text, 1, $now, 1, $now)
                """;
            cmd.Parameters.AddWithValue("$id", $"nt_{i:D2}");
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
            i++;
        }

        // 当前学期：term_key 只有「重置学期」才换
        var termKey = TermKeyFor(DateTime.Now);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO periods (id, name, term_key, start_at, status, created_at)
                VALUES ($id, $name, $term, $start, 'active', $now)
                """;
            cmd.Parameters.AddWithValue("$id", "p_" + termKey);
            cmd.Parameters.AddWithValue("$name", TermNameFor(DateTime.Now));
            cmd.Parameters.AddWithValue("$term", termKey);
            cmd.Parameters.AddWithValue("$start", Clock.FromLocal(DateTime.Now.Date));
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO schemes (id, name, exam_weight, daily_weight, normalize, is_active, created_at)
                VALUES ('sch_default', '默认方案', 0.7, 0.3, 0, 1, $now)
                """;
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        (string Key, string Value)[] settings =
        {
            ("seeded", "1"),
            ("onboarding_done", "0"),
            ("app_mode", ""),
            ("class_name", ""),
            ("class_code", ""),
            ("seat_cols", "8"),
            ("seat_rows", "6"),
            ("idle_timeout_sec", "30"),
            ("lock_timeout_sec", "300"),
            ("lookup_timeout_sec", "30"),
            ("sound_on", "1"),
            ("board_rotate_sec", "20"),
            ("absence_policy", "redistribute"),
            ("backup_dir", BackupRoot),
            ("last_backup_at", "0"),
            ("window_w", "1920"),
            ("window_h", "1048"),
            ("window_x", ""),
            ("window_y", ""),
        };
        foreach (var (k, v) in settings)
            SetSetting(conn, tx, k, v);
    }

    public static string TermKeyFor(DateTime date)
    {
        // 9 月–次年 1 月为秋季，2 月–8 月为春季；同一学期内多次重置积分，term_key 不变
        var y = date.Month >= 9 ? date.Year : date.Year - 1;
        var season = date.Month is 9 or 10 or 11 or 12 or 1 ? "AUT" : "SPR";
        return $"{y}-{season}";
    }

    public static string TermNameFor(DateTime date)
    {
        var y = date.Month >= 9 ? date.Year : date.Year - 1;
        return date.Month is 9 or 10 or 11 or 12 or 1 ? $"{y} 秋季学期" : $"{y} 春季学期";
    }

    // ---------- 小工具 ----------

    public static object Scalar(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar() ?? DBNull.Value;
    }

    /// <summary>事务内查询必须显式带上事务，Microsoft.Data.Sqlite 会校验。</summary>
    public static object ScalarTx(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar() ?? DBNull.Value;
    }

    public static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public string? GetSetting(string key)
    {
        using var conn = Open();
        return GetSetting(conn, key);
    }

    public static string? GetSetting(SqliteConnection conn, string key)
    {
        var v = Scalar(conn, Sql.SettingGet, ("$key", key));
        return v is DBNull or null ? null : v.ToString();
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        SetSetting(conn, null, key, value);
    }

    public static void SetSetting(SqliteConnection conn, SqliteTransaction? tx, string key, string value) =>
        Exec(conn, tx, Sql.SettingSet, ("$key", key), ("$value", value));

    public int GetIntSetting(string key, int fallback) =>
        int.TryParse(GetSetting(key), out var v) ? v : fallback;

    public bool GetBoolSetting(string key, bool fallback) =>
        GetSetting(key) is string s ? s == "1" : fallback;

    public string GetSyncState(string key, string fallback = "")
    {
        using var conn = Open();
        var v = Scalar(conn, Sql.SyncStateGet, ("$key", key));
        return v is DBNull or null ? fallback : v.ToString()!;
    }

    public void SetSyncState(string key, string value)
    {
        using var conn = Open();
        Exec(conn, Sql.SyncStateSet, ("$key", key), ("$value", value));
    }
}
