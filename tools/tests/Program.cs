using ClassPoints.Core;
using ClassPoints.Core.Config;
using ClassPoints.Data;
using ClassPoints.Data.Sync;

namespace ClassPoints.Tests;

/// <summary>
/// 自带的断言工具：不引 xunit，一条命令就能跑。
///
///   dotnet run --project tools/tests                 只跑 Core 纯函数（离线，秒级）
///   dotnet run --project tools/tests -- --sync http://127.0.0.1:8787
///                                                    外加一轮真连 Worker 的端到端同步
///
/// 不引测试框架是刻意的：这个项目要在没有 VS、没有 NuGet 外网的教室电脑上也能自检。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static readonly List<string> Failures = [];
    private static string _section = "";

    private static async Task<int> Main(string[] args)
    {
        var syncUrl = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--sync" && i + 1 < args.Length) syncUrl = args[i + 1];
            else if (args[i].StartsWith("--sync=", StringComparison.Ordinal)) syncUrl = args[i]["--sync=".Length..];
        }

        Section("封顶");
        CapTests();
        Section("权重与口径");
        SchemeTests();
        Section("排名");
        RankingTests();
        Section("姓名归一");
        NameTests();
        Section("密码与恢复码");
        SecurityTests();
        Section("成绩单解析");
        ScoreSheetTests();
        Section("CSV");
        CsvTests();
        Section("预配置包");
        ConfigTests.Run(Check);
        Section("Excel 读写");
        ExcelTests.Run(Check);
        Section("事件计数");
        CounterTests.Run(Check);
        Section("同步凭据");
        SyncStoreTests.Run(Check);

        // 造一份真实的配置包，给"家里导出 → 学校导入"这条链路做验收用
        var makeConfig = args.FirstOrDefault(a => a.StartsWith("--make-config=", StringComparison.Ordinal));
        if (makeConfig is not null)
        {
            var parts = makeConfig["--make-config=".Length..].Split('|');
            var bundle = new ConfigBundle
            {
                CreatedAt = Clock.NowMs(),
                Note = parts.Length > 4 ? parts[4] : "",
                ClassName = parts.Length > 5 ? parts[5] : "初二（3）班",
                SeatRows = 6,
                SeatCols = 8,
                Sync = new ConfigSync { BaseUrl = parts[0], ClassCode = parts[1] },
            };
            if (parts.Length > 2 && parts[2].Length > 0) bundle.Protect(parts[2], parts[3]);
            ClassPoints.Data.Config.ConfigBundleFile.Save(bundle, parts[^1]);
            Console.WriteLine("已生成配置包：" + parts[^1]);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(syncUrl))
        {
            Section("端到端同步（真连 " + syncUrl + "）");
            await SyncTests.RunAsync(syncUrl, Check);
        }

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine($"✅ 全部通过：{_pass} 项");
            return 0;
        }

        Console.WriteLine($"❌ 通过 {_pass} 项，失败 {Failures.Count} 项");
        foreach (var f in Failures) Console.WriteLine("   - " + f);
        return 1;
    }

    private static void Section(string name)
    {
        _section = name;
        Console.WriteLine();
        Console.WriteLine($"-- {name} --");
    }

    internal static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  ✓ {name}");
        }
        else
        {
            Failures.Add($"[{_section}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
            Console.WriteLine($"  ✗ {name}{(detail.Length > 0 ? " — " + detail : "")}");
        }
    }

    // ---------------- 封顶 ----------------

    private static void CapTests()
    {
        var single = new Rule { Label = "回答问题", Delta = 5, CapValue = 10, CapScope = CapScope.Single };
        var r1 = CapPolicy.Apply(single, 5, 0);
        Check("单次上限内原样通过", r1.AppliedDelta == 5 && !r1.Capped);

        var r2 = CapPolicy.Apply(single, 15, 0);
        Check("单次上限被压回", r2.AppliedDelta == 10 && r2.Capped && r2.Message is not null, r2.Message ?? "");

        var r3 = CapPolicy.Apply(single, 15, 0, forceOverCap: true);
        Check("管理者硬顶算原值并标 over_cap", r3.AppliedDelta == 15 && r3.OverCap && !r3.Capped);

        var r4 = CapPolicy.Apply(single, -5, 100);
        Check("扣分不受上限约束", r4.AppliedDelta == -5 && !r4.Capped);

        var periodRule = new Rule { Label = "值日", Delta = 5, CapValue = 10, CapScope = CapScope.Period };
        var r5 = CapPolicy.Apply(periodRule, 5, 8);
        Check("周期上限只补剩余额度", r5.AppliedDelta == 2 && r5.Capped, $"applied={r5.AppliedDelta}");

        var r6 = CapPolicy.Apply(periodRule, 5, 10);
        Check("周期额度用完就拦住", r6.IsBlocked && r6.Message is not null, r6.Message ?? "");
        Check("额度用完的判词说得清楚", CapPolicy.IsExhausted(periodRule, 10));
        Check("没封顶的规则不显示角标", CapPolicy.RemainingBadge(new Rule { Label = "x", Delta = 1 }, 0) is null);
    }

    // ---------------- 权重 ----------------

    private static void SchemeTests()
    {
        var scheme = new Scheme { ExamWeight = 0.7, DailyWeight = 0.3 };
        var exams = new List<Exam>
        {
            new() { Id = "e1", Name = "期中" },
            new() { Id = "e2", Name = "期末" },
        };

        var even = SchemeMath.BuildExamWeights(exams, new Dictionary<string, double>());
        Check("没配置时按场次均分", System.Math.Abs(even["e1"] - 0.5) < 1e-9);

        var configured = SchemeMath.BuildExamWeights(exams, new Dictionary<string, double> { ["e1"] = 3, ["e2"] = 1 });
        Check("配了权重就归一到 1", System.Math.Abs(configured["e1"] - 0.75) < 1e-9, configured["e1"].ToString("0.000"));

        var balanced = SchemeMath.AutoBalance(new Dictionary<string, double> { ["a"] = 30, ["b"] = 70 });
        Check("自动配平合计 100%", System.Math.Abs(balanced.Values.Sum() - 1) < 1e-9);

        // 原值加权下 700 分考试和 100 分积分不是 7:3，这一行必须如实显示
        var (ex, da) = SchemeMath.ActualImpact(700, 100, scheme);
        Check("原值加权的实际影响不是名义 70:30", ex > 90, $"考试影响 {ex:0}%");
        Check("实际影响有一句人话", SchemeMath.ImpactText(700, 100, scheme).Contains("实际影响"));

        var normalized = new Scheme { ExamWeight = 0.7, DailyWeight = 0.3, Normalize = true };
        var (nex, nda) = SchemeMath.ActualImpact(700, 100, normalized);
        Check("归一后名义即实际", System.Math.Abs(nex - 70) < 1e-6 && System.Math.Abs(nda - 30) < 1e-6);
    }

    // ---------------- 排名 ----------------

    private static void RankingTests()
    {
        var students = new List<Student>
        {
            new() { Id = "s1", Name = "杨欣怡", Active = true, SeatNo = 1 },
            new() { Id = "s2", Name = "陈芷若", Active = true, SeatNo = 2 },
            new() { Id = "s3", Name = "刘书豪", Active = true, SeatNo = 3 },
        };
        var points = new Dictionary<string, int> { ["s1"] = 40, ["s2"] = 30, ["s3"] = 20 };
        var exams = new List<Exam> { new() { Id = "e1", Name = "期中", FullScore = 750 } };
        var scores = new List<ExamScore>
        {
            new() { ExamId = "e1", StudentId = "s1", Score = 600 },
            new() { ExamId = "e1", StudentId = "s2", Score = 450 },
            new() { ExamId = "e1", StudentId = "s3", Score = 300 },
        };
        var scheme = new Scheme { Id = "sc", ExamWeight = 0.7, DailyWeight = 0.3 };

        var result = RankingCalculator.Compute(students, points, exams, scores,
            new Dictionary<string, double>(), scheme, AbsencePolicy.Redistribute);

        Check("三个人都在榜上", result.Rows.Count == 3);
        var top = result.Rows[0];
        Check("第一名是积分和考试都高的那个", top.StudentId == "s1", top.Name);
        // 原值加权：600*0.7 + 40*0.3 = 432
        Check("原值加权算式正确", System.Math.Abs(top.Total - 432) < 1e-6, top.Total.ToString("0.0"));
        Check("名次连续且从 1 开始", result.Rows.Select(r => r.Rank).SequenceEqual([1, 2, 3]));

        // 缺考：三策略必须给出不同结果，否则这个开关就是假的。
        // 两场考试才看得出差别 —— 只有一场时「再分配」等于「按 0 分计入」。
        var twoExams = new List<Exam>
        {
            new() { Id = "x1", Name = "期中", FullScore = 750 },
            new() { Id = "x2", Name = "期末", FullScore = 750 },
        };
        var twoScores = new List<ExamScore>
        {
            new() { ExamId = "x1", StudentId = "s1", Score = 600 },
            new() { ExamId = "x1", StudentId = "s2", Score = 450 },
            new() { ExamId = "x1", StudentId = "s3", Score = null, Absent = true },
            new() { ExamId = "x2", StudentId = "s1", Score = 600 },
            new() { ExamId = "x2", StudentId = "s2", Score = 450 },
            new() { ExamId = "x2", StudentId = "s3", Score = 450 },
        };

        var redistribute = RankingCalculator.Compute(students, points, twoExams, twoScores,
            new Dictionary<string, double>(), scheme, AbsencePolicy.Redistribute);
        var zero = RankingCalculator.Compute(students, points, twoExams, twoScores,
            new Dictionary<string, double>(), scheme, AbsencePolicy.Zero);
        var average = RankingCalculator.Compute(students, points, twoExams, twoScores,
            new Dictionary<string, double>(), scheme, AbsencePolicy.ClassAverage);

        var r3 = redistribute.Rows.First(r => r.StudentId == "s3").Total;
        var z3 = zero.Rows.First(r => r.StudentId == "s3").Total;
        var a3 = average.Rows.First(r => r.StudentId == "s3").Total;
        Check("缺考三策略给出三种结果", r3 != z3 && z3 != a3, $"{r3:0.0} / {z3:0.0} / {a3:0.0}");
        // 再分配：缺考那场的权重摊给剩下那场，450*0.7 + 20*0.3 = 321
        Check("缺考再分配按剩余场次算", System.Math.Abs(r3 - 321) < 1e-6, r3.ToString("0.0"));
        // 按 0 分：0*0.5 + 450*0.5 = 225 → 225*0.7 + 6 = 163.5
        Check("缺考按 0 分计入", System.Math.Abs(z3 - 163.5) < 1e-6, z3.ToString("0.0"));
        Check("缺考按全班均值替代", System.Math.Abs(a3 - 347.25) < 1e-6, a3.ToString("0.0"));
        Check("缺考名单会写进结果", redistribute.Rows.First(r => r.StudentId == "s3").AbsentExams.Contains("期中"));

        // 归一：600/750*100*0.7 + 40*0.3 = 56 + 12 = 68
        var norm = new Scheme { Id = "sc2", ExamWeight = 0.7, DailyWeight = 0.3, Normalize = true };
        var normResult = RankingCalculator.Compute(students, points, exams, scores,
            new Dictionary<string, double>(), norm, AbsencePolicy.Redistribute);
        // 归一口径：考试折百分制(600/750*100=80)，平时分占全班最高积分的百分比(40/40=100)
        Check("归一后总分是百分制", System.Math.Abs(normResult.Rows[0].Total - 86) < 1e-6,
            normResult.Rows[0].Total.ToString("0.0"));
    }

    // ---------------- 姓名 ----------------

    private static void NameTests()
    {
        Check("全角空格和前后空白被清掉", NameNormalizer.Clean("　张 三　") == "张 三", NameNormalizer.Clean("　张 三　"));
        Check("序号前缀被去掉", NameNormalizer.StripListIndex("03 杨欣怡") == "杨欣怡", NameNormalizer.StripListIndex("03 杨欣怡"));
        Check("顿号分隔的序号被去掉", NameNormalizer.StripListIndex("1、陈芷若") == "陈芷若", NameNormalizer.StripListIndex("1、陈芷若"));
        Check("不误伤以数字开头的真名字", NameNormalizer.StripListIndex("3班张三") == "3班张三");
        Check("归一化的键忽略空白与间隔点", NameNormalizer.NormalizeKey("买买提·艾力") == NameNormalizer.NormalizeKey("买买提 艾力"));
        Check("合计行会被识别出来", NameNormalizer.IsLikelySummaryRow("合计"));
        Check("平均分行会被识别出来", NameNormalizer.IsLikelySummaryRow("平均分"));
        Check("普通姓名不会被误判", !NameNormalizer.IsLikelySummaryRow("杨欣怡"));
    }

    // ---------------- 安全 ----------------

    private static void SecurityTests()
    {
        var hash = PasswordHasher.Hash("123456");
        Check("密码哈希可以校验通过", PasswordHasher.Verify("123456", hash));
        Check("错密码校验失败", !PasswordHasher.Verify("123457", hash));
        Check("空密码永远失败", !PasswordHasher.Verify("123456", null));

        var code = RecoveryCode.Generate();
        Check("恢复码能生成", RecoveryCode.LooksValid(code), code);
        Check("恢复码忽略大小写和分隔符", RecoveryCode.Normalize(code) == RecoveryCode.Normalize(code.ToLowerInvariant().Replace("-", " ")));
        Check("恢复码格式化带分隔符", RecoveryCode.Format(RecoveryCode.Normalize(code)).Contains('-'));

        var uid1 = Ids.NewUuid7();
        var uid2 = Ids.NewUuid7();
        Check("UUIDv7 版本位是 7", uid1[14] == '7', uid1);
        Check("UUIDv7 两次不相同", uid1 != uid2);
        Check("卡号归一化去掉冒号和空格",
            Crypto.NormalizeUid("04:A2:B3:C4") == Crypto.NormalizeUid("04a2b3c4"));

        var digest = Crypto.HashCredential("04A2B3C4D5E680");
        Check("卡号只存哈希不存明文", digest.Length == 64 && !digest.Contains("04a2", StringComparison.OrdinalIgnoreCase));
        Check("同一张卡两次哈希一致", Crypto.HashCredential("04A2B3C4D5E680") == digest);
    }

    // ---------------- 成绩单 ----------------

    private static void ScoreSheetTests()
    {
        var grid = new SheetGrid("Sheet1", new List<IReadOnlyList<string>>
        {
            new[] { "2026 秋季学期期中考试成绩表" },
            new[] { "序号", "姓名", "语文", "总分" },
            new[] { "1", "杨欣怡", "112", "712" },
            new[] { "2", "陈芷若", "98", "688" },
            new[] { "3", "刘书豪", "缺考", "缺考" },
            new[] { "", "合计", "", "1400" },
        });

        var layout = ScoreSheetParser.DetectLayout(grid);
        Check("表头行能自动找到", layout.HeaderRow == 1, layout.HeaderRow.ToString());
        Check("姓名列能自动找到", grid.Cell(layout.HeaderRow, layout.NameColumn) == "姓名");
        Check("总分列能自动找到", grid.Cell(layout.HeaderRow, layout.ScoreColumn) == "总分");

        var students = new List<Student>
        {
            new() { Id = "s1", Name = "杨欣怡", Active = true },
            new() { Id = "s2", Name = "陈芷若", Active = true },
            new() { Id = "s3", Name = "刘书豪", Active = true },
        };

        var parsed = ScoreSheetParser.Parse(grid, students, layout);
        var matched = parsed.Rows.Count(r => r.Kind == ScoreMatchKind.Matched);
        var absent = parsed.Rows.Count(r => r.Kind == ScoreMatchKind.Absent);
        Check("两个有分的匹配上", matched == 2, matched.ToString());
        Check("缺考被单独识别", absent == 1, absent.ToString());
        Check("合计行被跳过", parsed.Rows.Where(r => r.Kind == ScoreMatchKind.Matched).All(r => !r.CleanName.Contains("合计")));
        Check("第一名分数正确", parsed.Rows.First(r => r.CleanName == "杨欣怡").Score == 712);

        Check("缺考标记识别", ScoreSheetParser.IsAbsentMark("缺考") && ScoreSheetParser.IsAbsentMark("—") && !ScoreSheetParser.IsAbsentMark("88"));
        Check("分数解析", ScoreSheetParser.TryParseScore("688.5", out var s) && System.Math.Abs(s - 688.5) < 1e-9);
        Check("非数字不当作分数", !ScoreSheetParser.TryParseScore("优秀", out _));

        // 名单里没有的名字：不猜、不硬塞，交给人处理
        var strangerGrid = new SheetGrid("S", new List<IReadOnlyList<string>>
        {
            new[] { "姓名", "总分" },
            new[] { "查无此人", "600" },
        });
        var stranger = ScoreSheetParser.Parse(strangerGrid, students);
        Check("名单外的人不会被悄悄塞进来", stranger.Rows.All(r => r.Kind != ScoreMatchKind.Matched));
    }

    // ---------------- CSV ----------------

    private static void CsvTests()
    {
        var rows = CsvFile.ParseCsv("姓名,总分\n\"杨欣怡\",712\n陈芷若,688\n");
        Check("CSV 行数正确", rows.Count == 3, rows.Count.ToString());
        Check("引号内的逗号不当分隔", rows[1][0] == "杨欣怡" && rows[1][1] == "712");
        Check("表头读出中文不炸", rows[0][0] == "姓名");
    }
}
