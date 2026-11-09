/**
 * 鉴权：两种端，两种策略。
 * - 电脑端免密，靠 refresh token（90 天）静默续期。token 由客户端用 DPAPI 加密后存盘，
 *   换 Windows 用户就解不开 —— 这一层服务端不用管。
 * - 大屏端只在进管理模式时用密码，班委刷卡记分不碰密码。
 *
 * 令牌是 HS256 的 JWT 简化版（header.payload.signature），不引库：
 * 只有本服务自己签、自己验，不需要 RS256，也不需要 JWKS。
 */
import type { Claims, Env } from './types';
import { HttpError, b64urlDecode, b64urlEncode, now, randomHex, safeEqual } from './util';

const HEADER = b64urlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));

async function hmacKey(secret: string): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign', 'verify'],
  );
}

async function sign(data: string, secret: string): Promise<string> {
  const sig = await crypto.subtle.sign('HMAC', await hmacKey(secret), new TextEncoder().encode(data));
  return b64urlEncode(String.fromCharCode(...new Uint8Array(sig)));
}

export async function signToken(env: Env, claims: Omit<Claims, 'iat' | 'exp'>, ttlSec: number): Promise<string> {
  const iat = Math.floor(now() / 1000);
  const body = b64urlEncode(JSON.stringify({ ...claims, iat, exp: iat + ttlSec }));
  return `${HEADER}.${body}.${await sign(`${HEADER}.${body}`, env.TOKEN_SECRET)}`;
}

export async function verifyToken(env: Env, token: string): Promise<Claims> {
  const parts = token.split('.');
  if (parts.length !== 3) throw new HttpError(401, 'bad_token', '令牌格式不对');
  const [h, p, s] = parts;
  const expected = await sign(`${h}.${p}`, env.TOKEN_SECRET);
  if (!safeEqual(s, expected)) throw new HttpError(401, 'bad_signature', '令牌签名不对');

  let claims: Claims;
  try {
    claims = JSON.parse(b64urlDecode(p)) as Claims;
  } catch {
    throw new HttpError(401, 'bad_token', '令牌内容不是合法 JSON');
  }
  if (claims.exp * 1000 < now()) throw new HttpError(401, 'token_expired', '令牌已过期，请刷新');
  return claims;
}

export function bearer(request: Request): string {
  const raw = request.headers.get('authorization') ?? '';
  const m = /^Bearer\s+(.+)$/i.exec(raw);
  if (!m) throw new HttpError(401, 'no_token', '缺少 Authorization: Bearer');
  return m[1].trim();
}

export async function requireAuth(env: Env, request: Request): Promise<Claims> {
  return verifyToken(env, bearer(request));
}

// ---------------- 口令哈希 ----------------

const enc = new TextEncoder();

export async function pbkdf2Hex(password: string, saltHex: string, iterations: number): Promise<string> {
  const key = await crypto.subtle.importKey('raw', enc.encode(password), 'PBKDF2', false, ['deriveBits']);
  const salt = Uint8Array.from(saltHex.match(/.{2}/g)!.map((x) => parseInt(x, 16)));
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', hash: 'SHA-256', salt, iterations },
    key,
    256,
  );
  return [...new Uint8Array(bits)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export async function hashPassword(env: Env, password: string): Promise<{ hash: string; salt: string }> {
  const salt = randomHex(16);
  const iterations = Number(env.PBKDF2_ITERATIONS || '100000');
  return { hash: await pbkdf2Hex(password, salt, iterations), salt };
}

export async function verifyPassword(env: Env, password: string, hash: string, salt: string): Promise<boolean> {
  const iterations = Number(env.PBKDF2_ITERATIONS || '100000');
  const got = await pbkdf2Hex(password, salt, iterations);
  return safeEqual(got, hash);
}

export function sha256Hex(input: string): Promise<string> {
  return crypto.subtle.digest('SHA-256', enc.encode(input)).then((buf) =>
    [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join(''),
  );
}

export const accessTtlSec = (env: Env): number => Number(env.TOKEN_TTL_HOURS || '12') * 3600;
export const refreshTtlSec = (env: Env): number => Number(env.REFRESH_TTL_DAYS || '90') * 86400;
