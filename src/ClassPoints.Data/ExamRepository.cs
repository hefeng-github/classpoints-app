using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

/// <summary>一场考试的概况（成绩页列表用，不加载全部分数）。</summary>
public sealed record ExamSummary(Exam Exam, int PresentCount, int AbsentCount, int MissingCount)
{
    public string DateText => string.IsNullOrWhiteSpace(Exam.ExamDate) ? "—" : Exam.ExamDate!;

    public string FullScoreText => Exam.FullScore.ToString("0.#");

    public string ImportedText => Clock.ToLocal(Exam.ImportedAt).ToString("yyyy-MM-dd HH:mm");
}

/// <summary>成绩导入的落库结果。</summary>
public sealed record ExamImportResult(
    string ExamId,
    int Written,
    int Absent,
    IReadOnlyList<string> Unmatched,
    IReadOnlyList<string> Ignored);

public sealed class ExamRepository(Database db)
{
    // ---------------- 考试 ----------------

    public List<Exam> ListExams(string periodId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM exams WHERE period_id = $p ORDER BY COALESCE(exam_date,'') DESC, imported_at DESC";
        cmd.Parameters.AddWithValue("$p", periodId);
        using var r = cmd.ExecuteReader();
        var list = new List<Exam>();
        while (r.Read()) list.Add(Mappers.Exam(r));
        return list;
    }

    public Exam? GetExam(string examId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM exams WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", examId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Exam(r) : null;
    }

    /// <summary>成绩页那四行概况：参考、缺考、未录入。</summary>
    public List<ExamSummary> ListSummaries(string periodId, int studentCount)
    {
        var exams = ListExams(periodId);
        var result = new List<ExamSummary>(exams.Count);
        using var conn = db.Open();
        foreach (var ex in exams)
        {
            var present = Convert.ToInt32(Database.Scalar(conn,
                "SELECT COUNT(*) FROM exam_scores WHERE exam_id = $e AND absent = 0", ("$e", ex.Id)));
            var absent = Convert.ToInt32(Database.Scalar(conn,
                "SELECT COUNT(*) FROM exam_scores WHERE exam_id = $e AND absent = 1", ("$e", ex.Id)));
            var missing = System.Math.Max(0, studentCount - present - absent);
            result.Add(new ExamSummary(ex, present, absent, missing));
        }
        return result;
    }

    public List<ExamScore> Scores(string examId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM exam_scores WHERE exam_id = $e";
        cmd.Parameters.AddWithValue("$e", examId);
        using var r = cmd.ExecuteReader();
        var list = new List<ExamScore>();
        while (r.Read())
            list.Add(new ExamScore
            {
                ExamId = r.GetStr("exam_id"),
                StudentId = r.GetStr("student_id"),
                Score = r.GetDoubleOrNull("score"),
                Absent = r.GetBool("absent"),
            });
        return list;
    }

