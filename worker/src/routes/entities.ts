/**
 * 通用实体写入：students / rules / members / note_templates / schemes。
 * 这几张表没有特殊逻辑，硬给每个开一个端点只会多写五套一模一样的校验。
 *
 * 并发：客户端把整条对象连同 rev 一起发上来。
 * - 离线补推（outbox 重放）走 rev 比较：excluded.rev > 库里的 rev 才覆盖，否则当作旧消息丢弃，
 *   不返回 409 —— 否则队列会卡死在一条永远推不上去的旧记录上。
 * - 人在界面上改（电脑端交互）带 If-Match: rev，严格比对，不匹配返回 409，
 *   让 UI 弹"用我的 / 用教室那台的"。
 *
 * 枚举坑：客户端用 System.Text.Json 默认序列化，C# enum 落到 JSON 里是数字，不是字符串。
 * 所以 role / cap_scope 要在这里翻译，不能指望拿到 'owner'。
 */
import type { Env } from '../types';
import { requireAuth } from '../auth';
import {
  HttpError,
  bit,
  changeStmt,
  intOrNull,
  json,
  now,
  optionalString,
  readJson,
  requireString,
} from '../util';

type Kind = 'text' | 'int' | 'real' | 'bool';

interface Col {
  /** 客户端 JSON 字段名（camelCase） */
  key: string;
  /** 数据库列名（snake_case） */
  col: string;
  kind: Kind;
  /** 缺省值（客户端没发这个字段时用） */
  dflt?: string | number | null;
  /** 值翻译，枚举、时间戳这类走这里 */
  map?: (v: unknown) => string | number | null;
}

interface Spec {
  /** 规范路径用的名字 */
  entity: string;
  table: string;
  cols: Col[];
  hasRev: boolean;
}

const str = (v: unknown): string | null => (typeof v === 'string' ? v : v == null ? null : String(v));
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);

/** MemberRole: Owner=0, CoOwner=1, Officer=2 */
function roleOf(v: unknown): string {
  if (typeof v === 'string') {
    const s = v.toLowerCase();
    if (s === 'owner' || s === 'co_owner' || s === 'officer') return s;
  }
  return v === 0 ? 'owner' : v === 1 ? 'co_owner' : 'officer';
}

/** CapScope: None=0, Single=1, Period=2 */
function capScopeOf(v: unknown): string | null {
  if (typeof v === 'string') {
    const s = v.toLowerCase();
    return s === 'single' || s === 'period' ? s : null;
  }
  return v === 1 ? 'single' : v === 2 ? 'period' : null;
}

const column = (key: string, col: string, kind: Kind = 'text', extra: Partial<Col> = {}): Col => ({
  key, col, kind, ...extra,
});

const SPECS: Record<string, Spec> = {
  students: {
    entity: 'students',
    table: 'students',
    hasRev: true,
    cols: [
      column('name', 'name'),
      column('studentNo', 'student_no'),
      column('seatNo', 'seat_no', 'int'),
      column('groupName', 'group_name'),
      column('active', 'active', 'bool', { dflt: 1 }),
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
      column('createdAt', 'created_at', 'int', { dflt: 0 }),
    ],
  },
  rules: {
    entity: 'rules',
    table: 'rules',
    hasRev: true,
    cols: [
      column('label', 'label'),
      column('delta', 'delta', 'int', { dflt: 0 }),
      column('icon', 'icon', 'text', { dflt: '⭐' }),
      column('sort', 'sort', 'int', { dflt: 0 }),
      column('presetKey', 'preset_key'),
      column('capValue', 'cap_value', 'int'),
      column('capScope', 'cap_scope', 'text', { map: capScopeOf }),
      column('archivedAt', 'archived_at', 'int'),
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
    ],
  },
  members: {
    entity: 'members',
    table: 'members',
    hasRev: true,
    cols: [
      column('displayName', 'display_name'),
      column('role', 'role', 'text', { map: roleOf, dflt: 'officer' }),
      column('pinHash', 'pin_hash'),
      column('createdAt', 'created_at', 'int', { dflt: 0 }),
      column('revokedAt', 'revoked_at', 'int'),
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
    ],
  },
  note_templates: {
    entity: 'note_templates',
    table: 'note_templates',
    hasRev: true,
    cols: [
      column('text', 'text'),
      column('useCount', 'use_count', 'int', { dflt: 1 }),
      column('lastUsedAt', 'last_used_at', 'int', { dflt: 0 }),
      column('archivedAt', 'archived_at', 'int'),
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
    ],
  },
  schemes: {
    entity: 'schemes',
    table: 'schemes',
    hasRev: false,
    cols: [
      column('name', 'name'),
      column('examWeight', 'exam_weight', 'real', { dflt: 0.7 }),
      column('dailyWeight', 'daily_weight', 'real', { dflt: 0.3 }),
      column('normalize', 'normalize', 'bool', { dflt: 0 }),
      column('isActive', 'is_active', 'bool', { dflt: 0 }),
      column('createdAt', 'created_at', 'int', { dflt: 0 }),
    ],
  },
};

