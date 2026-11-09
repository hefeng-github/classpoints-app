-- 班级积分系统 · D1 初始结构
-- 与本地 SQLite（src/ClassPoints.Data/Sql.cs）保持一致，去掉三张本地专属表：
--   outbox / sync_state / backups
-- 另外补三张只属于服务端的表：classes（班级与配对凭据）、devices（refresh token）、
--   changelog（统一增量游标）、reset_tokens（重置令牌）。
--
-- 关于 changelog：设计稿里增量拉取用 entries.seq 当游标，但那是单表游标。
-- 学生、规则、周期、成绩也要能增量拉，靠各表自己的自增列没法拼成一个整数。
-- 所以服务端维护一张只追加的 changelog，任何写操作都往里加一行，
-- /sync?since= 只认它的 seq。客户端仍然只看到"一个整数游标"，协议没变。
--
-- 关于 class_id：一个 D1 库可以放几十个班（README 是这么承诺的）。
-- 所以**每一张业务表都必须带 class_id**，而且所有查询都要带上它 ——
-- 只要有一处漏了，拿到 A 班令牌的设备就能读到 B 班的名单和流水。
-- 这条不是可选的优化项，是数据隔离的底线。
-- 连带影响：所有 UNIQUE 约束都要变成"班内唯一"，否则第二个班的学生学号、
-- 常用评语、同期 active 周期都会撞上第一个班的那一行。
--
-- 本文件在真实 Cloudflare 部署之前重写过一次（从"无 class_id"改成"全表带 class_id"）。
-- 因为那时远端 D1 还不存在，不存在需要兼容的线上数据；上过线之后再改就必须走
-- 0002_xxx.sql 做 SQLite 的 12 步表重建，不能直接改这个文件。

-- ============ 班级与配对 ============

CREATE TABLE classes (
  id            TEXT PRIMARY KEY,           -- UUIDv7
  name          TEXT NOT NULL,              -- "高二 (3) 班"
  code          TEXT NOT NULL,              -- 班级码，配对用，人念得出来的 8 位
  owner_pin     TEXT NOT NULL,              -- PBKDF2(salt||pin)，绝不存明文
  owner_salt    TEXT NOT NULL,
  recovery_hash TEXT,                       -- 恢复码同样只存哈希
  recovery_salt TEXT,
  resetting     INTEGER NOT NULL DEFAULT 0, -- 1 = 正在重置周期，两端拒绝普通写入
  created_at    INTEGER NOT NULL,
  deleted_at    INTEGER
);
CREATE UNIQUE INDEX uq_classes_code ON classes(code);

-- 设备：refresh token 只存哈希；换设备就多一行，撤销就把 revoked_at 填上
CREATE TABLE devices (
  id           TEXT PRIMARY KEY,
  class_id     TEXT NOT NULL REFERENCES classes(id),
  kind         TEXT NOT NULL CHECK (kind IN ('board','desk')),
  label        TEXT,
  refresh_hash TEXT NOT NULL,
  created_at   INTEGER NOT NULL,
  last_seen_at INTEGER,
  expires_at   INTEGER NOT NULL,
  revoked_at   INTEGER
);
CREATE INDEX idx_devices_class ON devices(class_id, revoked_at);

-- 重置令牌：两阶段提交。90 秒有效，归档进行中每 45 秒续期
CREATE TABLE reset_tokens (
  token      TEXT PRIMARY KEY,
  class_id   TEXT NOT NULL REFERENCES classes(id),
  issued_at  INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  committed_at INTEGER
);

-- ============ 同步游标 ============

-- seq 是全局自增（SQLite 只允许单列 INTEGER PRIMARY KEY AUTOINCREMENT），
-- 但每个班读的是 `WHERE class_id = ? AND seq > ?`，所以对某个班来说游标仍然连续单调。
-- 不同班的 seq 会交错，这不影响正确性：客户端只把它当成"我这台机器读到哪了"。
CREATE TABLE changelog (
  seq      INTEGER PRIMARY KEY AUTOINCREMENT,
  class_id TEXT NOT NULL,
  entity   TEXT NOT NULL,     -- students|rules|members|note_templates|periods|entries|exams|exam_scores|schemes|scheme_weights
  row_id   TEXT NOT NULL,
  op       TEXT NOT NULL CHECK (op IN ('upsert','delete')),
  at       INTEGER NOT NULL
);
CREATE INDEX idx_changelog_class_seq ON changelog(class_id, seq);
CREATE INDEX idx_changelog_entity ON changelog(class_id, entity, row_id);