    /// <summary>整学期（同一 term_key 下所有周期）的分数，排名计算用。</summary>
    public List<ExamScore> ScoresForTerm(string termKey, IReadOnlyList<string> examIds)
    {
        if (examIds.Count == 0) return new List<ExamScore>();
        var placeholders = string.Join(",", examIds.Select((_, i) => $"$e{i}"));
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT sc.* FROM exam_scores sc
            JOIN exams e ON e.id = sc.exam_id
            WHERE e.id IN ({placeholders})
            """;
        for (var i = 0; i < examIds.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", examIds[i]);
        using var r = cmd.ExecuteReader();
        var list = new List<ExamScore>();
        while (r.Read())
            list.Add(new ExamScore
            {
                ExamId = r.GetStr("exam_id"),
                StudentId = r.GetStr("student_id"),
                Score = r.GetDoubleOrNull("score"),
                Absent = r.GetBool("absent"),
            });
        return list;
    }

    /// <summary>
    /// 导入一场考试。重名导入同一场考试时按 id 覆盖（同一场考试重导是修数据，不是新增）。
    /// 已存在该 examId 时先清空分数再写，避免上一版多出来的行残留。
    /// </summary>
    public ExamImportResult ImportScores(string periodId, string examName, double fullScore, string? examDate,
        IEnumerable<(string StudentId, double? Score, bool Absent)> rows, string? sourceFile)
    {
        var now = Clock.NowMs();
        var matched = rows.ToList();
        var exam = new Exam
        {
            Id = Ids.NewUuid7(),
            PeriodId = periodId,
            Name = examName,
            FullScore = fullScore <= 0 ? 750 : fullScore,
            ExamDate = examDate,
            ImportedAt = now,
            SourceFile = sourceFile,
        };

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        // 同名同周期同日期视为同一场：覆盖，而不是并排出现两行「期中考试」
        var existing = Database.ScalarTx(conn, tx,
            "SELECT id FROM exams WHERE period_id = $p AND name = $n LIMIT 1",
            ("$p", periodId), ("$n", examName));
        if (existing is string existId)
        {
            exam.Id = existId;
            Database.Exec(conn, tx, "DELETE FROM exam_scores WHERE exam_id = $e", ("$e", exam.Id));
            Database.Exec(conn, tx,
                "UPDATE exams SET full_score = $f, exam_date = $d, imported_at = $at, source_file = $src WHERE id = $id",
                ("$id", exam.Id), ("$f", exam.FullScore), ("$d", examDate), ("$at", now), ("$src", sourceFile));
        }
        else
        {
            Database.Exec(conn, tx, """
                INSERT INTO exams (id, period_id, name, full_score, exam_date, imported_at, source_file)
                VALUES ($id, $p, $n, $f, $d, $at, $src)
                """,
                ("$id", exam.Id), ("$p", periodId), ("$n", examName), ("$f", exam.FullScore),
                ("$d", examDate), ("$at", now), ("$src", sourceFile));
        }

        var written = 0;
        var absent = 0;
        foreach (var row in matched)
        {
            Database.Exec(conn, tx,
                "INSERT OR REPLACE INTO exam_scores (exam_id, student_id, score, absent) VALUES ($e, $s, $sc, $a)",
                ("$e", exam.Id), ("$s", row.StudentId),
                ("$sc", row.Absent ? null : row.Score), ("$a", row.Absent ? 1 : 0));
            if (row.Absent) absent++; else written++;
        }

        // 一场考试 + 全部成绩一起入队：云端 /exams 是整批一个事务，
        // 逐条推 48 个分数跨境要等十几秒，整批只要一次往返。
        var scores = matched
            .Select(r => new { studentId = r.StudentId, score = r.Absent ? (double?)null : r.Score, absent = r.Absent })
            .ToList();
        SyncOutbox.Enqueue(conn, tx, $"exams:{exam.Id}", "exam", new { action = "upsert", exam, scores });
        tx.Commit();

        return new ExamImportResult(exam.Id, written, absent, Array.Empty<string>(), Array.Empty<string>());
    }

    public void UpdateFullScore(string examId, double fullScore)
    {
        PushExam(examId, conn =>
        {
            Database.Exec(conn, "UPDATE exams SET full_score = $f WHERE id = $id",
                ("$id", examId), ("$f", fullScore <= 0 ? 750 : fullScore));
        });
    }

    public void Rename(string examId, string name)
    {
        PushExam(examId, conn =>
        {
            Database.Exec(conn, "UPDATE exams SET name = $n WHERE id = $id", ("$id", examId), ("$n", name));
        });
    }

    /// <summary>
    /// 改名、改满分这类小改动也要入队 —— 只改本地不入队，另一端就永远看不到。
    /// 这里只推考试本身，不带 scores：云端按 id upsert，没给的分数保持原样。
    /// </summary>
    private void PushExam(string examId, Action<SqliteConnection> mutate)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        mutate(conn);
        var exam = ReadExam(conn, tx, examId);
        if (exam is not null)
            SyncOutbox.Enqueue(conn, tx, $"exams:{examId}", "exam", new { action = "upsert", exam });
        tx.Commit();
    }

    private static Exam? ReadExam(SqliteConnection conn, SqliteTransaction tx, string examId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM exams WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", examId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Exam(r) : null;
    }

    public void Delete(string examId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, "DELETE FROM exam_scores WHERE exam_id = $e", ("$e", examId));
        Database.Exec(conn, tx, "DELETE FROM scheme_weights WHERE exam_id = $e", ("$e", examId));
        Database.Exec(conn, tx, "DELETE FROM exams WHERE id = $e", ("$e", examId));
        SyncOutbox.Enqueue(conn, tx, $"exams:{examId}", "exam", new { action = "delete", id = examId });
        tx.Commit();
    }

    // ---------------- 方案与权重 ----------------

    public Scheme ActiveScheme()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM schemes WHERE is_active = 1 LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (r.Read()) return Mappers.Scheme(r);

        var fallback = new Scheme { Id = "sch_default", Name = "默认方案" };
        return fallback;
    }

    public void SaveScheme(Scheme s)
    {
        var examWeight = System.Math.Clamp(s.ExamWeight, 0, 1);
        var dailyWeight = 1 - examWeight;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT INTO schemes (id, name, exam_weight, daily_weight, normalize, is_active, created_at)
            VALUES ($id, $name, $ew, $dw, $norm, 0, $now)
            ON CONFLICT(id) DO UPDATE SET name = $name, exam_weight = $ew, daily_weight = $dw, normalize = $norm
            """,
            ("$id", s.Id), ("$name", s.Name), ("$ew", examWeight), ("$dw", dailyWeight),
            ("$norm", s.Normalize ? 1 : 0), ("$now", Clock.NowMs()));
        Database.Exec(conn, tx, "UPDATE schemes SET is_active = 0");
        Database.Exec(conn, tx, "UPDATE schemes SET is_active = 1 WHERE id = $id", ("$id", s.Id));
        SyncOutbox.Enqueue(conn, tx, $"schemes:{s.Id}", "scheme", new { action = "upsert", scheme = s });
        tx.Commit();

