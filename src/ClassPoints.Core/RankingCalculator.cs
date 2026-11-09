namespace ClassPoints.Core;

public sealed record RankingRow(
    string StudentId,
    string Name,
    int? SeatNo,
    string? GroupName,
    int Points,
    double ExamRaw,
    double ExamPercent,
    double Total,
    int Rank,
    IReadOnlyList<string> AbsentExams)
{
    public string TotalText => Total.ToString("0.0");
    public string ExamText => ExamRaw.ToString("0.0");
    public string ExamPercentText => ExamPercent.ToString("0.0");
    public string SeatText => SeatNo?.ToString() ?? "—";
}

public sealed record RankingResult(
    IReadOnlyList<RankingRow> Rows,
    int MaxPoints,
    string MaxPointsWho,
    Scheme Scheme,
    AbsencePolicy Policy,
    bool HasExamData)
{
    public string PolicyText => Policy switch
    {
        AbsencePolicy.Zero => "缺考按 0 分计入",
        AbsencePolicy.ClassAverage => "缺考按该场全班均值替代",
        _ => "缺考按权重再分配",
    };
}

public static class SchemeMath
{
    /// <summary>各场考试的最终权重：有配置就用配置，没配置就均分；统一归一到合计 1。</summary>
    public static Dictionary<string, double> BuildExamWeights(
        IReadOnlyList<Exam> exams,
        IReadOnlyDictionary<string, double> configured)
    {
        var result = new Dictionary<string, double>();
        if (exams.Count == 0) return result;

        var sum = exams.Sum(e => configured.TryGetValue(e.Id, out var w) ? w : 0);
        if (sum <= 1e-9)
        {
            var even = 1.0 / exams.Count;
            foreach (var e in exams) result[e.Id] = even;
            return result;
        }

        foreach (var e in exams)
            result[e.Id] = (configured.TryGetValue(e.Id, out var w) ? w : 0) / sum;
        return result;
    }

    /// <summary>「自动配平」：按现有比例缩放到合计 100%。</summary>
    public static Dictionary<string, double> AutoBalance(IReadOnlyDictionary<string, double> weights)
    {
        var result = new Dictionary<string, double>();
        if (weights.Count == 0) return result;
        var sum = weights.Values.Sum();
        if (sum <= 1e-9)
        {
            var even = 1.0 / weights.Count;
            foreach (var k in weights.Keys) result[k] = even;
            return result;
        }
        foreach (var (k, v) in weights) result[k] = v / sum;
        return result;
    }

    /// <summary>
    /// 名义权重 → 实际影响。原值加权下两个量纲不同，权重不等于看起来那个比例，
    /// 这一行必须如实显示给班主任（见方案 7.2）。
    /// </summary>
    public static (double ExamShare, double DailyShare) ActualImpact(
        double examMean, double pointsMean, Scheme scheme)
    {
        if (scheme.Normalize)
        {
            // 归一后两者都是百分制，名义权重就是实际权重
            return (scheme.ExamWeight * 100, scheme.DailyWeight * 100);
        }

        var ex = examMean * scheme.ExamWeight;
        var da = pointsMean * scheme.DailyWeight;
        var total = ex + da;
        if (total <= 1e-9) return (50, 50);
        return (ex / total * 100, da / total * 100);
    }

    public static string ImpactText(double examMean, double pointsMean, Scheme scheme)
    {
        var (ex, da) = ActualImpact(examMean, pointsMean, scheme);
        return $"名义权重 {scheme.ExamPercent} : {scheme.DailyPercent}　→　按全班平均实际影响 {ex:0} : {da:0}" +
               $"（考试均分 {examMean:0}、积分均 {pointsMean:0}）";
    }
}

