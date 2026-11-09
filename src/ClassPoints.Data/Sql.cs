namespace ClassPoints.Data;

/// <summary>
/// 所有 SQL 集中在这个文件里。换云端那天，打开这一个文件就能数清楚要改多少行。
/// </summary>
public static class Sql
{
    public const int SchemaVersion = 2;

    public const string Pragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;
        """;

    public const string CreateTables = """
        CREATE TABLE IF NOT EXISTS students (
          id          TEXT PRIMARY KEY,
          name        TEXT NOT NULL,
          student_no  TEXT,
          seat_no     INTEGER,
          group_name  TEXT,
          active      INTEGER NOT NULL DEFAULT 1,
          rev         INTEGER NOT NULL DEFAULT 1,
          updated_at  INTEGER NOT NULL,
          deleted_at  INTEGER,
          created_at  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_students_updated ON students(updated_at);
        CREATE INDEX IF NOT EXISTS idx_students_seat ON students(active, seat_no);
        CREATE UNIQUE INDEX IF NOT EXISTS uq_students_no ON students(student_no) WHERE student_no IS NOT NULL;

        CREATE TABLE IF NOT EXISTS members (
          id           TEXT PRIMARY KEY,
          display_name TEXT NOT NULL,
          role         TEXT NOT NULL CHECK (role IN ('owner','co_owner','officer')),
          pin_hash     TEXT,
          created_at   INTEGER NOT NULL,
          revoked_at   INTEGER,
          rev          INTEGER NOT NULL DEFAULT 1,
          updated_at   INTEGER NOT NULL,
          deleted_at   INTEGER
        );

        CREATE TABLE IF NOT EXISTS identities (
          id           TEXT PRIMARY KEY,
          member_id    TEXT NOT NULL REFERENCES members(id),
          method       TEXT NOT NULL CHECK (method IN ('nfc','finger','face','pin')),
          credential   TEXT NOT NULL,
          label        TEXT,
          created_at   INTEGER NOT NULL,
          last_used_at INTEGER
        );
        CREATE UNIQUE INDEX IF NOT EXISTS uq_identities_cred ON identities(method, credential);

        CREATE TABLE IF NOT EXISTS rules (
          id          TEXT PRIMARY KEY,
          label       TEXT NOT NULL,
          delta       INTEGER NOT NULL,
          icon        TEXT,
          sort        INTEGER NOT NULL DEFAULT 0,
          preset_key  TEXT UNIQUE,
          cap_value   INTEGER CHECK (cap_value IS NULL OR cap_value > 0),
          cap_scope   TEXT CHECK (cap_scope IN ('single','period')),
          archived_at INTEGER,
          rev         INTEGER NOT NULL DEFAULT 1,
          updated_at  INTEGER NOT NULL,
          deleted_at  INTEGER,
          CHECK (cap_value IS NULL OR cap_scope IS NOT NULL)
        );
        CREATE INDEX IF NOT EXISTS idx_rules_updated ON rules(updated_at);

        -- 事件计数：只数次数，不产生积分。单独两张表，绝不混进 entries。
        CREATE TABLE IF NOT EXISTS counters (
          id          TEXT PRIMARY KEY,
          label       TEXT NOT NULL,
          icon        TEXT,
          sort        INTEGER NOT NULL DEFAULT 0,
          preset_key  TEXT UNIQUE,
          archived_at INTEGER,
          rev         INTEGER NOT NULL DEFAULT 1,
          updated_at  INTEGER NOT NULL,
          deleted_at  INTEGER
        );

        CREATE TABLE IF NOT EXISTS counter_ticks (
          seq           INTEGER PRIMARY KEY AUTOINCREMENT,
          id            TEXT NOT NULL UNIQUE,
          counter_id    TEXT NOT NULL REFERENCES counters(id),
          student_id    TEXT NOT NULL REFERENCES students(id),
          period_id     TEXT NOT NULL REFERENCES periods(id),
          value         INTEGER NOT NULL CHECK (value <> 0),
          note          TEXT,
          operator_id   TEXT NOT NULL,
          operator_name TEXT NOT NULL,
          occurred_at   INTEGER NOT NULL,
          created_at    INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_ticks_count ON counter_ticks(period_id, counter_id, student_id);
        CREATE INDEX IF NOT EXISTS idx_ticks_time ON counter_ticks(period_id, occurred_at DESC);

        CREATE TABLE IF NOT EXISTS note_templates (
          id           TEXT PRIMARY KEY,
          text         TEXT NOT NULL,
          use_count    INTEGER NOT NULL DEFAULT 1,
          last_used_at INTEGER NOT NULL,
          archived_at  INTEGER,
          rev          INTEGER NOT NULL DEFAULT 1,
          updated_at   INTEGER NOT NULL,
          deleted_at   INTEGER
        );
        CREATE UNIQUE INDEX IF NOT EXISTS uq_note_text ON note_templates(text) WHERE deleted_at IS NULL;

        CREATE TABLE IF NOT EXISTS periods (
          id         TEXT PRIMARY KEY,
          name       TEXT NOT NULL,
          term_key   TEXT NOT NULL,
          start_at   INTEGER NOT NULL,
          end_at     INTEGER,
          status     TEXT NOT NULL CHECK (status IN ('active','archived')),
          created_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_periods_term ON periods(term_key);
        CREATE UNIQUE INDEX IF NOT EXISTS uq_period_active ON periods(status) WHERE status = 'active';

        CREATE TABLE IF NOT EXISTS entries (
          seq               INTEGER PRIMARY KEY AUTOINCREMENT,
          id                TEXT NOT NULL UNIQUE,
          batch_id          TEXT NOT NULL,
          period_id         TEXT NOT NULL REFERENCES periods(id),
          student_id        TEXT NOT NULL REFERENCES students(id),
          rule_id           TEXT,
          rule_label        TEXT,
          rule_delta_default INTEGER,
          delta             INTEGER NOT NULL,
          source            TEXT NOT NULL DEFAULT 'rule' CHECK (source IN ('rule','custom')),
          note              TEXT,
          note_template_id  TEXT,
          capped            INTEGER NOT NULL DEFAULT 0,
          over_cap          INTEGER NOT NULL DEFAULT 0,
          operator_id       TEXT NOT NULL,
          operator_name     TEXT NOT NULL,
          identify_method   TEXT,
          occurred_at       INTEGER NOT NULL,
          created_at        INTEGER NOT NULL,
          reverses_entry_id TEXT,
          reversed_at       INTEGER,
          reversed_by       TEXT,
          synced            INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS idx_entries_period  ON entries(period_id, created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_entries_student ON entries(student_id, created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_entries_batch   ON entries(batch_id);
        CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_reversal ON entries(reverses_entry_id)
          WHERE reverses_entry_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS exams (
          id           TEXT PRIMARY KEY,
          period_id    TEXT NOT NULL REFERENCES periods(id),
          name         TEXT NOT NULL,
          full_score   REAL NOT NULL DEFAULT 750,
          exam_date    TEXT,
          imported_at  INTEGER NOT NULL,
          source_file  TEXT
        );

        CREATE TABLE IF NOT EXISTS exam_scores (
          exam_id     TEXT NOT NULL REFERENCES exams(id),
          student_id  TEXT NOT NULL REFERENCES students(id),
          score       REAL,
          absent      INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (exam_id, student_id),
          CHECK (absent = 1 OR score IS NOT NULL)
        );

        CREATE TABLE IF NOT EXISTS schemes (
          id           TEXT PRIMARY KEY,
          name         TEXT NOT NULL,
          exam_weight  REAL NOT NULL DEFAULT 0.7,
          daily_weight REAL NOT NULL DEFAULT 0.3,
          normalize    INTEGER NOT NULL DEFAULT 0,
          is_active    INTEGER NOT NULL DEFAULT 0,
          created_at   INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS uq_scheme_active ON schemes(is_active) WHERE is_active = 1;

        CREATE TABLE IF NOT EXISTS scheme_weights (
          scheme_id TEXT NOT NULL REFERENCES schemes(id),
          exam_id   TEXT NOT NULL REFERENCES exams(id),
          weight    REAL NOT NULL CHECK (weight >= 0),
          PRIMARY KEY (scheme_id, exam_id)
        );

        CREATE TABLE IF NOT EXISTS settings (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS audit_log (
          id       INTEGER PRIMARY KEY AUTOINCREMENT,
          at       INTEGER NOT NULL,
          actor_id TEXT,
          action   TEXT NOT NULL,
          target   TEXT,
          detail   TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_audit_at ON audit_log(at DESC);

        CREATE TABLE IF NOT EXISTS backups (
          id          INTEGER PRIMARY KEY AUTOINCREMENT,
          at          INTEGER NOT NULL,
          path        TEXT NOT NULL,
          entry_count INTEGER NOT NULL,
          ok          INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS outbox (
          id         TEXT PRIMARY KEY,
          kind       TEXT NOT NULL CHECK (kind IN ('entry','reverse','student','rule','period','exam','member','note','scheme')),
          payload    TEXT NOT NULL,
          attempts   INTEGER NOT NULL DEFAULT 0,
          next_at    INTEGER NOT NULL,
          created_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_outbox_next ON outbox(next_at);

        CREATE TABLE IF NOT EXISTS sync_state (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );
        """;

    // ---------- 写入：入账 ----------
    public const string InsertEntry = """
        INSERT INTO entries
          (id, batch_id, period_id, student_id, rule_id, rule_label, rule_delta_default, delta, source,
           note, note_template_id, capped, over_cap, operator_id, operator_name, identify_method,
           occurred_at, created_at, reverses_entry_id, reversed_at, reversed_by, synced)
        VALUES
          ($id, $batch, $period, $student, $rule, $label, $ruleDefault, $delta, $source,
           $note, $noteTpl, $capped, $overCap, $opId, $opName, $method,
           $occurred, $created, $reverses, $reversedAt, $reversedBy, $synced)
        """;

    public const string MarkBatchReversed = """
        UPDATE entries SET reversed_at = $at, reversed_by = $by
        WHERE batch_id = $batch AND reversed_at IS NULL AND reverses_entry_id IS NULL
        """;

    public const string MarkSingleReversed = """
        UPDATE entries SET reversed_at = $at, reversed_by = $by
        WHERE id = $id AND reversed_at IS NULL AND reverses_entry_id IS NULL
        """;

    // ---------- 查询：总分 ----------
    /// <summary>当前周期总分。注意不过滤 reversed_at —— 冲销行自己抵消，见方案 6.4。</summary>
    public const string PeriodTotals = """
        SELECT s.id AS id, COALESCE(SUM(e.delta), 0) AS pts
        FROM students s
        LEFT JOIN entries e ON e.student_id = s.id AND e.period_id = $period
        WHERE s.active = 1 AND s.deleted_at IS NULL
        GROUP BY s.id
        """;

    /// <summary>学期累计总分（跨周期相加），总评用这个。</summary>
    public const string TermTotals = """
        SELECT s.id AS id, COALESCE(SUM(e.delta), 0) AS pts
        FROM students s
        LEFT JOIN entries e ON e.student_id = s.id
        LEFT JOIN periods p ON p.id = e.period_id
        WHERE s.active = 1 AND s.deleted_at IS NULL
          AND (e.id IS NULL OR p.term_key = $term)
        GROUP BY s.id
        """;

    public const string StudentTotals = """
        SELECT COALESCE(SUM(delta), 0) FROM entries WHERE student_id = $student AND period_id = $period
        """;

    public const string StudentRuleTotal = """
        SELECT COALESCE(SUM(delta), 0) FROM entries
        WHERE student_id = $student AND period_id = $period AND rule_id = $rule
        """;

    /// <summary>明细列表：不显示冲销行本身。</summary>
    public const string EntriesByPeriod = """
        SELECT e.*, s.name AS student_name
        FROM entries e JOIN students s ON s.id = e.student_id
        WHERE e.period_id = $period AND e.reverses_entry_id IS NULL
        ORDER BY e.created_at DESC, e.seq DESC
        LIMIT $limit OFFSET $offset
        """;

    public const string EntriesByStudent = """
        SELECT e.*, s.name AS student_name
        FROM entries e JOIN students s ON s.id = e.student_id
        WHERE e.student_id = $student AND e.reverses_entry_id IS NULL
        ORDER BY e.created_at DESC, e.seq DESC
        LIMIT $limit
        """;

    public const string EntryById = """
        SELECT e.*, s.name AS student_name FROM entries e JOIN students s ON s.id = e.student_id
        WHERE e.id = $id
        """;

    public const string ReversibleInBatch = """
        SELECT COUNT(*) FROM entries
        WHERE batch_id = $batch AND reversed_at IS NULL AND reverses_entry_id IS NULL
        """;

    public const string BatchRows = """
        SELECT e.*, s.name AS student_name FROM entries e JOIN students s ON s.id = e.student_id
        WHERE e.batch_id = $batch AND e.reverses_entry_id IS NULL
        ORDER BY e.seq
        """;

    /// <summary>按项目统计：次数与总分（行为分布看板 / 查分页条形图都靠它）。</summary>
    public const string RuleDistribution = """
        SELECT COALESCE(e.rule_label, '其他') AS label, COUNT(*) AS cnt, COALESCE(SUM(e.delta), 0) AS pts
        FROM entries e
        WHERE e.period_id = $period AND e.student_id = $student AND e.reverses_entry_id IS NULL
        GROUP BY e.rule_label
        ORDER BY cnt DESC
        """;

    public const string ClassRuleDistribution = """
        SELECT COALESCE(e.rule_label, '其他') AS label, COUNT(*) AS cnt, COALESCE(SUM(e.delta), 0) AS pts
        FROM entries e
        WHERE e.period_id = $period AND e.reverses_entry_id IS NULL
        GROUP BY e.rule_label
        ORDER BY cnt DESC
        """;

    public const string TodayDelta = """
        SELECT COALESCE(SUM(delta), 0) FROM entries
        WHERE student_id = $student AND period_id = $period AND occurred_at >= $from AND occurred_at < $to
        """;

    public const string TodayDeltas = """
        SELECT student_id AS id, COALESCE(SUM(delta), 0) AS pts FROM entries
        WHERE period_id = $period AND occurred_at >= $from AND occurred_at < $to
        GROUP BY student_id
        """;

    public const string GroupTotals = """
        SELECT COALESCE(NULLIF(TRIM(s.group_name), ''), '未分组') AS grp,
               COALESCE(SUM(e.delta), 0) AS pts, COUNT(DISTINCT s.id) AS members
        FROM students s
        LEFT JOIN entries e ON e.student_id = s.id AND e.period_id = $period
        WHERE s.active = 1 AND s.deleted_at IS NULL
        GROUP BY grp
        ORDER BY pts DESC
        """;

    // ---------- 事件计数 ----------
    /// <summary>整班整周期的次数表：counter_id + student_id → 次数。一次查全，别在界面里 N×M 次查。</summary>
    public const string CounterCounts = """
        SELECT counter_id AS cid, student_id AS sid, SUM(value) AS n
        FROM counter_ticks
        WHERE period_id = $period
        GROUP BY cid, sid
        """;

    /// <summary>某个学生在一个周期里各类事件各几次。</summary>
    public const string CounterCountsForStudent = """
        SELECT counter_id AS cid, SUM(value) AS n
        FROM counter_ticks
        WHERE period_id = $period AND student_id = $student
        GROUP BY cid
        """;

    public const string CounterTotalForStudent = """
        SELECT COALESCE(SUM(value), 0) FROM counter_ticks
        WHERE period_id = $period AND student_id = $student AND counter_id = $counter
        """;

    public const string CounterClassTotal = """
        SELECT COALESCE(SUM(value), 0) FROM counter_ticks
        WHERE period_id = $period AND counter_id = $counter
        """;

    public const string CounterTicksRecent = """
        SELECT t.*, s.name AS student_name, c.label AS counter_label
        FROM counter_ticks t
        JOIN students s ON s.id = t.student_id
        JOIN counters c ON c.id = t.counter_id
        WHERE t.period_id = $period
        ORDER BY t.created_at DESC, t.seq DESC
        LIMIT $limit
        """;

    public const string CounterTicksForStudent = """
        SELECT t.*, s.name AS student_name, c.label AS counter_label
        FROM counter_ticks t
        JOIN students s ON s.id = t.student_id
        JOIN counters c ON c.id = t.counter_id
        WHERE t.student_id = $student AND t.period_id = $period
        ORDER BY t.created_at DESC, t.seq DESC
        LIMIT $limit
        """;

    // ---------- 同步 ----------
    public const string EnqueueOutbox = """
        INSERT OR REPLACE INTO outbox (id, kind, payload, attempts, next_at, created_at)
        VALUES ($id, $kind, $payload, 0, $next, $now)
        """;

    public const string DueOutbox = """
        SELECT * FROM outbox WHERE next_at <= $now ORDER BY created_at LIMIT $limit
        """;

    public const string DeleteOutbox = "DELETE FROM outbox WHERE id = $id";

    public const string BumpOutboxAttempt = """
        UPDATE outbox SET attempts = attempts + 1, next_at = $next WHERE id = $id
        """;

    public const string OutboxCount = "SELECT COUNT(*) FROM outbox";

    public const string SettingGet = "SELECT value FROM settings WHERE key = $key";

    public const string SettingSet = """
        INSERT INTO settings (key, value) VALUES ($key, $value)
        ON CONFLICT(key) DO UPDATE SET value = excluded.value
        """;

    public const string SyncStateGet = "SELECT value FROM sync_state WHERE key = $key";

    public const string SyncStateSet = """
        INSERT INTO sync_state (key, value) VALUES ($key, $value)
        ON CONFLICT(key) DO UPDATE SET value = excluded.value
        """;
}
