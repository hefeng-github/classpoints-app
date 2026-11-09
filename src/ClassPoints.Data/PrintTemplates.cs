using System.Text;
using ClassPoints.Core;

namespace ClassPoints.Data;

/// <summary>打印时页脚要自证是哪一版数据，所以每份 HTML 都带时间和校验码。</summary>
public sealed record PrintContext(
    string ClassName,
    string PeriodName,
    string RangeText,
    string FormulaText,
    string ExportedBy,
    DateTime ExportedAt,
    string Checksum)
{
    public string StampText =>
        $"{ClassName} · {PeriodName} · 打印于 {ExportedAt:yyyy-MM-dd HH:mm} · {ExportedBy} · 校验码 {Checksum}";
}

/// <summary>
/// 五种版式的 HTML 模板。预览和打印是同一份 HTML —— 所见即所印（方案 10.6）。
/// 只引入系统自带字体：教室断网时，Web 字体会打出一堆方块。
/// </summary>
public static class PrintTemplates
{
    private const string BaseCss = """
        * { box-sizing: border-box; }
        html, body { margin: 0; padding: 0; }
        body {
          font-family: "Microsoft YaHei", "Microsoft YaHei UI", "SimSun", "PingFang SC", sans-serif;
          color: #1A1A1A; background: #fff;
          -webkit-print-color-adjust: exact; print-color-adjust: exact;
        }
        h1 { font-size: 22pt; margin: 0 0 4pt; letter-spacing: 2pt; }
        .sub { font-size: 9pt; color: #555; margin-bottom: 10pt; }
        .meta { display: flex; justify-content: space-between; font-size: 9pt; color: #444; margin-bottom: 6pt; }
        table { width: 100%; border-collapse: collapse; }
        th, td { border: 0.5pt solid #B9B9B9; padding: 3.2pt 4pt; font-size: 9.5pt; }
        th { background: #EDEFF2; font-weight: 600; text-align: center; }
        td.num { text-align: right; font-variant-numeric: tabular-nums; }
        td.c { text-align: center; }
        tr { page-break-inside: avoid; break-inside: avoid; }
        thead { display: table-header-group; }
        .zebra tbody tr:nth-child(even) { background: #FAFBFC; }
        .foot {
          position: fixed; bottom: 0; left: 0; right: 0;
          font-size: 7.5pt; color: #777; text-align: center;
          border-top: 0.5pt solid #DDD; padding-top: 3pt;
        }
        .note { font-size: 8.5pt; color: #8A6D00; background: #FFF8E1; border: 0.5pt solid #F0D98C;
                padding: 5pt 7pt; margin-top: 8pt; page-break-inside: avoid; }
        .sign { margin-top: 18pt; font-size: 9pt; color: #444; }
        .page { page-break-after: always; break-after: page; }
        .page:last-child { page-break-after: auto; break-after: auto; }
        """;

