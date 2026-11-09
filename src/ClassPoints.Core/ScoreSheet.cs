using System.Globalization;
using System.Text;

namespace ClassPoints.Core;

/// <summary>从 Excel / CSV 读出来的原始网格。Core 不认识 NPOI，只认识这张表。</summary>
public sealed class SheetGrid
{
    public SheetGrid(string name, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Name = name;
        Rows = rows;
    }

    public string Name { get; }
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public int RowCount => Rows.Count;

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    public string Cell(int row, int col) =>
        row >= 0 && row < Rows.Count && col >= 0 && col < Rows[row].Count ? Rows[row][col] ?? "" : "";

    public bool IsRowEmpty(int row) =>
        row < 0 || row >= Rows.Count || Rows[row].All(string.IsNullOrWhiteSpace);

    public string ColumnLetter(int index)
    {
        var s = "";
        var n = index;
        do
        {
            s = (char)('A' + n % 26) + s;
            n = n / 26 - 1;
        } while (n >= 0);
        return s;
    }
}

public sealed record ColumnOption(int Index, string Header, string Sample)
{
    public string Display => string.IsNullOrWhiteSpace(Header)
        ? $"{Letter} 列"
        : $"{Letter} 列 · {Header}";

    public string Letter => IndexToLetter(Index);

    public static string IndexToLetter(int index)
    {
        var s = "";
        var n = index;
        do
        {
            s = (char)('A' + n % 26) + s;
            n = n / 26 - 1;
        } while (n >= 0);
        return s;
    }
}

public sealed record ScoreSheetLayout(int HeaderRow, int NameColumn, int ScoreColumn, IReadOnlyList<ColumnOption> Columns)
{
    public bool IsValid => NameColumn >= 0 && ScoreColumn >= 0 && NameColumn != ScoreColumn;
}

public enum ScoreMatchKind
{
    Matched,
    Absent,
    Summary,
    Duplicate,
    Unmatched,
    Invalid,
}

public sealed class ParsedScoreRow
{
    public required string RawName { get; init; }
    public required string RawScore { get; init; }
    public string CleanName { get; init; } = "";
    public double? Score { get; set; }
    public bool Absent { get; set; }
    public ScoreMatchKind Kind { get; set; }
    public Student? Matched { get; set; }
    public IReadOnlyList<Student> Candidates { get; set; } = Array.Empty<Student>();
    public string? StatusText { get; set; }
    public bool Include { get; set; } = true;

    public string ScoreText => Absent ? "缺考" : Score is double d ? d.ToString("0.#") : RawScore;
}

public sealed class ScoreParseResult
{
    public required ScoreSheetLayout Layout { get; init; }
    public required IReadOnlyList<ParsedScoreRow> Rows { get; init; }

    public int MatchedCount => Rows.Count(r => r.Kind == ScoreMatchKind.Matched);
    public int AbsentCount => Rows.Count(r => r.Kind == ScoreMatchKind.Absent);
    public int IgnoredCount => Rows.Count(r => r.Kind == ScoreMatchKind.Summary);
    public int PendingCount => Rows.Count(r => r.Kind is ScoreMatchKind.Duplicate or ScoreMatchKind.Unmatched or ScoreMatchKind.Invalid);

    /// <summary>可导入人数：匹配上 + 缺考（缺考也要占位，权重再分配靠它）。</summary>
    public int ImportableCount => Rows.Count(r => r.Include &&
        r.Kind is ScoreMatchKind.Matched or ScoreMatchKind.Absent);

    public string SummaryText =>
        $"可导入 {ImportableCount} 人，缺考 {AbsentCount} 人，忽略 {IgnoredCount} 行（汇总行），{PendingCount} 行待处理";
}

/// <summary>
/// 成绩单解析：只认两列（姓名 + 总分），其余全是噪声。
/// 表头从第 1 行往下扫 10 行找「姓名/学生」，总分列在表头里取最右一个匹配。
/// </summary>
public static class ScoreSheetParser
{
    private static readonly string[] NameHeaders = { "姓名", "学生", "名字", "学生姓名" };
    private static readonly (string Key, int Priority)[] ScoreHeaders =
    {
        ("总分", 0), ("总成绩", 0), ("总分成绩", 0), ("合计", 0), ("总计", 0), ("总分数", 0),
        ("成绩", 1), ("分数", 1), ("得分", 1),
    };

    public static int DetectHeaderRow(SheetGrid grid, int scan = 10)
    {
        var limited = System.Math.Min(scan, grid.RowCount);
        for (var r = 0; r < limited; r++)
        {
            var cols = grid.ColumnCount;
            for (var c = 0; c < cols; c++)
            {
                var k = NameNormalizer.NormalizeKey(grid.Cell(r, c));
                if (NameHeaders.Any(h => k.Contains(h))) return r;
            }
        }
        return 0;
    }

