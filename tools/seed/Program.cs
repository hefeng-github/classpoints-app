using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Seed;

/// <summary>
/// 演示数据生成器（只用于开发验收，不随产品交付）。
/// 用法：seed &lt;数据库路径&gt; [学生人数]
/// 生成一份可预测的数据：固定随机种子，任何机器上重建结果一致，
/// 这样界面截图之间才有可比性。
/// </summary>
internal static class Program
{
    private static readonly string[] Surnames =
    [
        "王", "李", "张", "刘", "陈", "杨", "黄", "赵", "吴", "周",
        "徐", "孙", "马", "朱", "胡", "郭", "何", "高", "林", "罗",
    ];

    private static readonly string[] Given =
    [
        "子涵", "雨欣", "浩然", "梓萱", "思远", "欣怡", "一鸣", "嘉怡", "宇轩", "梦琪",
        "浩然", "诗涵", "俊杰", "若曦", "博文", "心怡", "天佑", "思彤", "子墨", "雅静",
        "语晨", "书豪", "欣妍", "明轩", "静怡", "泽宇", "雨桐", "家豪", "芷若", "皓轩",
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法: seed <数据库路径> [学生人数]");
            return 2;
        }

        var path = args[0];
        var count = args.Length > 1 ? int.Parse(args[1]) : 45;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);

        var db = new Database(path);
        db.Initialize();

        var outbox = new SyncOutbox(db);
        var students = new StudentRepository(db);
        var rules = new RuleRepository(db);
        var members = new MemberRepository(db);
        var periods = new PeriodRepository(db);
        var audit = new AuditRepository(db);
        var entries = new EntryService(db, audit);

        var period = periods.Active() ?? throw new InvalidOperationException("没有 active 周期");

        // ---- 班级 ----
        db.SetSetting("class_name", "初二 (3) 班");
        db.SetSetting("class_code", "C3-2026");
        db.SetSetting("seat_cols", "8");
        db.SetSetting("seat_rows", "6");
        db.SetSetting("onboarding_done", "1");

        // ---- 名单 ----
        var rnd = new Random(20261108);
        var created = new List<Student>();
        for (var i = 0; i < count; i++)
        {
            var name = Surnames[i % Surnames.Length] + Given[(i * 7 + i / Surnames.Length) % Given.Length];
            var s = students.Insert(new Student
            {
                Name = name,
                StudentNo = (20260000 + i + 1).ToString(),
                SeatNo = i + 1,
                GroupName = $"第 {i / 8 + 1} 组",
                Active = true,
            });
            created.Add(s);
        }

        // ---- 班长与班委（密码在验收说明里写明，仅演示库）----
        var owner = members.Save(new Member { DisplayName = "班主任", Role = MemberRole.Owner }, "123456");
        var officer = members.Save(new Member { DisplayName = "李雨欣", Role = MemberRole.Officer }, "246810");
        members.AddCard(officer.Id, "04A2B3C4D5E680", "班委卡");

        // ---- 流水：最近 18 天，每天 3~9 条 ----
        var ruleList = rules.ListAll();
        var today = DateTime.Now.Date;

        for (var day = 18; day >= 0; day--)
        {
            var date = today.AddDays(-day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            var picks = rnd.Next(3, 10);
            var lines = new List<AwardLine>();
            var used = new HashSet<string>();

            for (var k = 0; k < picks; k++)
            {
                var s = created[rnd.Next(created.Count)];
                if (!used.Add(s.Id)) continue;

                var rule = ruleList[rnd.Next(ruleList.Count)];
                // 八成是加分，两成是扣分（同一加分项按原值反向计入）
                var negative = rnd.NextDouble() < 0.2;
                lines.Add(new AwardLine(s.Id, rule, negative ? -rule.Delta : rule.Delta));
            }

            if (lines.Count == 0) continue;

            var at = Clock.FromLocal(date.AddHours(9 + rnd.Next(0, 7)).AddMinutes(rnd.Next(0, 60)));
            entries.ApplyBatch(lines, owner, "seed", period.Id, at, "演示数据");
        }

        // 今天多记几条，让「今日变化」有数
        var todayLines = new List<AwardLine>();
        foreach (var s in created.Take(6))
            todayLines.Add(new AwardLine(s.Id, ruleList[0], ruleList[0].Delta));
        entries.ApplyBatch(todayLines, owner, "seed", period.Id, null, "演示数据");

        // ---- 事件计数：迟到/作业未交，散布在最近两周 ----
        var counters = new CounterRepository(db);
        var counterList = counters.ListAll();
        if (counterList.Count > 0)
        {
            foreach (var student in created)
            {
                if (rnd.NextDouble() > 0.35) continue;

                var counter = counterList[rnd.Next(counterList.Count)];
                var times = rnd.Next(1, 4);
                for (var k = 0; k < times; k++)
                    counters.AddTick(counter.Id, student.Id, period.Id, 1, owner);
            }
        }

        var total = students.ListAll().Count;
        var entryCount = entries.Entries(period.Id, 5000).Count;
        var tickCount = counters.Recent(period.Id, 5000).Count;
        Console.WriteLine($"已生成演示库：{path}");
        Console.WriteLine($"  学生 {total} 人，流水 {entryCount} 条，事件 {tickCount} 条，周期「{period.Name}」");
        Console.WriteLine("  班主任密码 123456（管理模式），班委密码 246810，班委卡 UID 04A2B3C4D5E680");
        return 0;
    }
}