-- ============ 业务表（照抄设计稿 6.3，全表补 class_id） ============

CREATE TABLE students (
  id          TEXT PRIMARY KEY,             -- 客户端 UUIDv7
  class_id    TEXT NOT NULL,
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
CREATE INDEX idx_students_updated ON students(class_id, updated_at);
CREATE INDEX idx_students_seat ON students(class_id, active, seat_no);
-- 学号在同一张名单里不能重；两个班共用"01 号"是正常的
CREATE UNIQUE INDEX uq_students_no ON students(class_id, student_no) WHERE student_no IS NOT NULL;

CREATE TABLE members (
  id           TEXT PRIMARY KEY,
  class_id     TEXT NOT NULL,
  display_name TEXT NOT NULL,
  role         TEXT NOT NULL CHECK (role IN ('owner','co_owner','officer')),
  pin_hash     TEXT,
  created_at   INTEGER NOT NULL,
  revoked_at   INTEGER,
  rev          INTEGER NOT NULL DEFAULT 1,
  updated_at   INTEGER NOT NULL,
  deleted_at   INTEGER
);
CREATE INDEX idx_members_class ON members(class_id, deleted_at);

CREATE TABLE identities (
  id           TEXT PRIMARY KEY,
  class_id     TEXT NOT NULL,
  member_id    TEXT NOT NULL REFERENCES members(id),
  method       TEXT NOT NULL CHECK (method IN ('nfc','finger','face','pin')),
  credential   TEXT NOT NULL,
  label        TEXT,
  created_at   INTEGER NOT NULL,
  last_used_at INTEGER
);
CREATE UNIQUE INDEX uq_identities_cred ON identities(class_id, method, credential);

CREATE TABLE rules (
  id          TEXT PRIMARY KEY,
  class_id    TEXT NOT NULL,
  label       TEXT NOT NULL,
  delta       INTEGER NOT NULL,
  icon        TEXT,
  sort        INTEGER NOT NULL DEFAULT 0,
  preset_key  TEXT,                       -- 预设规则的去重键，班内唯一（见下面的索引）
  cap_value   INTEGER CHECK (cap_value IS NULL OR cap_value > 0),
  cap_scope   TEXT CHECK (cap_scope IN ('single','period')),
  archived_at INTEGER,
  rev         INTEGER NOT NULL DEFAULT 1,
  updated_at  INTEGER NOT NULL,
  deleted_at  INTEGER,
  CHECK (cap_value IS NULL OR cap_scope IS NOT NULL)
);
CREATE INDEX idx_rules_updated ON rules(class_id, updated_at);
CREATE UNIQUE INDEX uq_rules_preset ON rules(class_id, preset_key) WHERE preset_key IS NOT NULL;

CREATE TABLE note_templates (
  id           TEXT PRIMARY KEY,
  class_id     TEXT NOT NULL,
  text         TEXT NOT NULL,
  use_count    INTEGER NOT NULL DEFAULT 1,
  last_used_at INTEGER NOT NULL,
  archived_at  INTEGER,
  rev          INTEGER NOT NULL DEFAULT 1,
  updated_at   INTEGER NOT NULL,
  deleted_at   INTEGER
);
CREATE UNIQUE INDEX uq_note_text ON note_templates(class_id, text) WHERE deleted_at IS NULL;

CREATE TABLE periods (
  id         TEXT PRIMARY KEY,
  class_id   TEXT NOT NULL,
  name       TEXT NOT NULL,
  term_key   TEXT NOT NULL,
  start_at   INTEGER NOT NULL,
  end_at     INTEGER,
  status     TEXT NOT NULL CHECK (status IN ('active','archived')),
  created_at INTEGER NOT NULL
);
CREATE INDEX idx_periods_term ON periods(class_id, term_key);
-- 一个班同时只能有一个 active 周期；换个班当然可以有它自己的那个
CREATE UNIQUE INDEX uq_period_active ON periods(class_id) WHERE status = 'active';

CREATE TABLE entries (
  seq                INTEGER PRIMARY KEY AUTOINCREMENT,  -- 服务端游标，不复用
  id                 TEXT NOT NULL,                      -- 客户端 UUIDv7，幂等键
  class_id           TEXT NOT NULL,
  batch_id           TEXT NOT NULL,
  period_id          TEXT NOT NULL REFERENCES periods(id),
  student_id         TEXT NOT NULL REFERENCES students(id),
  rule_id            TEXT,
  rule_label         TEXT,
  rule_delta_default INTEGER,
  delta              INTEGER NOT NULL,
  source             TEXT NOT NULL DEFAULT 'rule' CHECK (source IN ('rule','custom')),
  note               TEXT,
  note_template_id   TEXT,
  capped             INTEGER NOT NULL DEFAULT 0,
  over_cap           INTEGER NOT NULL DEFAULT 0,
  operator_id        TEXT NOT NULL,
  operator_name      TEXT NOT NULL,
  identify_method    TEXT,
  occurred_at        INTEGER NOT NULL,
  created_at         INTEGER NOT NULL,
  reverses_entry_id  TEXT,
  reversed_at        INTEGER,
  reversed_by        TEXT
);
-- 幂等键按班唯一：极低概率的跨班 UUIDv7 撞车不该让后一个班的流水被静默吞掉
CREATE UNIQUE INDEX uq_entries_id ON entries(class_id, id);
CREATE INDEX idx_entries_period  ON entries(class_id, period_id, created_at DESC);
CREATE INDEX idx_entries_student ON entries(class_id, student_id, created_at DESC);
CREATE INDEX idx_entries_batch   ON entries(class_id, batch_id);
-- 同一批次只能撤销一次：靠约束，不靠代码判断
CREATE UNIQUE INDEX uq_entries_reversal ON entries(class_id, reverses_entry_id)
  WHERE reverses_entry_id IS NOT NULL;

CREATE TABLE exams (
  id          TEXT PRIMARY KEY,
  class_id    TEXT NOT NULL,
  period_id   TEXT NOT NULL REFERENCES periods(id),
  name        TEXT NOT NULL,
  full_score  REAL NOT NULL DEFAULT 750,
  exam_date   TEXT,
  imported_at INTEGER NOT NULL,
  source_file TEXT,
  deleted_at  INTEGER
);
CREATE INDEX idx_exams_class ON exams(class_id, deleted_at);

CREATE TABLE exam_scores (
  exam_id    TEXT NOT NULL REFERENCES exams(id),
  student_id TEXT NOT NULL REFERENCES students(id),
  class_id   TEXT NOT NULL,
  score      REAL,
  absent     INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (exam_id, student_id),
  CHECK (absent = 1 OR score IS NOT NULL)
);

CREATE TABLE schemes (
  id           TEXT PRIMARY KEY,
  class_id     TEXT NOT NULL,
  name         TEXT NOT NULL,
  exam_weight  REAL NOT NULL DEFAULT 0.7,
  daily_weight REAL NOT NULL DEFAULT 0.3,
  normalize    INTEGER NOT NULL DEFAULT 0,
  is_active    INTEGER NOT NULL DEFAULT 0,
  created_at   INTEGER NOT NULL,
  deleted_at   INTEGER,
  CHECK (abs(exam_weight + daily_weight - 1.0) < 1e-6)
);
CREATE UNIQUE INDEX uq_scheme_active ON schemes(class_id) WHERE is_active = 1;

CREATE TABLE scheme_weights (
  scheme_id TEXT NOT NULL REFERENCES schemes(id),
  exam_id   TEXT NOT NULL REFERENCES exams(id),
  class_id  TEXT NOT NULL,
  weight    REAL NOT NULL CHECK (weight >= 0),
  PRIMARY KEY (scheme_id, exam_id)
);
