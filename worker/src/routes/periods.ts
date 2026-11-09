/**
 * 周期与重置。
 * 重置是整个系统里唯一必须加锁的操作：一体机上点「重置学期」时，如果电脑端正在导入成绩，
 * 两边对「当前周期」的理解会分叉，一部分记录会落到已归档的周期里。
 *
 * 做法是两阶段：
 *   request → 拿令牌，班级标记 resetting=1，两端期间拒绝普通写入
 *   commit  → 归档旧周期 + 建新周期 + 释放标记，全部在一个 batch 里
 * 令牌 90 秒有效，归档过程中每 45 秒续期一次。比"最后写入胜"安全，也比真分布式锁便宜。
 */
import type { Env } from '../types';
import { requireAuth } from '../auth';
import {
  HttpError,
  changeStmt,
  json,
  now,
  optionalString,
  randomHex,
  readJson,
  requireString,
} from '../util';

const TOKEN_TTL_MS = 90_000;

interface PeriodDto {
  id?: string;
  name?: string;
  termKey?: string;
  startAt?: number;
  endAt?: number | null;
  status?: string;
  createdAt?: number;
}

/** 普通写入：新建/更新一个周期。重置请走 reset，别用这个去改 active。 */
export async function upsertPeriod(env: Env, request: Request): Promise<Response> {
  const claims = await requireAuth(env, request);
  const body = await readJson<{ action?: string; period?: PeriodDto } & PeriodDto>(request);
  const dto = body.period ?? body;
  const id = requireString(dto.id, 'id', 40);
  const status = dto.status === 'archived' ? 'archived' : 'active';
  const at = now();

  const stmts: D1PreparedStatement[] = [];
  // uq_period_active 是唯一索引：一个班同时只能有一个 active
  if (status === 'active') {
    stmts.push(env.DB.prepare("UPDATE periods SET status = 'archived', end_at = COALESCE(end_at, ?) WHERE class_id = ? AND status = 'active' AND id <> ?")
      .bind(at, claims.cid, id));
  }
  stmts.push(
    env.DB.prepare(
      `INSERT INTO periods (id, class_id, name, term_key, start_at, end_at, status, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(id) DO UPDATE SET
         name = excluded.name, term_key = excluded.term_key, start_at = excluded.start_at,
         end_at = excluded.end_at, status = excluded.status
       WHERE periods.class_id = excluded.class_id`,
    ).bind(
      id,
      claims.cid,
      requireString(dto.name, 'name', 60),
      requireString(dto.termKey, 'termKey', 20),
      typeof dto.startAt === 'number' ? dto.startAt : at,
      typeof dto.endAt === 'number' ? dto.endAt : null,
      status,
      typeof dto.createdAt === 'number' ? dto.createdAt : at,
    ),
    changeStmt(env.DB, claims.cid, 'periods', id, 'upsert', at),
  );

  await env.DB.batch(stmts);
  return json({ ok: true, id, status, classId: claims.cid });
}

export async function resetPeriod(env: Env, request: Request): Promise<Response> {
  const claims = await requireAuth(env, request);
  const body = await readJson<{ phase?: string; token?: string; period?: PeriodDto } & PeriodDto>(request);
  const phase = requireString(body.phase, 'phase', 20);

  if (phase === 'request') {
    const token = randomHex(16);
    const at = now();
    await env.DB.batch([
      env.DB.prepare('INSERT INTO reset_tokens (token, class_id, issued_at, expires_at) VALUES (?, ?, ?, ?)')
        .bind(token, claims.cid, at, at + TOKEN_TTL_MS),
      env.DB.prepare('UPDATE classes SET resetting = 1 WHERE id = ?').bind(claims.cid),
    ]);
    return json({ token, expiresAt: at + TOKEN_TTL_MS });
  }

  if (phase === 'renew') {
    const token = requireString(body.token, 'token', 64);
    const at = now();
    const res = await env.DB.prepare(
      'UPDATE reset_tokens SET expires_at = ? WHERE token = ? AND class_id = ? AND committed_at IS NULL',
    ).bind(at + TOKEN_TTL_MS, token, claims.cid).run();
    if ((res.meta?.changes ?? 0) === 0) throw new HttpError(409, 'token_invalid', '重置令牌已失效，请重新发起');
    return json({ ok: true, expiresAt: at + TOKEN_TTL_MS });
  }

  if (phase === 'abort') {
    const token = optionalString(body.token, 64);
    await env.DB.batch([
      env.DB.prepare('DELETE FROM reset_tokens WHERE token = ? AND class_id = ?').bind(token, claims.cid),
      env.DB.prepare('UPDATE classes SET resetting = 0 WHERE id = ?').bind(claims.cid),
    ]);
    return json({ ok: true, aborted: true });
  }

  if (phase !== 'commit') throw new HttpError(422, 'bad_phase', 'phase 只能是 request / renew / abort / commit');

  const token = requireString(body.token, 'token', 64);
  const row = await env.DB.prepare(
    'SELECT expires_at, committed_at FROM reset_tokens WHERE token = ? AND class_id = ?',
  ).bind(token, claims.cid).first<{ expires_at: number; committed_at: number | null }>();

  if (!row) throw new HttpError(409, 'token_invalid', '重置令牌不存在');
  if (row.committed_at) throw new HttpError(409, 'already_committed', '这次重置已经提交过了');
  if (row.expires_at < now()) throw new HttpError(409, 'token_expired', '重置令牌已过期，请重新发起');

  const dto = body.period ?? body;
  const id = requireString(dto.id, 'id', 40);
  const at = now();

  await env.DB.batch([
    env.DB.prepare("UPDATE periods SET status = 'archived', end_at = COALESCE(end_at, ?) WHERE class_id = ? AND status = 'active'")
      .bind(at, claims.cid),
    env.DB.prepare(
      `INSERT INTO periods (id, class_id, name, term_key, start_at, end_at, status, created_at)
       VALUES (?, ?, ?, ?, ?, NULL, 'active', ?)`,
    ).bind(
      id,
      claims.cid,
      requireString(dto.name, 'name', 60),
      requireString(dto.termKey, 'termKey', 20),
      typeof dto.startAt === 'number' ? dto.startAt : at,
      at,
    ),
    changeStmt(env.DB, claims.cid, 'periods', id, 'upsert', at),
    env.DB.prepare('UPDATE reset_tokens SET committed_at = ? WHERE token = ?').bind(at, token),
    env.DB.prepare('UPDATE classes SET resetting = 0 WHERE id = ?').bind(claims.cid),
  ]);

  return json({ ok: true, periodId: id, committedAt: at });
}
