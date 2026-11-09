/** 统一的 JSON 响应、错误、编码小工具。不引任何依赖，Worker 冷启动才快。 */

const CORS = {
  'access-control-allow-origin': '*',
  'access-control-allow-methods': 'GET,POST,PATCH,OPTIONS',
  'access-control-allow-headers': 'content-type,authorization,if-match',
  'access-control-max-age': '86400',
};

export class HttpError extends Error {
  constructor(readonly status: number, readonly code: string, message: string) {
    super(message);
  }
}

export function json(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', ...CORS },
  });
}

export function preflight(): Response {
  return new Response(null, { status: 204, headers: CORS });
}

export function fail(e: unknown): Response {
  if (e instanceof HttpError) {
    return json({ error: e.code, message: e.message }, e.status);
  }
  const message = e instanceof Error ? e.message : String(e);
  // 不把堆栈发给客户端，但要留在 Workers 日志里
  console.error('unhandled', message, e instanceof Error ? e.stack : '');
  return json({ error: 'internal', message }, 500);
}

export async function readJson<T>(request: Request): Promise<T> {
  const text = await request.text();
  if (!text) throw new HttpError(400, 'empty_body', '请求体是空的');
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new HttpError(400, 'bad_json', '请求体不是合法 JSON');
  }
}

export const now = (): number => Date.now();

export function requireString(v: unknown, field: string, maxLen = 200): string {
  if (typeof v !== 'string' || v.trim() === '') {
    throw new HttpError(422, 'invalid_' + field, `${field} 不能为空`);
  }
  if (v.length > maxLen) throw new HttpError(422, 'invalid_' + field, `${field} 太长`);
  return v;
}

export function optionalString(v: unknown, maxLen = 500): string | null {
  if (v === null || v === undefined) return null;
  if (typeof v !== 'string') return null;
  return v.length > maxLen ? v.slice(0, maxLen) : v;
}

export function intOrNull(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? Math.trunc(v) : null;
}

/** 0/1 —— SQLite 没有布尔 */
export const bit = (v: unknown): number => (v ? 1 : 0);

export function randomHex(bytes: number): string {
  const buf = new Uint8Array(bytes);
  crypto.getRandomValues(buf);
  return [...buf].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** 班级码：去掉 0/O/1/I，人念得出来，也不至于念错 */
const CODE_ALPHABET = '23456789ABCDEFGHJKLMNPQRSTUVWXYZ';

export function classCode(len = 8): string {
  const buf = new Uint8Array(len);
  crypto.getRandomValues(buf);
  return [...buf].map((b) => CODE_ALPHABET[b % CODE_ALPHABET.length]).join('');
}

/**
 * UUIDv7：48 位毫秒时间 + 版本位 + 随机。
 * 主键带时间前缀，索引友好；时间戳也让"两台设备各自生成"不会撞。
 */
export function uuidv7(): string {
  const ms = Date.now();
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  // 前 6 字节 = 时间戳（大端）
  b[0] = (ms / 2 ** 40) & 0xff;
  b[1] = (ms / 2 ** 32) & 0xff;
  b[2] = (ms / 2 ** 24) & 0xff;
  b[3] = (ms / 2 ** 16) & 0xff;
  b[4] = (ms / 2 ** 8) & 0xff;
  b[5] = ms & 0xff;
  b[6] = (b[6] & 0x0f) | 0x70; // version 7
  b[8] = (b[8] & 0x3f) | 0x80; // variant
  const hex = [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** 常量时间比较，别用 === 比哈希 */
export function safeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

export const b64urlEncode = (s: string): string =>
  btoa(String.fromCharCode(...new TextEncoder().encode(s)))
    .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

export const b64urlDecode = (s: string): string => {
  const pad = s.replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(pad + '='.repeat((4 - (pad.length % 4)) % 4));
  return new TextDecoder().decode(Uint8Array.from(raw, (c) => c.charCodeAt(0)));
};

/**
 * 一次 changelog 追加，所有写路由都用它，保证游标不漏。
 *
 * classId 是必填的：changelog 不按班隔离的话，A 班的设备会读到 B 班的变更记录，
 * 进而把 B 班的行 id 当成"我这边该拉的东西"。这类漏洞在单班试点时完全看不出来，
 * 一到两个班主任共用一套部署就露馅 —— 所以这里不给默认值，漏传就编译报错。
 */
export function changeStmt(
  db: D1Database,
  classId: string,
  entity: string,
  rowId: string,
  op: 'upsert' | 'delete',
  at = Date.now(),
): D1PreparedStatement {
  return db
    .prepare('INSERT INTO changelog (class_id, entity, row_id, op, at) VALUES (?, ?, ?, ?, ?)')
    .bind(classId, entity, rowId, op, at);
}

export function chunk<T>(items: T[], size: number): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size));
  return out;
}