/** 客户端 outbox 的 kind 用的是单数名，这里做一次别名收敛 */
const ALIASES: Record<string, string> = {
  student: 'students', students: 'students',
  rule: 'rules', rules: 'rules',
  member: 'members', members: 'members',
  note: 'note_templates', notes: 'note_templates', note_templates: 'note_templates',
  scheme: 'schemes', schemes: 'schemes',
};

export function resolveEntity(raw: string): Spec | null {
  const name = ALIASES[raw];
  return name ? SPECS[name] : null;
}

function coerce(col: Col, raw: unknown): string | number | null {
  if (col.map) return col.map(raw);
  if (raw === undefined || raw === null) return col.dflt ?? null;
  switch (col.kind) {
    case 'int':
    case 'real':
      return num(raw) ?? col.dflt ?? null;
    case 'bool':
      return bit(raw);
    default:
      return str(raw) ?? col.dflt ?? null;
  }
}

/** 从客户端 payload 里取出实体对象：{action:'upsert', student:{...}} */
function pickEntity(body: Record<string, unknown>, spec: Spec): Record<string, unknown> {
  const keys = [spec.entity, spec.entity.replace(/s$/, ''), 'student', 'rule', 'member', 'note', 'scheme', 'period', 'exam'];
  for (const k of keys) {
    const v = body[k];
    if (v && typeof v === 'object') return v as Record<string, unknown>;
  }
  if (typeof body.id === 'string') return body; // 直接发裸对象也收
  throw new HttpError(422, 'no_payload', `请求体里找不到 ${spec.entity} 对象`);
}