        s.ExamWeight = examWeight;
        s.DailyWeight = dailyWeight;
        s.IsActive = true;
    }

    public Dictionary<string, double> Weights(string schemeId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT exam_id, weight FROM scheme_weights WHERE scheme_id = $s";
        cmd.Parameters.AddWithValue("$s", schemeId);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, double>();
        while (r.Read()) map[r.GetString(0)] = r.GetDouble(1);
        return map;
    }

    public void SetWeights(string schemeId, IReadOnlyDictionary<string, double> weights)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (examId, w) in weights)
        {
            if (w <= 0)
            {
                Database.Exec(conn, tx, "DELETE FROM scheme_weights WHERE scheme_id = $s AND exam_id = $e",
                    ("$s", schemeId), ("$e", examId));
                continue;
            }
            Database.Exec(conn, tx, """
                INSERT INTO scheme_weights (scheme_id, exam_id, weight) VALUES ($s, $e, $w)
                ON CONFLICT(scheme_id, exam_id) DO UPDATE SET weight = excluded.weight
                """, ("$s", schemeId), ("$e", examId), ("$w", w));
        }
        SyncOutbox.Enqueue(conn, tx, $"schemes:{schemeId}", "scheme", new { action = "weights", schemeId, weights });
        tx.Commit();
    }

    public static AbsencePolicy ParsePolicy(string? raw) => raw switch
    {
        "zero" => AbsencePolicy.Zero,
        "average" => AbsencePolicy.ClassAverage,
        _ => AbsencePolicy.Redistribute,
    };

    public static string PolicyToDb(AbsencePolicy p) => p switch
    {
        AbsencePolicy.Zero => "zero",
        AbsencePolicy.ClassAverage => "average",
        _ => "redistribute",
    };

    /// <summary>按整学期算的排名（总评用学期累计积分，不用当前周期）。</summary>
    public RankingResult BuildRanking(
        IReadOnlyList<Student> students,
        IReadOnlyDictionary<string, int> termPoints,
        string termKey,
        Scheme scheme,
        AbsencePolicy policy)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.* FROM exams e JOIN periods p ON p.id = e.period_id
            WHERE p.term_key = $term ORDER BY COALESCE(e.exam_date,'') , e.imported_at
            """;
        cmd.Parameters.AddWithValue("$term", termKey);
        using var r = cmd.ExecuteReader();
        var exams = new List<Exam>();
        while (r.Read()) exams.Add(Mappers.Exam(r));

        var scores = ScoresForTerm(termKey, exams.Select(e => e.Id).ToList());
        var weights = Weights(scheme.Id);
        return RankingCalculator.Compute(students, termPoints, exams, scores, weights, scheme, policy);
    }
}
