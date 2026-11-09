/**
 * 服务端只读两种东西：环境绑定和请求。
 * 这里把两者写清楚，后面每个路由都靠它。
 */
export interface Env {
  DB: D1Database;
  TOKEN_SECRET: string;
  TOKEN_TTL_HOURS: string;
  REFRESH_TTL_DAYS: string;
  PBKDF2_ITERATIONS: string;
}

export interface Claims {
  /** 设备 id */
  sid: string;
  /** 班级 id */
  cid: string;
  /** board = 大屏（有班委卡），desk = 电脑端（免密） */
  kind: 'board' | 'desk';
  role: 'owner' | 'co_owner' | 'officer';
  iat: number;
  exp: number;
}
