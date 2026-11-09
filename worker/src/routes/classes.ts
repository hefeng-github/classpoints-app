/**
 * 班级与配对。
 * 向导第 1 步只调用一次 /classes —— 这个端点最容易在接口表里被漏掉，
 * 因为整个系统只用到它一次。
 */
import type { Claims, Env } from '../types';
import {
  accessTtlSec,
  hashPassword,
  refreshTtlSec,
  requireAuth,
  sha256Hex,
  signToken,
  verifyPassword,
} from '../auth';
import {
  HttpError,
  changeStmt,
  classCode,
  json,
  now,
  optionalString,
  randomHex,
  readJson,
  requireString,
  uuidv7,
} from '../util';

interface CreateClassBody {
  name?: string;
  ownerName?: string;
  ownerPin?: string;
  recoveryCode?: string;
  kind?: 'board' | 'desk';
  deviceLabel?: string;
}

interface PairBody {
  code?: string;
  secret?: string;
  kind?: 'board' | 'desk';
  deviceLabel?: string;
}

async function issueDevice(
  env: Env,
  classId: string,
  kind: 'board' | 'desk',
  role: 'owner' | 'co_owner' | 'officer',
  label: string | null,
): Promise<{ deviceId: string; access: string; refresh: string; accessExpiresAt: number; refreshExpiresAt: number }> {
  const deviceId = uuidv7();
  const refresh = randomHex(32);
  const at = now();
  const refreshExpiresAt = at + refreshTtlSec(env) * 1000;

  await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO devices (id, class_id, kind, label, refresh_hash, created_at, last_seen_at, expires_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).bind(deviceId, classId, kind, label, await sha256Hex(refresh), at, at, refreshExpiresAt),
  ]);

  const access = await signToken(env, { sid: deviceId, cid: classId, kind, role }, accessTtlSec(env));
  return { deviceId, access, refresh, accessExpiresAt: at + accessTtlSec(env) * 1000, refreshExpiresAt };
}

export async function createClass(env: Env, request: Request): Promise<Response> {
  const body = await readJson<CreateClassBody>(request);
  const name = requireString(body.name, 'name', 60);
  const ownerName = optionalString(body.ownerName, 30) ?? '班主任';
  const pin = requireString(body.ownerPin, 'ownerPin', 64);
  if (pin.length < 6) throw new HttpError(422, 'weak_pin', '班主任密码至少 6 位');

  const classId = uuidv7();
  const ownerId = uuidv7();
  const code = classCode(8);
  const at = now();

  const ownerPin = await hashPassword(env, pin);
  const recovery = body.recoveryCode ? await hashPassword(env, body.recoveryCode) : null;

  await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO classes (id, name, code, owner_pin, owner_salt, recovery_hash, recovery_salt, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).bind(classId, name, code, ownerPin.hash, ownerPin.salt, recovery?.hash ?? null, recovery?.salt ?? null, at),
    env.DB.prepare(
      `INSERT INTO members (id, class_id, display_name, role, pin_hash, created_at, rev, updated_at)
       VALUES (?, ?, ?, 'owner', ?, ?, 1, ?)`,
    ).bind(ownerId, classId, ownerName, ownerPin.hash, at, at),
    changeStmt(env.DB, classId, 'members', ownerId, 'upsert', at),
  ]);

  const tokens = await issueDevice(env, classId, body.kind ?? 'board', 'owner', body.deviceLabel ?? null);
  return json({ classId, code, ownerId, ...tokens }, 201);
}

/** 班级码 + 密码（或恢复码）→ 一对 token。恢复码走同一个入口，区别只在比对哪一列。 */
export async function pair(env: Env, request: Request): Promise<Response> {
  const body = await readJson<PairBody>(request);
  const code = requireString(body.code, 'code', 16).trim().toUpperCase();
  const secret = requireString(body.secret, 'secret', 64);

  const row = await env.DB.prepare(
    'SELECT id, owner_pin, owner_salt, recovery_hash, recovery_salt FROM classes WHERE code = ? AND deleted_at IS NULL',
  )
    .bind(code)
    .first<{ id: string; owner_pin: string; owner_salt: string; recovery_hash: string | null; recovery_salt: string | null }>();

  if (!row) throw new HttpError(404, 'class_not_found', '班级码不对，或者这个班已经注销');

  let ok = await verifyPassword(env, secret, row.owner_pin, row.owner_salt);
  if (!ok && row.recovery_hash && row.recovery_salt) {
    ok = await verifyPassword(env, secret, row.recovery_hash, row.recovery_salt);
  }
  // 统一的失败话术：不告诉对方"密码对但恢复码错"这种信息
  if (!ok) throw new HttpError(401, 'bad_secret', '密码不对');

  const tokens = await issueDevice(env, row.id, body.kind ?? 'desk', 'owner', body.deviceLabel ?? null);
  return json({ classId: row.id, code, ...tokens });
}

/** 静默续期。refresh 一次一换（rotate），旧 token 立即作废。 */
export async function refresh(env: Env, request: Request): Promise<Response> {
  const body = await readJson<{ refresh?: string }>(request);
  const token = requireString(body.refresh, 'refresh', 128);
  const hash = await sha256Hex(token);

  const device = await env.DB.prepare(
    `SELECT d.id, d.class_id, d.kind, d.expires_at, d.revoked_at
       FROM devices d WHERE d.refresh_hash = ?`,
  )
    .bind(hash)
    .first<{ id: string; class_id: string; kind: 'board' | 'desk'; expires_at: number; revoked_at: number | null }>();

  if (!device) throw new HttpError(401, 'bad_refresh', '设备凭据无效，请重新配对');
  if (device.revoked_at) throw new HttpError(401, 'device_revoked', '这台设备已被撤销');
  if (device.expires_at < now()) throw new HttpError(401, 'refresh_expired', '设备凭据已过期，请重新配对');

  const next = randomHex(32);
  const at = now();
  const expiresAt = at + refreshTtlSec(env) * 1000;
  await env.DB.batch([
    env.DB.prepare('UPDATE devices SET refresh_hash = ?, last_seen_at = ?, expires_at = ? WHERE id = ?')
      .bind(await sha256Hex(next), at, expiresAt, device.id),
  ]);

  const claims: Omit<Claims, 'iat' | 'exp'> = {
    sid: device.id,
    cid: device.class_id,
    kind: device.kind,
    role: 'owner',
  };
  return json({
    access: await signToken(env, claims, accessTtlSec(env)),
    refresh: next,
    accessExpiresAt: at + accessTtlSec(env) * 1000,
    refreshExpiresAt: expiresAt,
  });
}

/** 换班主任：撤销当前设备。撤销后 refresh 立刻失效，access 到期自然失效。 */
export async function signOut(env: Env, request: Request): Promise<Response> {
  const claims = await requireAuth(env, request);
  await env.DB.prepare('UPDATE devices SET revoked_at = ? WHERE id = ? AND class_id = ?')
    .bind(now(), claims.sid, claims.cid)
    .run();
  return json({ ok: true });
}

/** 唤醒探测：一体机休眠后 TCP 是假活的，客户端每次激活发一个 /ping，2 秒超时重建连接。 */
export async function ping(env: Env, request: Request): Promise<Response> {
  await requireAuth(env, request);
  const row = await env.DB.prepare('SELECT 1 AS ok').first<{ ok: number }>();
  return json({ ok: row?.ok === 1, serverTime: now() });
}