    private static string Wrap(string title, string pageCss, string body, PrintContext ctx, string footerExtra = "")
    {
        var sb = new StringBuilder(8 * 1024);
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(E(title)).Append("</title><style>");
        sb.Append(BaseCss).Append(pageCss).Append("</style></head><body>");
        sb.Append(body);
        sb.Append("<div class=\"foot\">").Append(E(ctx.StampText));
        if (!string.IsNullOrWhiteSpace(footerExtra)) sb.Append(" · ").Append(E(footerExtra));
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    // ---------------- 1. 班级排名表（贴后墙） ----------------
    /// <summary>默认不含总评：贴出来等于把「谁考试差」公开到全班，而积分本来就公开。</summary>
    public static string RankingTable(
        PrintContext ctx,
        IReadOnlyList<RankingRow> rows,
        bool includeTotal,
        int? totalColumnPercent = null,
        bool normalized = false)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>").Append(E(ctx.ClassName)).Append(" · 积分排名</h1>");
        sb.Append("<div class=\"meta\"><span>").Append(E(ctx.PeriodName)).Append("　").Append(E(ctx.RangeText))
          .Append("</span><span>共 ").Append(rows.Count).Append(" 人</span></div>");

        sb.Append("<table class=\"zebra\"><thead><tr><th style=\"width:10%\">名次</th><th style=\"width:30%\">姓名</th>")
          .Append("<th style=\"width:22%\">座位</th><th>积分</th>");
        if (includeTotal)
            sb.Append("<th>").Append(normalized ? "总评（百分制）" : "总评（加权和）").Append("</th>");
        sb.Append("</tr></thead><tbody>");

        foreach (var r in rows)
        {
            sb.Append("<tr><td class=\"c\">").Append(r.Rank).Append("</td>");
            sb.Append("<td>").Append(E(r.Name)).Append("</td>");
            sb.Append("<td class=\"c\">").Append(E(r.SeatText)).Append("</td>");
            sb.Append("<td class=\"num\">").Append(r.Points).Append("</td>");
            if (includeTotal) sb.Append("<td class=\"num\">").Append(r.Total.ToString("0.0")).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");

        if (includeTotal && !normalized)
            sb.Append("<div class=\"note\">本表总评为加权和，不是百分制分数，不可与考试成绩直接比较；同一表内排名有效。</div>");

        var css = "@page { size: A4 landscape; margin: 12mm; } .foot { position: static; margin-top: 10pt; }";
        return Wrap($"{ctx.ClassName} 积分排名", css, sb.ToString(), ctx,
            includeTotal ? ctx.FormulaText : "本表不含总评");
    }

    // ---------------- 2. 期末总评表（评优/上报/存档） ----------------
    public static string FinalGrade(
        PrintContext ctx,
        IReadOnlyList<RankingRow> rows,
        IReadOnlyList<Exam> exams,
        IReadOnlyDictionary<string, double> weights,
        Scheme scheme,
        AbsencePolicy policy)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>").Append(E(ctx.ClassName)).Append(" · 期末总评</h1>");
        sb.Append("<div class=\"meta\"><span>").Append(E(ctx.PeriodName)).Append("　").Append(E(ctx.RangeText))
          .Append("</span><span>共 ").Append(rows.Count).Append(" 人</span></div>");

        sb.Append("<table class=\"zebra\"><thead><tr><th style=\"width:8%\">名次</th><th style=\"width:14%\">姓名</th>")
          .Append("<th style=\"width:10%\">座位</th><th>考试总分</th><th>考试百分制</th><th>平时分（积分）</th><th>总评</th>")
          .Append("</tr></thead><tbody>");

        foreach (var r in rows)
        {
            sb.Append("<tr><td class=\"c\">").Append(r.Rank).Append("</td>");
            sb.Append("<td>").Append(E(r.Name)).Append("</td>");
            sb.Append("<td class=\"c\">").Append(r.SeatNo?.ToString() ?? "—").Append("</td>");
            sb.Append("<td class=\"num\">").Append(r.ExamRaw.ToString("0.0")).Append("</td>");
            sb.Append("<td class=\"num\">").Append(r.ExamPercent.ToString("0.0")).Append("</td>");
            sb.Append("<td class=\"num\">").Append(r.Points).Append("</td>");
            sb.Append("<td class=\"num\"><b>").Append(r.Total.ToString("0.0")).Append("</b></td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<div class=\"note\">");
        sb.Append("折算公式：").Append(E(scheme.FormulaText)).Append("<br>");
        sb.Append("总评口径：").Append(scheme.Normalize ? "百分制（已归一）" : "原值加权和（未归一，不可与考试分直接比较）").Append("<br>");
        sb.Append("缺考处理：").Append(E(WorkbookBuilder.PolicyLong(policy)));
        if (exams.Count > 0)
        {
            sb.Append("<br>各场权重：");
            sb.Append(string.Join("　", exams.Select(e =>
                $"{E(e.Name)} 满分 {e.FullScore:0.#} 权重 {weights.GetValueOrDefault(e.Id) * 100:0.#}%")));
        }
        sb.Append("</div>");

        sb.Append("<div class=\"sign\">班主任签字：________________　　　　").Append(ctx.ExportedAt.ToString("yyyy 年 M 月 d 日")).Append("</div>");

        var css = "@page { size: A4 landscape; margin: 12mm; } .foot { position: static; margin-top: 10pt; }";
        return Wrap($"{ctx.ClassName} 期末总评", css, sb.ToString(), ctx);
    }

    // ---------------- 3. 个人成绩单（家长会一人一张） ----------------
    public static string PersonalReport(
        PrintContext ctx,
        Student student,
        RankingRow rank,
        IReadOnlyList<Exam> exams,
        IReadOnlyDictionary<string, double> weights,
        IReadOnlyDictionary<(string ExamId, string StudentId), ExamScore> scores,
        Scheme scheme,
        int classSize,
        IReadOnlyList<Entry> recentEntries,
        IReadOnlyList<(string Label, int Count, int Points)> distribution)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"pr\">");
        sb.Append("<div class=\"pr-head\"><div><div class=\"pr-name\">").Append(E(student.Name))
          .Append("</div><div class=\"pr-seat\">").Append(E(student.SeatText))
          .Append("　班级第 ").Append(rank.Rank).Append(" 名</div></div>")
          .Append("<div class=\"pr-badge\">总评 ").Append(rank.Total.ToString("0.0")).Append("</div></div>");

