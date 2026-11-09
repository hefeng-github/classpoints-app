using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 事件计数的测试。重点全在"次数会不会算错"，因为这功能一旦错，老师第二天就不信它了：
/// ① 加和减都要落在同一条 SUM 口径上；
/// ② 减到 0 就不许再减（负数次数没法解释）；
/// ③ 换周期要互不影响（次数是按周期算的）；
/// ④ 图标迁移要幂等，旧库里的 emoji 要变成字形，再跑一次不能再变。
/// </summary>
internal static class CounterTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "cp-counter-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var db = new Database(Path.Combine(dir, "t.db"));
            db.Initialize();

            var counters = new CounterRepository(db);
            var periods = new PeriodRepository(db);
            var students = new StudentRepository(db);
            var op = new Member { Id = "m_owner", DisplayName = "班主任", Role = MemberRole.Owner };

            var period = periods.Active()!;
            var kid = students.Insert(new Student { Name = "王小明", SeatNo = 1 });
            var kid2 = students.Insert(new Student { Name = "李思远", SeatNo = 2 });

            // ---- 预设 ----
            var presets = counters.ListAll();
            Check("默认自带事件类型", presets.Count >= 3, $"实际 {presets.Count} 个");
            Check("预设里有迟到", presets.Any(c => c.Label == "迟到"));
            Check("预设图标已经是字形", presets.All(c => IconCatalog.IsGlyph(c.Icon)),
                string.Join(",", presets.Select(c => c.Icon)));

            var late = presets.First(c => c.Label == "迟到");

            // ---- 加减 ----
            Check("加一次得到 1", counters.AddTick(late.Id, kid.Id, period.Id, 1, op) == 1);
            Check("再加一次得到 2", counters.AddTick(late.Id, kid.Id, period.Id, 1, op) == 2);
            Check("减一次回到 1", counters.AddTick(late.Id, kid.Id, period.Id, -1, op) == 1);
            Check("读到的是同一口径", counters.Total(period.Id, kid.Id, late.Id) == 1);

            counters.AddTick(late.Id, kid2.Id, period.Id, 1, op);
            counters.AddTick(late.Id, kid2.Id, period.Id, 1, op);
            Check("全班合计含两个人", counters.ClassTotal(period.Id, late.Id) == 3,
                counters.ClassTotal(period.Id, late.Id).ToString());

            var map = counters.Counts(period.Id);
            Check("整班次数表按人分开", map.GetValueOrDefault((late.Id, kid.Id)) == 1 &&
                                      map.GetValueOrDefault((late.Id, kid2.Id)) == 2);

            // ---- 不许减成负数 ----
            counters.AddTick(late.Id, kid.Id, period.Id, -1, op);
            var zero = counters.AddTick(late.Id, kid.Id, period.Id, -1, op);
            Check("已经 0 次时再减仍是 0", zero == 0, zero.ToString());
            Check("0 次时不会真的写一条负流水", counters.Total(period.Id, kid.Id, late.Id) == 0);

            // ---- 直接改准确值 ----
            Check("改成 5 次", counters.SetCount(late.Id, kid.Id, period.Id, 5, op) == 5);
            Check("改成 0 次", counters.SetCount(late.Id, kid.Id, period.Id, 0, op) == 0);
            Check("负数被夹到 0", counters.SetCount(late.Id, kid.Id, period.Id, -3, op) == 0);

            // ---- 撤销上一步 ----
            counters.AddTick(late.Id, kid.Id, period.Id, 1, op);
            counters.AddTick(late.Id, kid.Id, period.Id, 1, op);
            var afterUndo = counters.UndoLast(late.Id, kid.Id, period.Id, op);
            Check("撤销跳掉最近一条", afterUndo == 1, afterUndo.ToString());

            // ---- 周期隔离 ----
            var next = periods.Rollover("第二周期", newTerm: false);
            Check("新周期次数归零", counters.Total(next.Id, kid.Id, late.Id) == 0);
            Check("旧周期次数还在", counters.Total(period.Id, kid.Id, late.Id) == 1);

            // ---- 学生维度 ----
            var homework = presets.First(c => c.Label == "作业未交");
            counters.AddTick(homework.Id, kid.Id, period.Id, 1, op);
            var forKid = counters.CountsForStudent(period.Id, kid.Id);
            Check("学生维度能同时读到两类事件",
                forKid.GetValueOrDefault(late.Id) == 1 && forKid.GetValueOrDefault(homework.Id) == 1);

            // ---- 归档 ----
            counters.Archive(homework.Id);
            Check("归档后不在默认列表里", counters.ListAll().All(c => c.Id != homework.Id));
            Check("归档后仍能在全量列表里", counters.ListAll(true).Any(c => c.Id == homework.Id));
            counters.Archive(homework.Id, archived: false);
            Check("恢复归档", counters.ListAll().Any(c => c.Id == homework.Id));

            // ---- 明细 ----
            var ticks = counters.Recent(period.Id, 50);
            Check("明细带出姓名与事件名",
                ticks.Count > 0 && ticks[0].StudentName.Length > 0 && ticks[0].CounterLabel.Length > 0);
            Check("摘要能拼出「迟到 N 次」", counters.SummaryForStudent(period.Id, kid.Id).Contains("迟到"),
                counters.SummaryForStudent(period.Id, kid.Id));

            // ---- 图标迁移：旧库里的 emoji 启动后要变成字形，且可重复执行 ----
            using (var conn = db.Open())
                Database.Exec(conn, "UPDATE rules SET icon = '💬' WHERE id = 'r_speak'");

            db.Initialize();
            var migrated = new RuleRepository(db).Get("r_speak")!;
            Check("旧 emoji 被迁移成字形", IconCatalog.IsGlyph(migrated.Icon), migrated.Icon);
            Check("迁移后再跑一次不变", IconCatalog.Normalize(migrated.Icon) == migrated.Icon);

            // ---- 纯函数：图标归一 ----
            Check("未知字符串回落到默认星标", IconCatalog.Normalize("随便写的") == IconCatalog.Default);
            Check("空值回落到默认星标", IconCatalog.Normalize(null) == IconCatalog.Default);
            Check("字形原样保留", IconCatalog.Normalize(IconCatalog.Flag) == IconCatalog.Flag);
            Check("规则默认图标是字形", IconCatalog.IsGlyph(new Rule().Icon));
        }
        catch (Exception ex)
        {
            Check("事件计数测试未抛异常", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* 临时目录清不掉不影响结论 */ }
        }
    }
}