    public static ScoreSheetLayout DetectLayout(SheetGrid grid)
    {
        var headerRow = DetectHeaderRow(grid);
        var cols = grid.ColumnCount;

        var nameCol = -1;
        var bestScoreCol = -1;
        var bestScorePriority = int.MaxValue;

        for (var c = 0; c < cols; c++)
        {
            var k = NameNormalizer.NormalizeKey(grid.Cell(headerRow, c));
            if (nameCol < 0 && NameHeaders.Any(k.Contains))
                nameCol = c;

            foreach (var (key, pri) in ScoreHeaders)
            {
                if (!k.Contains(key)) continue;
                // 同名优先级取最右一个；"成绩"排在最后，因为"语文成绩""数学成绩"都含它
                if (pri < bestScorePriority || (pri == bestScorePriority && c > bestScoreCol))
                {
                    bestScorePriority = pri;
                    bestScoreCol = c;
                }
            }
        }

        // 表头里没找到总分列：退化成「姓名列右侧第一个看起来像数字的列」
        if (bestScoreCol < 0)
        {
            for (var c = nameCol + 1; c < cols; c++)
            {
                var hasNumber = Enumerable.Range(headerRow + 1, System.Math.Max(0, System.Math.Min(5, grid.RowCount - headerRow - 1)))
                    .Any(r => TryParseScore(grid.Cell(r, c), out _));
                if (hasNumber)
                {
                    bestScoreCol = c;
                    break;
                }
            }
        }

        if (nameCol < 0 && cols > 0) nameCol = 0;

        var options = new List<ColumnOption>();
        for (var c = 0; c < cols; c++)
        {
            var sample = "";
            for (var r = headerRow + 1; r < grid.RowCount && r <= headerRow + 4; r++)
            {
                var v = grid.Cell(r, c).Trim();
                if (v.Length == 0) continue;
                sample = v.Length > 12 ? v[..12] : v;
                break;
            }
            options.Add(new ColumnOption(c, grid.Cell(headerRow, c).Trim(), sample));
        }

        return new ScoreSheetLayout(headerRow, nameCol, bestScoreCol, options);
    }

    public static ScoreParseResult Parse(SheetGrid grid, IReadOnlyList<Student> students, ScoreSheetLayout? layout = null)
    {
        layout ??= DetectLayout(grid);

        var byKey = new Dictionary<string, List<Student>>();
        foreach (var s in students.Where(s => s.Active && s.DeletedAt is null))
        {
            var k = NameNormalizer.NormalizeKey(s.Name);
            if (k.Length == 0) continue;
            if (!byKey.TryGetValue(k, out var list)) byKey[k] = list = new List<Student>();
            list.Add(s);
        }

        var rows = new List<ParsedScoreRow>();
        for (var r = layout.HeaderRow + 1; r < grid.RowCount; r++)
        {
            if (grid.IsRowEmpty(r)) continue;

            var rawName = grid.Cell(r, layout.NameColumn).Trim();
            var rawScore = grid.Cell(r, layout.ScoreColumn).Trim();

            if (rawName.Length == 0 && rawScore.Length == 0) continue;

            var row = new ParsedScoreRow { RawName = rawName, RawScore = rawScore, CleanName = NameNormalizer.Clean(rawName) };

            if (NameNormalizer.IsLikelySummaryRow(rawName))
            {
                row.Kind = ScoreMatchKind.Summary;
                row.StatusText = "非学生行，已忽略";
                row.Include = false;
                rows.Add(row);
                continue;
            }

            if (IsAbsentMark(rawScore))
            {
                row.Absent = true;
                row.Score = null;
            }
            else if (TryParseScore(rawScore, out var score))
            {
                row.Score = score;
                row.Absent = false;
            }
            else
            {
                row.Kind = ScoreMatchKind.Invalid;
                row.StatusText = $"分数读不出来（{rawScore}）";
                rows.Add(row);
                continue;
            }

            var key = NameNormalizer.NormalizeKey(rawName);
            if (byKey.TryGetValue(key, out var list) && list.Count > 0)
            {
                if (list.Count > 1)
                {
                    row.Kind = ScoreMatchKind.Duplicate;
                    row.Candidates = list;
                    row.StatusText = "两人同名，请选择";
                }
                else
                {
                    row.Matched = list[0];
                    row.Kind = row.Absent ? ScoreMatchKind.Absent : ScoreMatchKind.Matched;
                    row.StatusText = $"{list[0].Name} · {list[0].SeatText}";
                }
            }
            else
            {
                row.Kind = ScoreMatchKind.Unmatched;
                row.StatusText = "名单里没有这个人";
            }

            rows.Add(row);
        }

        return new ScoreParseResult { Layout = layout, Rows = rows };
    }

    private static readonly string[] AbsentMarks = { "缺考", "缺", "免考", "未考", "未参加", "无", "-", "—", "/", "\\", "空", "缺席" };

    public static bool IsAbsentMark(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return true;
        var k = s.Normalize(NormalizationForm.FormKC).Trim();
        return AbsentMarks.Any(m => k.Equals(m, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryParseScore(string? raw, out double score)
    {
        score = 0;
        var s = (raw ?? "").Normalize(NormalizationForm.FormKC).Trim();
        if (s.Length == 0) return false;
        s = s.Replace("分", "").Replace(" ", "").Replace(",", "");
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out score))
            return true;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out score);
    }
}
