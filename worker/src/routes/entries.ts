/**
 * 流水：唯一事实表。整批一个事务（D1 的 batch 就是事务，且只算一次查询配额）。
 * 幂等靠约束：entries.id UNIQUE + INSERT OR IGNORE，重传自然不会重复计分。
 * 撤销靠约束：uq_entries_reversal（reverses_entry_id 唯一），同一批次只能撤一次。
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

/** 客户端 Entry 的 camelCase 形状（ClassPoints.Core.Entry 直接序列化过来） */
interface EntryDto {
  id?: string;
  batchId?: string;
  periodId?: string;
  studentId?: string;
  ruleId?: string | null;
  ruleLabel?: string | null;
  ruleDeltaDefault?: number | null;
  delta?: number;
  source?: string;
  note?: string | null;
  noteTemplateId?: string | null;
  capped?: boolean;
  overCap?: boolean;
  operatorId?: string;
  operatorName?: string;
  identifyMethod?: string | null;
  occurredAt?: number;
  createdAt?: number;
  reversesEntryId?: string | null;
}

const INSERT = `INSERT OR IGNORE INTO entries
  (id, class_id, batch_id, period_id, student_id, rule_id, rule_label, rule_delta_default, delta, source,
   note, note_template_id, capped, over_cap, operator_id, operator_name, identify_method,
   occurred_at, created_at, reverses_entry_id)
  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`;

function bindEntry(env: Env, classId: string, e: EntryDto): D1PreparedStatement {
  const id = requireString(e.id, 'id', 40);
  const delta = intOrNull(e.delta);
  if (delta === null) throw new HttpError(422, 'invalid_delta', 'delta 必须是整数');
  if (Math.abs(delta) > 50 && !e.overCap) {
    throw new HttpError(422, 'delta_too_large', '单条超过 50 分必须显式 overCap');
  }
  const source = e.source === 'custom' ? 'custom' : 'rule';
  const at = intOrNull(e.occurredAt) ?? now();

  return env.DB.prepare(INSERT).bind(
    id,
    classId,
    requireString(e.batchId, 'batchId', 40),
    requireString(e.periodId, 'periodId', 40),
    requireString(e.studentId, 'studentId', 40),
    optionalString(e.ruleId, 40),
    optionalString(e.ruleLabel, 60),
    intOrNull(e.ruleDeltaDefault),
    delta,
    source,
    optionalString(e.note),
    optionalString(e.noteTemplateId, 40),
    bit(e.capped),
    bit(e.overCap),
    requireString(e.operatorId, 'operatorId', 40),
    requireString(e.operatorName, 'operatorName', 40),
    optionalString(e.identifyMethod, 20),
    at,
    intOrNull(e.createdAt) ?? at,
    optionalString(e.reversesEntryId, 40),
  );
}

export async function postEntries(env: Env, request: Request): Promise<Response> {
  const claims = await requireAuth(env, request);
  const body = await readJson<{ entries?: EntryDto[] } | EntryDto[]>(request);
  const list = Array.isArray(body) ? body : body.entries ?? [];
  if (list.length === 0) throw new HttpError(422, 'empty_batch', '至少要一条记录');
  if (list.length > 50) throw new HttpError(422, 'batch_too_large', '一批最多 50 条，请分批提交');

  const at = now();
  const stmts: D1PreparedStatement[] = [];
  const insertAt: number[] = [];
  for (const e of list) {
    insertAt.push(stmts.length);
    stmts.push(bindEntry(env, claims.cid, e));
    stmts.push(changeStmt(env.DB, claims.cid, 'entries', requireString(e.id, 'id', 40), 'upsert', at));

    // 客户端把撤销当成一条普通流水推上来（冲销行 + 原行指向），
    // 这里负责把原行打上 reversed_at。不然另一台设备拉到的原行还是"有效"的，
    // 于是它的列表里这条会一直显示为正常加过分。
    if (e.reversesEntryId) {
      stmts.push(
        env.DB.prepare('UPDATE entries SET reversed_at = ?, reversed_by = ? WHERE id = ? AND class_id = ? AND reversed_at IS NULL')
          .bind(intOrNull(e.createdAt) ?? at, requireString(e.operatorName, 'operatorName', 40), e.reversesEntryId, claims.cid),
      );
      stmts.push(changeStmt(env.DB, claims.cid, 'entries', e.reversesEntryId, 'upsert', at));
    }
  }

  const results = await env.DB.batch(stmts);
  // 每条插入后面跟着 1 或 2 条附带语句（changelog、给原行打撤销标记），
  // 所以先把插入语句的下标记下来，再按 changes 判断这条是新写入还是被 IGNORE 的重传
  const inserted = insertAt.filter((i) => (results[i].meta?.changes ?? 0) > 0).length;

  return json({ accepted: list.length, inserted, duplicates: list.length - inserted }, 200);
}

/** 按批次撤销：一条冲销行 + 原行打 reversed_at。整批一个事务。 */
export async function reverseBatch(
  env: Env,
  request: Request,
  classId: string,
  batchId: string,
  operator: { id: string; name: string },
): Promise<Response> {
  const at = now();
  const rows = await env.DB.prepare(
    `SELECT id, period_id, student_id, delta, operator_id, operator_name, occurred_at
       FROM entries WHERE class_id = ? AND batch_id = ? AND reverses_entry_id IS NULL AND reversed_at IS NULL`,
  )
    .bind(classId, batchId)
    .all<{ id: string; period_id: string; student_id: string; delta: number; operator_id: string; operator_name: string; occurred_at: number }>();

  const originals = rows.results ?? [];
  if (originals.length === 0) throw new HttpError(404, 'nothing_to_reverse', '这一批已经撤销过，或者不存在');

  const stmts: D1PreparedStatement[] = [];
  for (const o of originals) {
    const reversalId = `${o.id}-rev`; // 由原 id 派生：天然幂等，重传不会造成二次冲销
    stmts.push(
      env.DB.prepare(INSERT).bind(
        reversalId, classId, batchId, o.period_id, o.student_id, null, '撤销', null, -o.delta, 'rule',
        null, null, 0, 0, operator.id, operator.name, null, at, at, o.id,
      ),
    );
    stmts.push(
      env.DB.prepare('UPDATE entries SET reversed_at = ?, reversed_by = ? WHERE id = ? AND class_id = ?')
        .bind(at, operator.name, o.id, classId),
    );
    // 两条都要进 changelog：原行是被标记的，冲销行是新增的。
    // 只记原行的话，另一台设备永远拉不到那条负数。
    stmts.push(changeStmt(env.DB, classId, 'entries', reversalId, 'upsert', at));
    stmts.push(changeStmt(env.DB, classId, 'entries', o.id, 'upsert', at));
  }

  await env.DB.batch(stmts);
  return json({ reversed: originals.length, at });
}