public static class RankingCalculator
{
    public static RankingResult Compute(
        IReadOnlyList<Student> students,
        IReadOnlyDictionary<string, int> points,
        IReadOnlyList<Exam> exams,
        IReadOnlyList<ExamScore> scores,
        IReadOnlyDictionary<string, double> configuredWeights,
        Scheme scheme,
        AbsencePolicy policy)
    {
        var active = students.Where(s => s.Active && s.DeletedAt is null).ToList();
        var weights = SchemeMath.BuildExamWeights(exams, configuredWeights);
        var scoreMap = scores.ToDictionary(s => (s.ExamId, s.StudentId), s => s);
        var maxPoints = active.Count == 0 ? 1 : System.Math.Max(1, active.Max(s => points.TryGetValue(s.Id, out var p) ? p : 0));
        var maxWho = "";
        if (active.Count > 0)
        {
            var top = active.OrderByDescending(s => points.TryGetValue(s.Id, out var p) ? p : 0).First();
            maxWho = top.Name;
        }

        // 缺考均值替代需要每场的全班均值（只统计有分的）
        var examMeans = new Dictionary<string, double>();
        foreach (var e in exams)
        {
            var vals = scores.Where(s => s.ExamId == e.Id && !s.Absent && s.Score is not null)
                             .Select(s => s.Score!.Value).ToList();
            examMeans[e.Id] = vals.Count == 0 ? 0 : vals.Average();
        }

        var rows = new List<RankingRow>(active.Count);
        foreach (var st in active)
        {
            var pts = points.TryGetValue(st.Id, out var p) ? p : 0;
            double examRaw = 0, examPct = 0;
            var absent = new List<string>();

            // 该生实际参与的场次及其权重
            var usable = new List<(Exam Exam, double Weight, double Score)>();
            foreach (var ex in exams)
            {
                if (!weights.TryGetValue(ex.Id, out var w) || w <= 0) continue;
                scoreMap.TryGetValue((ex.Id, st.Id), out var sc);
                var isAbsent = sc is null || sc.Absent || sc.Score is null;
                if (isAbsent)
                {
                    absent.Add(ex.Name);
                    if (policy == AbsencePolicy.Redistribute) continue;
                    var fill = policy == AbsencePolicy.Zero ? 0 : examMeans[ex.Id];
                    usable.Add((ex, w, fill));
                }
                else
                {
                    usable.Add((ex, w, sc!.Score!.Value));
                }
            }

            var wsum = usable.Sum(u => u.Weight);
            if (wsum > 1e-9)
            {
                foreach (var u in usable)
                {
                    var w = u.Weight / wsum;
                    examRaw += u.Score * w;
                    var full = u.Exam.FullScore > 0 ? u.Exam.FullScore : 750;
                    examPct += u.Score / full * 100 * w;
                }
            }

            var dailyPct = maxPoints > 0 ? pts * 100.0 / maxPoints : 0;
            var total = scheme.Normalize
                ? examPct * scheme.ExamWeight + dailyPct * scheme.DailyWeight
                : examRaw * scheme.ExamWeight + pts * scheme.DailyWeight;

            rows.Add(new RankingRow(st.Id, st.Name, st.SeatNo, st.GroupName, pts,
                System.Math.Round(examRaw, 4), System.Math.Round(examPct, 4),
                System.Math.Round(total, 4), 0, absent));
        }

        // 名次：按显示精度（一位小数）判定并列，避免"表上同分、名次不同"
        var ordered = rows
            .OrderByDescending(r => System.Math.Round(r.Total, 1))
            .ThenBy(r => r.SeatNo ?? int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();

        var ranked = new List<RankingRow>(ordered.Count);
        var lastValue = double.NaN;
        var lastRank = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            var v = System.Math.Round(r.Total, 1);
            if (double.IsNaN(lastValue) || System.Math.Abs(v - lastValue) > 1e-9)
            {
                lastRank = i + 1;
                lastValue = v;
            }
            ranked.Add(r with { Rank = lastRank });
        }

        return new RankingResult(ranked, maxPoints, maxWho, scheme, policy, exams.Count > 0);
    }

    /// <summary>「前 10 名中 2 人位次变化（李明 ↑3，王芳 ↓2）」。</summary>
    public static string DescribeTopChanges(RankingResult before, RankingResult after, int top = 10)
    {
        var oldRank = before.Rows.ToDictionary(r => r.StudentId, r => r.Rank);
        var changed = new List<string>();
        foreach (var r in after.Rows.Where(r => r.Rank <= top))
        {
            if (!oldRank.TryGetValue(r.StudentId, out var prev)) continue;
            var d = prev - r.Rank;
            if (d == 0) continue;
            changed.Add($"{r.Name} {(d > 0 ? "↑" : "↓")}{System.Math.Abs(d)}");
        }

        return changed.Count == 0
            ? $"前 {top} 名位次无变化"
            : $"前 {top} 名中 {changed.Count} 人位次变化（{string.Join("，", changed.Take(4))}）";
    }
}
