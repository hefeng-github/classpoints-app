/**
 * 考试：一场考试 + 全部成绩，整批一个事务。
 * 单开一个端点而不是走 /:entity，理由是它是成批的事实数据：
 * 1 条 exam + 48 条 score 用通用端点就是 49 次往返，跨境每次 100–300ms，
 * 导一次成绩要等十五秒。整批一次往返，失败整场回滚 —— 成绩导入要的就是这个原子性。
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

interface ExamDto {
  id?: string;
  periodId?: string;
  name?: string;
  fullScore?: number;
  examDate?: string | null;
  importedAt?: number;
  sourceFile?: string | null;
}

interface ScoreDto {
  studentId?: string;
  score?: number | null;
  absent?: boolean;
}

export async function upsertExam(env: Env, request: Request): Promise<Response> {
  const claims = await requireAuth(env, request);
  const body = await readJson<{ action?: string; exam?: ExamDto; id?: string; scores?: ScoreDto[] }>(request);
  const action = body.action ?? 'upsert';

  if (action === 'delete') {
    const id = requireString(body.exam?.id ?? body.id, 'id', 40);
    const at = now();
    await env.DB.batch([
      env.DB.prepare('UPDATE exams SET deleted_at = ? WHERE id = ? AND class_id = ?').bind(at, id, claims.cid),
      env.DB.prepare('DELETE FROM exam_scores WHERE exam_id = ? AND class_id = ?').bind(id, claims.cid),
      changeStmt(env.DB, claims.cid, 'exams', id, 'delete', at),
    ]);
    return json({ ok: true, id, action });
  }

  const exam = body.exam;
  if (!exam) throw new HttpError(422, 'no_payload', '请求体里找不到 exam 对象');
  const id = requireString(exam.id, 'id', 40);
  const at = now();
  const fullScore = typeof exam.fullScore === 'number' && exam.fullScore > 0 ? exam.fullScore : 750;

  const stmts: D1PreparedStatement[] = [
    env.DB.prepare(
      `INSERT INTO exams (id, class_id, period_id, name, full_score, exam_date, imported_at, source_file, deleted_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, NULL)
       ON CONFLICT(id) DO UPDATE SET
         name = excluded.name, full_score = excluded.full_score, exam_date = excluded.exam_date,
         imported_at = excluded.imported_at, source_file = excluded.source_file, deleted_at = NULL
       WHERE exams.class_id = excluded.class_id`,
    ).bind(
      id,
      claims.cid,
      requireString(exam.periodId, 'periodId', 40),
      requireString(exam.name, 'name', 60),
      fullScore,
      optionalString(exam.examDate, 20),
      intOrNull(exam.importedAt) ?? at,
      optionalString(exam.sourceFile, 260),
    ),
    changeStmt(env.DB, claims.cid, 'exams', id, 'upsert', at),
  ];

  const scores = body.scores;
  if (Array.isArray(scores)) {
    // 重导同一场是修数据：先清空再写，避免上一版多出来的行残留
    stmts.push(env.DB.prepare('DELETE FROM exam_scores WHERE exam_id = ? AND class_id = ?').bind(id, claims.cid));
    for (const s of scores) {
      const studentId = requireString(s.studentId, 'studentId', 40);
      const absent = !!s.absent;
      const score = typeof s.score === 'number' && Number.isFinite(s.score) ? s.score : null;
      if (!absent && score === null) throw new HttpError(422, 'score_required', `${studentId} 非缺考但没有分数`);
      stmts.push(
        env.DB.prepare('INSERT INTO exam_scores (exam_id, student_id, class_id, score, absent) VALUES (?, ?, ?, ?, ?)')
          .bind(id, studentId, claims.cid, absent ? null : score, bit(absent)),
      );
      stmts.push(changeStmt(env.DB, claims.cid, 'exam_scores', `${id}:${studentId}`, 'upsert', at));
    }
  }

  await env.DB.batch(stmts);
  return json({ ok: true, examId: id, scores: Array.isArray(scores) ? scores.length : null });
}
