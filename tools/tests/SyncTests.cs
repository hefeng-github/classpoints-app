using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Data.Sync;

namespace ClassPoints.Tests;

/// <summary>
/// 端到端同步：两台"设备"各自一个本地库，中间过一遍真的 Worker。
/// 这是唯一能证明"推送格式和云端契约对得上"的测试 ——
/// 单测只能证明本地算得对，证明不了线上那一半没被打断。
/// </summary>
internal static class SyncTests
{
    private sealed class Device : IDisposable
    {
        public Device(string dir)
        {
            Dir = dir;
            Db = new Database(Path.Combine(dir, "classpoints.db"));
            Db.Initialize();
            Outbox = new SyncOutbox(Db);
            Students = new StudentRepository(Db);
            Rules = new RuleRepository(Db);
            Members = new MemberRepository(Db);
            Periods = new PeriodRepository(Db);
            Entries = new EntryService(Db, new AuditRepository(Db));
            Exams = new ExamRepository(Db);
            Sync = new SyncService(Db, Outbox, new SyncStore(Path.Combine(dir, "sync.json")));
        }

        public string Dir { get; }
        public Database Db { get; }
        public SyncOutbox Outbox { get; }
        public StudentRepository Students { get; }
        public RuleRepository Rules { get; }
        public MemberRepository Members { get; }
        public PeriodRepository Periods { get; }
        public EntryService Entries { get; }
        public ExamRepository Exams { get; }
        public SyncService Sync { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, true);
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响结论
            }
        }
    }

    public static async Task RunAsync(string baseUrl, Action<string, bool, string> report)
    {
        // 断言工具的本地别名：Detail 可省，调用处才不至于到处写空字符串
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var root = Path.Combine(Path.GetTempPath(), "cp-sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        using var a = new Device(Path.Combine(root, "a"));
        using var b = new Device(Path.Combine(root, "b"));

        var periodA = a.Periods.Rollover("2026 秋季学期", newTerm: true);
        var ruleA = a.Rules.Save(new Rule { Label = "回答问题", Delta = 5, Icon = "✋" });
        var ownerA = a.Members.Save(new Member { DisplayName = "张老师", Role = MemberRole.Owner }, pin: "123456");

        var students = new[] { "杨欣怡", "陈芷若", "刘书豪" }
            .Select(n => a.Students.Insert(new Student { Name = n }))
            .ToList();

        var award = a.Entries.ApplyBatch(
            students.Select(s => new AwardLine(s.Id, ruleA, 5)).ToList(),
            ownerA, "pin", periodA.Id);
        check("本地先记账成功（不等网络）", award.Count == 3 && a.Outbox.Count() > 0,
            $"entries={award.Count} outbox={a.Outbox.Count()}");

        // ---- 建班 ----
        var created = await a.Sync.CreateClassAsync(baseUrl, "高二 (3) 班", "张老师", "123456", "REC-8888", "desk");
        var code = created.CreatedCode ?? "";
        check("建班拿到班级码", created.Ok && code.Length == 8, code);

        var pushA = await a.Sync.RunOnceAsync();
        check("第一轮推送成功", pushA.Ok, pushA.Error ?? "");
        if (!pushA.Ok) Console.WriteLine("      push error: " + pushA.Error);
        check("推完队列清空", a.Outbox.Count() == 0, a.Outbox.Count().ToString());
        check("确实推了东西", pushA.Pushed >= 3, pushA.Pushed.ToString());

        // ---- 另一端配对 + 拉取 ----
        var paired = await b.Sync.PairAsync(baseUrl, code, "123456", "desk");
        check("第二台设备配对成功", paired.Ok, paired.Error ?? "");

        var pullB = await b.Sync.RunOnceAsync();
        check("第二轮拉取成功", pullB.Ok, pullB.Error ?? "");
        var mine = b.Students.ListActive().Where(st => students.Any(x => x.Id == st.Id)).ToList();
        check("名单拉过来了", mine.Count == 3, mine.Count.ToString());

        var pulledPeriod = b.Periods.Active();
        check("周期拉过来了", pulledPeriod is not null);
        // D1 是全库共享的（一个部署一个班），多次跑测试会累积别的班的数据，
        // 所以只数本次造出来的那几个人
        var ids = students.Select(x => x.Id).ToHashSet();
        List<Entry> MineEntries() => pulledPeriod is null
            ? []
            : b.Entries.Entries(pulledPeriod.Id).Where(e => ids.Contains(e.StudentId)).ToList();
        var pulledEntries = MineEntries();
        check("流水拉过来了", pulledEntries.Count == 3, pulledEntries.Count.ToString());
        check("积分合计对得上", pulledEntries.Sum(e => e.Delta) == 15, pulledEntries.Sum(e => e.Delta).ToString());
        check("规则拉过来了", b.Rules.ListAll().Any(r => r.Label == "回答问题"));

        // ---- 幂等：重推一次不应该多出流水 ----
        var replay = await a.Sync.RunOnceAsync();
        var afterReplay = await b.Sync.RunOnceAsync();
        var countAgain = MineEntries().Count;
        check("重跑同步不会重复计分", countAgain == 3, $"after={countAgain} replay={replay.Ok}/{afterReplay.Ok}");

        // ---- 成绩：一场考试 + 全部分数，整批一个事务 ----
        var imported = a.Exams.ImportScores(periodA.Id, "期中考试", 750, "2026-11-01",
        [
            (students[0].Id, 712d, false),
            (students[1].Id, 688d, false),
            (students[2].Id, null, true),
        ], "期中.xlsx");
        check("成绩本地导入", imported.Written == 2 && imported.Absent == 1);

        var pushExam = await a.Sync.RunOnceAsync();
        check("成绩推送成功", pushExam.Ok, pushExam.Error ?? "");

        var pullExam = await b.Sync.RunOnceAsync();
        var examB = b.Exams.ListExams(pulledPeriod!.Id).FirstOrDefault(e => e.Name == "期中考试");
        check("考试拉过来了", examB is not null);
        if (examB is not null)
        {
            var scoresB = b.Exams.Scores(examB.Id);
            check("成绩条数对得上", scoresB.Count == 3, scoresB.Count.ToString());
            check("缺考不存 0 分", scoresB.Count(s => s.Absent) == 1 && scoresB.First(s => s.Absent).Score is null);
        }

        // ---- 撤销：冲销行也要同步过去 ----
        var reversed = a.Entries.ReverseSingle(award.Entries[0].Id, ownerA, "pin");
        check("本地撤销成了一条冲销行", reversed > 0);

        var pushReverse = await a.Sync.RunOnceAsync();
        check("撤销推送成功", pushReverse.Ok, pushReverse.Error ?? "");
        var pullReverse = await b.Sync.RunOnceAsync();
        check("撤销推送成功（拉取那侧也正常）", pullReverse.Ok, pullReverse.Error ?? "");

        // 界面上看不到冲销行（列表按设计把 reverses_entry_id 非空的过滤掉了），
        // 所以这里直接查库：冲销行到没到、原行有没有被打上 reversed_at。
        using var connB = b.Db.Open();
        var reversalInB = Convert.ToInt32(Database.Scalar(connB,
            "SELECT COUNT(*) FROM entries WHERE reverses_entry_id = $id", ("$id", award.Entries[0].Id)));
        var reversedAtInB = Database.Scalar(connB,
            "SELECT reversed_at FROM entries WHERE id = $id", ("$id", award.Entries[0].Id));
        var totalAfter = Convert.ToInt32(Database.Scalar(connB,
            $"SELECT COALESCE(SUM(delta),0) FROM entries WHERE period_id = $p AND student_id IN ({string.Join(",", ids.Select((_, i) => "$s" + i))})",
            [("$p", pulledPeriod!.Id), .. ids.Select((id, i) => ("$s" + i, (object?)id))]));

        check("另一端收到了冲销行", reversalInB == 1, reversalInB.ToString());
        check("原行在另一端被标记为已撤销", reversedAtInB is long, reversedAtInB?.ToString() ?? "null");
        check("另一端的合计也跟着抵回来了", totalAfter == 10, totalAfter.ToString());

        // ---- 断网：没配对时同步必须安静地跳过，队列一条都不许丢 ----
        using var offline = new Device(Path.Combine(root, "c"));
        offline.Outbox.Enqueue("probe:" + Guid.NewGuid().ToString("N"), "student",
            new { action = "upsert", student = new Student { Id = "probe", Name = "断网探针" } });
        var brokenPair = new SyncService(offline.Db, offline.Outbox,
            new SyncStore(Path.Combine(root, "c", "sync.json")));
        var offlineReport = await brokenPair.RunOnceAsync();
        check("没配对时同步直接跳过、不抛异常", !offlineReport.Ok && offline.Outbox.Count() == 1);
        check("跳过原因说得清楚", offlineReport.Error is not null, offlineReport.Error ?? "");

        Console.WriteLine($"  . 测试目录：{root}");
    }
}
