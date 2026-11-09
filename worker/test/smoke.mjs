/**
 * 冒烟测试：把十个接口按真实使用顺序跑一遍。
 * 运行前先起服务：npx wrangler dev --port 8787 --local
 * 然后：node test/smoke.mjs
 *
 * 这不是单元测试，是"接口契约还算不算数"的回归：
 * 客户端 C# 那边的 payload 形状一改，这里就该红。
 */
import { randomUUID } from 'node:crypto';

const BASE = process.env.BASE_URL ?? 'http://127.0.0.1:8787/api/v1';

let pass = 0;
const failures = [];

function check(name, ok, detail = '') {
  if (ok) {
    pass++;
    console.log(`  ✓ ${name}`);
  } else {
    failures.push(`${name}${detail ? ' — ' + detail : ''}`);
    console.log(`  ✗ ${name}${detail ? ' — ' + detail : ''}`);
  }
}

async function call(method, path, { body, token, headers = {} } = {}) {
  const res = await fetch(BASE + path, {
    method,
    headers: {
      ...(body ? { 'content-type': 'application/json' } : {}),
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...headers,
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }
  return { status: res.status, data };
}

const now = Date.now();
const uuid = () => randomUUID();

console.log('\n1) 建班 + 配对');
const created = await call('POST', '/classes', {
  body: { name: '高二 (3) 班', ownerName: '张老师', ownerPin: '123456', recoveryCode: 'REC-8888', kind: 'board' },
});
check('POST /classes 返回 201', created.status === 201, `status=${created.status} ${JSON.stringify(created.data)}`);
const code = created.data?.code;
let access = created.data?.access;
let refresh = created.data?.refresh;
check('拿到班级码', typeof code === 'string' && code.length === 8, code);
check('拿到 access/refresh', !!access && !!refresh);

const paired = await call('POST', '/pair', { body: { code, secret: '123456', kind: 'desk', deviceLabel: '办公室' } });
check('POST /pair 成功', paired.status === 200, `status=${paired.status}`);
check('配对照旧拿到 token', !!paired.data?.access);

const badPair = await call('POST', '/pair', { body: { code, secret: '000000', kind: 'desk' } });
check('密码错了返回 401', badPair.status === 401, `status=${badPair.status}`);

const recPair = await call('POST', '/pair', { body: { code, secret: 'REC-8888', kind: 'desk' } });
check('恢复码也能配对', recPair.status === 200, `status=${recPair.status}`);

const refreshed = await call('POST', '/refresh', { body: { refresh } });
check('POST /refresh 续期成功', refreshed.status === 200 && !!refreshed.data?.access);
check('refresh token 换了一个（rotate）', refreshed.data?.refresh !== refresh);
const staleRefresh = await call('POST', '/refresh', { body: { refresh } });
check('旧 refresh 立刻失效', staleRefresh.status === 401, `status=${staleRefresh.status}`);
access = refreshed.data?.access;

console.log('\n2) 先灌名单和周期（同步的前置条件）');
const periodId = uuid();
const p1 = await call('POST', '/periods', {
  token: access,
  body: { period: { id: periodId, name: '2026 秋季学期', termKey: '2026-AUT', startAt: now, status: 'active', createdAt: now } },
});
check('POST /periods 建周期', p1.status === 200, `status=${p1.status} ${JSON.stringify(p1.data)}`);

const studentIds = [];
for (const [i, name] of ['杨欣怡', '陈芷若', '刘书豪'].entries()) {
  const id = uuid();
  studentIds.push(id);
  const r = await call('POST', '/students', {
    token: access,
    body: { action: 'upsert', student: { id, name, seatNo: i + 1, active: true, rev: 1, createdAt: now, updatedAt: now } },
  });
  check(`POST /students 第 ${i + 1} 个`, r.status === 200 && r.data?.applied === true, JSON.stringify(r.data));
}

console.log('\n3) 批量入账 + 幂等');
const batchId = uuid();
const entries = studentIds.map((sid) => ({
  id: uuid(),
  batchId,
  periodId,
  studentId: sid,
  ruleId: 'r1',
  ruleLabel: '回答问题',
  ruleDeltaDefault: 5,
  delta: 5,
  source: 'rule',
  operatorId: 'm1',
  operatorName: '张老师',
  identifyMethod: 'nfc',
  occurredAt: now,
  createdAt: now,
}));
const posted = await call('POST', '/entries', { token: access, body: { entries } });
check('POST /entries 整批入库', posted.status === 200 && posted.data?.inserted === 3, JSON.stringify(posted.data));

const replayed = await call('POST', '/entries', { token: access, body: { entries } });
check('同一批重传不重复计分', replayed.data?.inserted === 0 && replayed.data?.duplicates === 3, JSON.stringify(replayed.data));

const tooMany = await call('POST', '/entries', {
  token: access,
  body: { entries: Array.from({ length: 51 }, () => ({ ...entries[0], id: uuid() })) },
});
check('超过 50 条被拒绝', tooMany.status === 422, `status=${tooMany.status}`);

const noAuth = await call('POST', '/entries', { body: { entries } });
check('没令牌写入被拒', noAuth.status === 401, `status=${noAuth.status}`);

console.log('\n4) 拉取');
const pulled = await call('GET', '/sync?since=0&limit=200', { token: access });
check('GET /sync 有游标', typeof pulled.data?.next === 'number' && pulled.data.next > 0, `next=${pulled.data?.next}`);
// 本地 D1 是持久的，多次跑会累积；只数本次造出来的那几条
const mineStudents = (pulled.data?.students ?? []).filter((s) => studentIds.includes(s.id)).length;
// /sync 返回的是库里的原始列名（snake_case），和本地 SQLite 列名一一对应
const mineEntries = (pulled.data?.entries ?? []).filter((e) => e.batch_id === batchId).length;
check('拉回本次的 3 个学生', mineStudents === 3, `got=${mineStudents}`);
check('拉回本次的 3 条流水', mineEntries === 3, `got=${mineEntries}`);
check('拉回本次的周期', (pulled.data?.periods ?? []).some((p) => p.id === periodId));

const again = await call('GET', `/sync?since=${pulled.data.next}`, { token: access });
check('用 next 再拉一次是空的', (again.data?.entries?.length ?? 1) === 0 && again.data?.hasMore === false);

console.log('\n5) 撤销');
const rev = await call('POST', `/entries/batch/${batchId}/reverse`, {
  token: access,
  body: { operatorId: 'm1', operatorName: '张老师' },
});
check('按批次撤销 3 条', rev.status === 200 && rev.data?.reversed === 3, JSON.stringify(rev.data));
const revAgain = await call('POST', `/entries/batch/${batchId}/reverse`, {
  token: access,
  body: { operatorId: 'm1', operatorName: '张老师' },
});
check('同一批不能再撤一次', revAgain.status === 404, `status=${revAgain.status}`);

const afterRev = await call('GET', `/sync?since=${pulled.data.next}`, { token: access });
// 只数本批：本地 D1 会累积多次跑的数据，全局求和没有意义
const sum = (afterRev.data?.entries ?? []).filter((e) => e.batch_id === batchId).reduce((a, e) => a + e.delta, 0);
check('冲销行把总分抵回 0', sum === 0, `sum=${sum}`);

console.log('\n6) 乐观锁');
const stuId = studentIds[0];
const cur = await call('GET', `/sync?since=0&limit=500`, { token: access });
const mineStudent = (cur.data?.students ?? []).find((s) => s.id === stuId) ?? (pulled.data?.students ?? []).find((s) => s.id === stuId);
const serverRev = mineStudent?.rev ?? 1;
const okPatch = await call('PATCH', `/students/${stuId}`, {
  token: access,
  headers: { 'if-match': String(serverRev) },
  body: { name: '杨欣怡', rev: serverRev + 1, updatedAt: now },
});
check('If-Match 对得上就放行', okPatch.status === 200, `status=${okPatch.status}`);
const stale = await call('PATCH', `/students/${stuId}`, {
  token: access,
  headers: { 'if-match': String(serverRev) },
  body: { name: '杨筱铭', rev: serverRev + 1, updatedAt: now },
});
check('If-Match 过期返回 409', stale.status === 409, `status=${stale.status}`);

const staleUpsert = await call('POST', '/students', {
  token: access,
  body: { action: 'upsert', student: { id: stuId, name: '旧名字', rev: 1, updatedAt: now } },
});
check('离线补推的旧 rev 被静默丢弃', staleUpsert.status === 200 && staleUpsert.data?.applied === false, JSON.stringify(staleUpsert.data));

console.log('\n7) 考试与成绩');
const examId = uuid();
const examUp = await call('POST', '/exams', {
  token: access,
  body: {
    action: 'upsert',
    exam: { id: examId, periodId, name: '期中考试', fullScore: 750, examDate: '2026-11-01', importedAt: now },
    scores: [
      { studentId: studentIds[0], score: 712, absent: false },
      { studentId: studentIds[1], score: 688, absent: false },
      { studentId: studentIds[2], score: null, absent: true },
    ],
  },
});
check('POST /exams 整批落库', examUp.status === 200 && examUp.data?.scores === 3, JSON.stringify(examUp.data));

const examPull = await call('GET', `/sync?since=${afterRev.data.next}`, { token: access });
check('拉回本次的考试', (examPull.data?.exams ?? []).some((e) => e.id === examId));
const mineScores = (examPull.data?.exam_scores ?? []).filter((s) => s.exam_id === examId).length;
check('拉回本次的 3 条成绩', mineScores === 3, `got=${mineScores}`);

console.log('\n8) 重置（两阶段）');
const noToken = await call('POST', '/periods/reset', { token: access, body: { phase: 'commit', period: { id: uuid(), name: '11 月', termKey: '2026-AUT' } } });
check('没令牌提交被拒', noToken.status >= 400 && noToken.status < 500, `status=${noToken.status}`);

const req = await call('POST', '/periods/reset', {
  token: access,
  body: { phase: 'request', period: { name: '11 月', termKey: '2026-AUT' } },
});
check('申请到重置令牌', req.status === 200 && typeof req.data?.token === 'string');

const blocked = await call('POST', '/entries', { token: access, body: { entries: [{ ...entries[0], id: uuid() }] } });
check('重置期间普通写入被拦 (423)', blocked.status === 423, `status=${blocked.status}`);

const newPeriodId = uuid();
const committed = await call('POST', '/periods/reset', {
  token: access,
  body: { phase: 'commit', token: req.data.token, period: { id: newPeriodId, name: '11 月', termKey: '2026-AUT', startAt: now } },
});
check('提交成功', committed.status === 200 && committed.data?.periodId === newPeriodId, JSON.stringify(committed.data));

const writeAfter = await call('POST', '/entries', { token: access, body: { entries: [{ ...entries[0], id: uuid(), periodId: newPeriodId }] } });
check('提交后写入恢复', writeAfter.status === 200 && writeAfter.data?.inserted === 1, JSON.stringify(writeAfter.data));

const periodPull = await call('GET', '/sync?since=0&limit=500', { token: access });
const activeCount = (periodPull.data?.periods ?? []).filter((p) => p.status === 'active').length;
check('同时只有一个 active 周期', activeCount === 1, `active=${activeCount}`);

console.log('\n9) 多班隔离（同一套 D1 服务两个班，谁也不许看见谁）');
const clsB = await call('POST', '/classes', {
  body: { name: '高三 (1) 班', ownerName: '李老师', ownerPin: '654321', kind: 'desk' },
});
check('第二个班也能建（班级码不撞）', clsB.status === 201 && clsB.data?.code !== code, `status=${clsB.status}`);
const accessB = clsB.data?.access;

// A 班这条学号 '01' 是班内唯一的；B 班再用 '01' 必须也能成功（唯一索引按班隔离）
const aNo01 = await call('POST', '/students', {
  token: access,
  body: { action: 'upsert', student: { id: uuid(), name: '学号一班', studentNo: '01', active: true, rev: 1, createdAt: now, updatedAt: now } },
});
check('A 班先占住学号 01', aNo01.status === 200 && aNo01.data?.applied === true, JSON.stringify(aNo01.data));
const bNo01 = await call('POST', '/students', {
  token: accessB,
  body: { action: 'upsert', student: { id: uuid(), name: '学号二班', studentNo: '01', active: true, rev: 1, createdAt: now, updatedAt: now } },
});
check('B 班用同一个学号 01 不被拦', bNo01.status === 200 && bNo01.data?.applied === true, JSON.stringify(bNo01.data));

const pullB = await call('GET', '/sync?since=0&limit=500', { token: accessB });
check('B 班拉不到 A 班的学生', (pullB.data?.students ?? []).every((s) => !studentIds.includes(s.id)), `got=${(pullB.data?.students ?? []).length} 行`);
check('B 班拉不到 A 班的流水', (pullB.data?.entries ?? []).every((e) => e.batch_id !== batchId), `got=${(pullB.data?.entries ?? []).length} 行`);
check('B 班拉不到 A 班的周期', (pullB.data?.periods ?? []).every((p) => p.id !== periodId && p.id !== newPeriodId));
check('B 班拉不到 A 班的考试', (pullB.data?.exams ?? []).every((e) => e.id !== examId));
check('B 班只看到自己的那一个成员', (pullB.data?.members ?? []).length === 1, `got=${(pullB.data?.members ?? []).length}`);

// 拿到 A 班某个真实 id 也不能改/删别人的行
const crossDelete = await call('POST', '/students', {
  token: accessB,
  body: { action: 'delete', student: { id: studentIds[1] } },
});
check('B 班拿 A 班 id 删除不报错但也不生效', crossDelete.status === 200);
const crossHijack = await call('POST', '/students', {
  token: accessB,
  body: { action: 'upsert', student: { id: studentIds[1], name: '被串改的名字', rev: 99, createdAt: now, updatedAt: now } },
});
check('B 班抢同一个主键 id 被 guard 挡下', crossHijack.status === 200 && crossHijack.data?.applied === false, JSON.stringify(crossHijack.data));

const checkA = await call('GET', '/sync?since=0&limit=500', { token: access });
const victim = (checkA.data?.students ?? []).find((s) => s.id === studentIds[1]);
check('A 班那个学生没被删（deleted_at 仍为空）', !!victim && victim.deleted_at === null, JSON.stringify(victim));
check('A 班那个学生名字没被改掉', victim?.name !== '被串改的名字', victim?.name);

const crossReverse = await call('POST', `/entries/batch/${batchId}/reverse`, {
  token: accessB,
  body: { operatorId: 'm2', operatorName: '李老师' },
});
check('B 班撤不到 A 班的批次（404）', crossReverse.status === 404, `status=${crossReverse.status}`);

// 折算方案权重同样要按班校验
const schemeA = uuid();
const schemeUp = await call('POST', '/schemes', {
  token: access,
  body: { action: 'upsert', scheme: { id: schemeA, name: '默认 7:3', examWeight: 0.7, dailyWeight: 0.3, isActive: true, createdAt: now, rev: 1 } },
});
check('A 班建折算方案', schemeUp.status === 200 && schemeUp.data?.applied === true, JSON.stringify(schemeUp.data));
const crossWeights = await call('POST', `/schemes/${schemeA}/weights`, {
  token: accessB,
  body: { weights: [{ examId, weight: 1 }] },
});
check('B 班改不到 A 班方案的权重（404）', crossWeights.status === 404, `status=${crossWeights.status}`);

const periodB = uuid();
const pB = await call('POST', '/periods', {
  token: accessB,
  body: { period: { id: periodB, name: '2026 秋季学期', termKey: '2026-AUT', startAt: now, status: 'active', createdAt: now } },
});
check('B 班能建自己的 active 周期', pB.status === 200, `status=${pB.status} ${JSON.stringify(pB.data)}`);
const stillActiveA = await call('GET', '/sync?since=0&limit=500', { token: access });
const activeA = (stillActiveA.data?.periods ?? []).filter((p) => p.status === 'active');
check('A 班的 active 周期不受 B 班影响', activeA.length === 1 && activeA[0].id === newPeriodId, JSON.stringify(activeA.map((p) => p.id)));

console.log('\n10) 收尾');
const pong = await call('GET', '/ping', { token: access });
check('GET /ping 通', pong.status === 200 && pong.data?.ok === true);

const out = await call('POST', '/signout', { token: access });
check('POST /signout 撤销设备', out.status === 200);

const afterOut = await call('GET', '/ping', { token: access });
check('撤销后 access 仍可解析（到期自然失效），refresh 已废', afterOut.status === 200);

console.log(`\n${failures.length === 0 ? '✅' : '❌'} 通过 ${pass} 项，失败 ${failures.length} 项`);
if (failures.length) {
  for (const f of failures) console.log('   - ' + f);
  process.exit(1);
}