        if (exams.Count > 0)
        {
            sb.Append("<table class=\"pr-table\"><thead><tr><th>考试</th><th>日期</th><th>满分</th><th>得分</th><th>百分制</th><th>权重</th></tr></thead><tbody>");
            foreach (var e in exams)
            {
                scores.TryGetValue((e.Id, student.Id), out var sc);
                var absent = sc is null || sc.Absent || sc.Score is null;
                var full = e.FullScore > 0 ? e.FullScore : 750;
                sb.Append("<tr><td>").Append(E(e.Name)).Append("</td>");
                sb.Append("<td class=\"c\">").Append(E(e.ExamDate ?? "—")).Append("</td>");
                sb.Append("<td class=\"num\">").Append(e.FullScore.ToString("0.#")).Append("</td>");
                if (absent)
                    sb.Append("<td class=\"c\" colspan=\"2\">缺考</td>");
                else
                    sb.Append("<td class=\"num\">").Append(sc!.Score!.Value.ToString("0.#"))
                      .Append("</td><td class=\"num\">").Append((sc.Score!.Value / full * 100).ToString("0.0")).Append("</td>");
                sb.Append("<td class=\"num\">").Append((weights.GetValueOrDefault(e.Id) * 100).ToString("0.#")).Append("%</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        sb.Append("<div class=\"pr-grid\">");
        sb.Append("<div class=\"pr-cell\"><div class=\"pr-v\">").Append(rank.Points).Append("</div><div class=\"pr-k\">平时分（学期累计积分）</div></div>");
        sb.Append("<div class=\"pr-cell\"><div class=\"pr-v\">").Append(rank.ExamRaw.ToString("0.0")).Append("</div><div class=\"pr-k\">考试总分（加权）</div></div>");
        sb.Append("<div class=\"pr-cell\"><div class=\"pr-v\">").Append(rank.Total.ToString("0.0")).Append("</div><div class=\"pr-k\">")
          .Append(scheme.Normalize ? "总评（百分制）" : "总评（加权和）").Append("</div></div>");
        sb.Append("</div>");

        if (distribution.Count > 0)
        {
            sb.Append("<div class=\"pr-sec\">积分构成</div><table class=\"pr-table\"><thead><tr><th>项目</th><th>次数</th><th>小计</th></tr></thead><tbody>");
            foreach (var (label, count, points) in distribution.Take(10))
                sb.Append("<tr><td>").Append(E(label)).Append("</td><td class=\"num\">").Append(count)
                  .Append("</td><td class=\"num\">").Append(points >= 0 ? "+" : "").Append(points).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        if (recentEntries.Count > 0)
        {
            sb.Append("<div class=\"pr-sec\">最近记录</div><table class=\"pr-table\"><thead><tr><th>时间</th><th>项目</th><th>分值</th></tr></thead><tbody>");
            foreach (var e in recentEntries.Take(10))
                sb.Append("<tr><td>").Append(E(Clock.ToLocal(e.OccurredAt).ToString("MM-dd HH:mm")))
                  .Append("</td><td>").Append(E(e.RuleLabel ?? "")).Append("</td><td class=\"num\">")
                  .Append(e.Delta >= 0 ? "+" : "").Append(e.Delta).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<div class=\"note\" style=\"font-size:7.8pt\">").Append(E(scheme.FormulaText))
          .Append(scheme.Normalize ? "" : "；总评为加权和，不是百分制分数，不可与考试分直接比较").Append("</div>");
        sb.Append("<div class=\"pr-sign\">家长签字：__________________　　班主任：__________________</div>");
        sb.Append("</div>");

        const string css = """
            @page { size: A4 portrait; margin: 14mm 14mm 16mm; }
            .page { page-break-after: always; }
            .pr { height: 250mm; display: flex; flex-direction: column; page-break-inside: avoid; break-inside: avoid; }
            .pr-head { display: flex; justify-content: space-between; align-items: flex-end; border-bottom: 2pt solid #1A1A1A; padding-bottom: 6pt; }
            .pr-name { font-size: 26pt; font-weight: 700; letter-spacing: 3pt; }
            .pr-seat { font-size: 10pt; color: #555; margin-top: 2pt; }
            .pr-badge { font-size: 16pt; font-weight: 700; }
            .pr-table { margin-top: 8pt; }
            .pr-grid { display: flex; gap: 6pt; margin-top: 10pt; }
            .pr-cell { flex: 1; border: 0.5pt solid #C9C9C9; padding: 8pt; text-align: center; }
            .pr-v { font-size: 20pt; font-weight: 700; font-variant-numeric: tabular-nums; }
            .pr-k { font-size: 8.5pt; color: #555; margin-top: 2pt; }
            .pr-sec { font-size: 11pt; font-weight: 600; margin-top: 12pt; }
            .pr-sign { margin-top: auto; font-size: 9.5pt; color: #444; padding-top: 14pt; }
            .foot { position: static; margin-top: 6pt; }
            """;
        return Wrap($"{student.Name} · 个人成绩单", css, sb.ToString(), ctx);
    }

    /// <summary>50 页个人成绩单：一次生成一整份，分页在文档流里自然断开（每条学生一个 .pr）。</summary>
    public static string PersonalReportBatch(PrintContext ctx, IEnumerable<string> pages)
    {
        var sb = new StringBuilder();
        foreach (var (html, i) in pages.Select((h, i) => (h, i)))
        {
            if (i > 0) sb.Append("<div class=\"page\"></div>");
            sb.Append(html);
        }
        const string css = "@page { size: A4 portrait; margin: 14mm 14mm 16mm; }";
        return Wrap("个人成绩单", css, sb.ToString(), ctx);
    }

    // ---------------- 4. 恢复卡（名片大小） ----------------
    public static string RecoveryCard(string className, string recoveryCode, string classCode, DateTime generatedAt)
    {
        var ctx = new PrintContext(className, "恢复卡", "", "Crockford Base32", "", generatedAt, "");
        var body = $"""
            <div class="rc">
              <div class="rc-title">班级积分 · 恢复卡</div>
              <div class="rc-class">{E(className)}</div>
              <div class="rc-label">恢复码（抄在纸上也行）</div>
              <div class="rc-code">{E(recoveryCode)}</div>
              {(string.IsNullOrWhiteSpace(classCode) ? "" : $"<div class=\"rc-label\">班级码</div><div class=\"rc-classnum\">{E(classCode)}</div>")}
              <div class="rc-tip">
                忘掉管理模式密码、或在新设备上接管这个班时，用这个码。<br>
                卡号里字母 O/I/L 一律按 0/1/1 认，抄错一位也认得出来。<br>
                这张纸请和钱包放一起，别贴在教室墙上。
              </div>
            </div>
            """;
        const string css = """
            @page { size: 90mm 55mm; margin: 5mm; }
            .foot { display: none; }
            .rc { font-size: 8pt; }
            .rc-title { font-size: 10pt; font-weight: 700; letter-spacing: 1pt; }
            .rc-class { font-size: 12pt; font-weight: 700; margin-top: 2pt; }
            .rc-label { color: #666; margin-top: 4pt; font-size: 7pt; }
            .rc-code {
              font-family: Consolas, "Courier New", monospace; font-size: 15pt; font-weight: 700;
              letter-spacing: 2pt; border: 0.8pt dashed #666; padding: 3pt 5pt; margin-top: 1pt; text-align: center;
            }
            .rc-classnum { font-family: Consolas, monospace; font-size: 12pt; font-weight: 700; letter-spacing: 3pt; }
            .rc-tip { margin-top: 4pt; font-size: 6.8pt; color: #555; line-height: 1.5; }
            """;
        return Wrap("恢复卡", css, body, ctx);
    }

    // ---------------- 5. 周期归档封面 ----------------
    public static string ArchiveCover(PrintContext ctx, int entryCount, int studentCount, string archivePath, string checksum)
    {
        var body = $"""
            <div class="cover">
              <div class="cv-kicker">班级积分 · 周期归档</div>
              <div class="cv-title">{E(ctx.ClassName)}</div>
              <div class="cv-period">{E(ctx.PeriodName)}</div>
              <div class="cv-range">{E(ctx.RangeText)}</div>
              <table class="cv-table">
                <tr><th>记录条数</th><td>{entryCount:N0} 条</td></tr>
                <tr><th>在册学生</th><td>{studentCount} 人</td></tr>
                <tr><th>归档时间</th><td>{ctx.ExportedAt:yyyy-MM-dd HH:mm:ss}</td></tr>
                <tr><th>归档文件</th><td class="mono">{E(archivePath)}</td></tr>
                <tr><th>校验码</th><td class="mono">{E(checksum)}</td></tr>
                <tr><th>折算公式</th><td>{E(ctx.FormulaText)}</td></tr>
              </table>
              <div class="cv-sign">归档人：{E(ctx.ExportedBy)}　　　　签字：__________________</div>
              <div class="cv-tip">
                这个周期的记录没有被删除，只是被归档。要查旧账：设置 → 历史周期 → 选中这一项 → 查看榜单或导出。
              </div>
            </div>
            """;
        const string css = """
            @page { size: A4 portrait; margin: 20mm; }
            .foot { position: static; margin-top: 12pt; }
            .cover { display: flex; flex-direction: column; height: 240mm; }
            .cv-kicker { font-size: 9pt; color: #666; letter-spacing: 3pt; }
            .cv-title { font-size: 30pt; font-weight: 700; letter-spacing: 4pt; margin-top: 14pt; }
            .cv-period { font-size: 17pt; margin-top: 6pt; }
            .cv-range { font-size: 10pt; color: #555; margin-top: 4pt; }
            .cv-table { margin-top: 26pt; }
            .cv-table th { width: 26%; text-align: left; background: #F2F4F7; }
            .cv-table td { text-align: left; }
            .mono { font-family: Consolas, monospace; font-size: 8.5pt; word-break: break-all; }
            .cv-sign { margin-top: 34pt; font-size: 10pt; }
            .cv-tip { margin-top: auto; font-size: 8.5pt; color: #666; border-top: 0.5pt solid #DDD; padding-top: 6pt; }
            """;
        return Wrap("周期归档封面", css, body, ctx);
    }

    /// <summary>HTML 转义。学生姓名里出现 &lt; 的概率很低，但出现一次就能把整张表打乱。</summary>
    public static string E(string? raw) =>
        string.IsNullOrEmpty(raw)
            ? ""
            : raw.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                 .Replace("\"", "&quot;").Replace("'", "&#39;");
}