export async function upsertEntity(env: Env, request: Request, rawEntity: string): Promise<Response> {
  const claims = await requireAuth(env, request);
  const spec = resolveEntity(rawEntity);
  if (!spec) throw new HttpError(404, 'unknown_entity', `未知实体 ${rawEntity}`);

  const body = await readJson<Record<string, unknown>>(request);
  const action = (body.action as string) ?? 'upsert';
  const data = pickEntity(body, spec);
  const id = requireString(data.id ?? body.id, 'id', 40);
  const at = now();

  if (action === 'delete' || action === 'restore') {
    let stmt: D1PreparedStatement;
    // 每条 UPDATE 都带 class_id：否则拿到别的班某个 id 就能把别人的记录删掉
    if (action === 'delete') {
      const deletedAt = intOrNull(body.deletedAt) ?? at;
      stmt = spec.hasRev
        ? env.DB.prepare(`UPDATE ${spec.table} SET deleted_at = ?, rev = rev + 1, updated_at = ? WHERE id = ? AND class_id = ?`)
            .bind(deletedAt, at, id, claims.cid)
        : env.DB.prepare(`UPDATE ${spec.table} SET deleted_at = ? WHERE id = ? AND class_id = ?`).bind(deletedAt, id, claims.cid);
    } else {
      stmt = spec.hasRev
        ? env.DB.prepare(`UPDATE ${spec.table} SET deleted_at = NULL, rev = rev + 1, updated_at = ? WHERE id = ? AND class_id = ?`)
            .bind(at, id, claims.cid)
        : env.DB.prepare(`UPDATE ${spec.table} SET deleted_at = NULL WHERE id = ? AND class_id = ?`).bind(id, claims.cid);
    }
    await env.DB.batch([
      stmt,
      changeStmt(env.DB, claims.cid, spec.entity, id, action === 'delete' ? 'delete' : 'upsert', at),
    ]);
    return json({ ok: true, id, action });
  }

  const incomingRev = spec.hasRev ? intOrNull(data.rev) ?? 1 : 0;
  const ifMatch = request.headers.get('if-match');

  if (spec.hasRev && ifMatch !== null) {
    const current = await env.DB.prepare(`SELECT rev FROM ${spec.table} WHERE id = ? AND class_id = ?`)
      .bind(id, claims.cid)
      .first<{ rev: number }>();
    if (current && String(current.rev) !== ifMatch.trim()) {
      const row = await env.DB.prepare(`SELECT * FROM ${spec.table} WHERE id = ? AND class_id = ?`)
        .bind(id, claims.cid)
        .first();
      // 交给客户端弹"用我的 / 用教室那台的"
      return json({ error: 'conflict', message: '这条记录刚在别处被改过', server: row }, 409);
    }
  }

  const cols = spec.cols;
  const names = ['id', 'class_id', ...cols.map((c) => c.col)];
  if (spec.hasRev) names.push('rev');
  const placeholders = names.map(() => '?').join(', ');
  const values: (string | number | null)[] = [id, claims.cid, ...cols.map((c) => coerce(c, data[c.key]))];
  if (spec.hasRev) values.push(incomingRev);

  const updates = cols.map((c) => `${c.col} = excluded.${c.col}`).join(', ');
  // rev 比较：只有更新的版本能覆盖。相等视为同一条消息重放，直接忽略。
  // class_id 也进 WHERE：主键是全局唯一 id，万一两个班撞了同一个 id，
  // 后到的那个绝不允许改掉别人的行 —— 宁可这条不落地，也不能串班。
  const guard = spec.hasRev
    ? ` WHERE excluded.rev > ${spec.table}.rev AND ${spec.table}.class_id = excluded.class_id`
    : ` WHERE ${spec.table}.class_id = excluded.class_id`;
  const sql = `INSERT INTO ${spec.table} (${names.join(', ')}) VALUES (${placeholders})
               ON CONFLICT(id) DO UPDATE SET ${updates}${spec.hasRev ? ', rev = excluded.rev' : ''}${guard}`;

  const results = await env.DB.batch([
    env.DB.prepare(sql).bind(...values),
    changeStmt(env.DB, claims.cid, spec.entity, id, 'upsert', at),
  ]);

  const applied = (results[0].meta?.changes ?? 0) > 0;
  return json({ ok: true, id, applied, rev: incomingRev, classId: claims.cid }, applied ? 200 : 200);
}

/** 各场考试的权重：一次换掉，不是逐条改 */
export async function putSchemeWeights(env: Env, request: Request, schemeId: string): Promise<Response> {
  const claims = await requireAuth(env, request);
  const body = await readJson<{ weights?: Record<string, number> | { examId: string; weight: number }[] }>(request);
  const raw = body.weights ?? {};
  const pairs: { examId: string; weight: number }[] = Array.isArray(raw)
    ? raw.map((w) => ({ examId: String(w.examId), weight: Number(w.weight) }))
    : Object.entries(raw).map(([examId, weight]) => ({ examId, weight: Number(weight) }));

  // 先确认这个折算方案属于本班，不然就是拿别人的 scheme_id 来改别人的权重
  const owned = await env.DB.prepare('SELECT 1 AS ok FROM schemes WHERE id = ? AND class_id = ?')
    .bind(schemeId, claims.cid)
    .first<{ ok: number }>();
  if (!owned) throw new HttpError(404, 'scheme_not_found', '这个折算方案不属于当前班级');

  const at = now();
  const stmts: D1PreparedStatement[] = [
    env.DB.prepare('DELETE FROM scheme_weights WHERE scheme_id = ? AND class_id = ?').bind(schemeId, claims.cid),
  ];
  for (const p of pairs) {
    stmts.push(
      env.DB.prepare('INSERT INTO scheme_weights (scheme_id, exam_id, class_id, weight) VALUES (?, ?, ?, ?)')
        .bind(schemeId, p.examId, claims.cid, p.weight),
    );
  }
  stmts.push(changeStmt(env.DB, claims.cid, 'scheme_weights', schemeId, 'upsert', at));
  await env.DB.batch(stmts);
  return json({ ok: true, count: pairs.length });
}

export { SPECS };
