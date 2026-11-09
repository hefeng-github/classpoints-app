using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Data.Sync;

namespace ClassPoints.App.Services;

/// <summary>
/// 启动期参数。只剩一个 --db（换库路径，给测试和"两台机器共用一份库"用）。
///
/// 形态（大屏/电脑/只读）和预配置包以前也是参数，现在都归首次启动向导管：
/// 老师不会记命令行，而参数一旦写死在快捷方式里，换台机器就会静默进错形态。
/// CP_MODE / CP_SNAP 这两个环境变量只服务验收脚本，不参与正常业务。
/// </summary>
public sealed class StartupOptions
{
    public string? DbPath { get; init; }

    /// <summary>验收脚本用：绕过设置强制某个形态，只影响这一次运行，不写库。</summary>
    public AppMode? ModeOverride { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        string? dbPath = null;

        foreach (var raw in args)
        {
            var arg = raw.Trim().TrimStart('-', '/');
            var parts = arg.Split('=', 2);
            if (!parts[0].Equals("db", StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Length > 1) dbPath = parts[1];
        }

        return new StartupOptions
        {
            DbPath = dbPath,
            ModeOverride = AppModeNames.Parse(Environment.GetEnvironmentVariable("CP_MODE")),
        };
    }
}

/// <summary>
/// 组合根。界面只从这里拿东西，不自己 new 仓储 —— 换数据库、换读卡器只改这一个文件。
/// </summary>
public sealed class AppServices
{
    private AppServices(Database db)
    {
        Db = db;
        Outbox = new SyncOutbox(db);
        Audit = new AuditRepository(db);
        Students = new StudentRepository(db);
        Rules = new RuleRepository(db);
        Counters = new CounterRepository(db);
        Members = new MemberRepository(db);
        Notes = new NoteTemplateRepository(db);
        Periods = new PeriodRepository(db);
        Entries = new EntryService(db, Audit);
        Exams = new ExamRepository(db);
        Backups = new BackupService(db);
        Settings = new AppSettings(db);
        Sync = new SyncService(db, Outbox);
        Config = new ConfigService(this);
        Pump = new SyncPump(this);
        Reader = new Pn532Serial();
    }

    public static AppServices Instance { get; private set; } = null!;

    public Database Db { get; }
    public SyncOutbox Outbox { get; }
    public AuditRepository Audit { get; }
    public StudentRepository Students { get; }
    public RuleRepository Rules { get; }
    public CounterRepository Counters { get; }
    public MemberRepository Members { get; }
    public NoteTemplateRepository Notes { get; }
    public PeriodRepository Periods { get; }
    public EntryService Entries { get; }
    public ExamRepository Exams { get; }
    public BackupService Backups { get; }
    public AppSettings Settings { get; }
    public SyncService Sync { get; }
    public ConfigService Config { get; }
    public SyncPump Pump { get; }
    public Pn532Serial Reader { get; }
    public BoardSession Session { get; private set; } = null!;

    public static AppServices Initialize(StartupOptions options)
    {
        var db = new Database(options.DbPath);
        db.Initialize();

        var services = new AppServices(db);
        services.Session = new BoardSession(services.Members);
        Instance = services;

        services.Log($"启动 db={db.DbPath} schema={Sql.SchemaVersion} mode={services.Settings.AppMode?.ToDb() ?? "(未选)"}");
        return services;
    }

    // ---------------- 常用查询 ----------------

    public Period CurrentPeriod => Periods.Active() ?? new Period
    {
        Id = "p_fallback",
        Name = "当前周期",
        TermKey = Database.TermKeyFor(DateTime.Now),
        StartAt = Clock.NowMs(),
    };

    /// <summary>界面上的版本号。只在这一个地方写，省得侧栏写 1.1、设置页写 1.0。</summary>
    public const string AppVersion = "1.1";

    public string ClassName =>
        string.IsNullOrWhiteSpace(Settings.ClassName) ? "我的班级" : Settings.ClassName;

    public string ClassTitle
    {
        get
        {
            var name = ClassName;
            var period = Periods.Active();
            return period is null ? name : $"{name} · {period.Name}";
        }
    }

    /// <summary>当前周期名。大屏看板的标题里已经在显班级名了，副标题只要周期，不然一行里班级名出现两遍。</summary>
    public string PeriodName => Periods.Active()?.Name ?? "";

    /// <summary>大屏上任何写操作的操作者。未认人时不该走到这里，兜底用一个明确的占位。</summary>
    public Member Operator => Session.Current ?? DeskOperator;

    private Member? _deskOperator;

    /// <summary>电脑端免密：所有记录记在班主任名下。</summary>
    public Member DeskOperator
    {
        get
        {
            if (_deskOperator is not null) return _deskOperator;
            var owner = Members.Owner();
            if (owner is not null) return _deskOperator = owner;

            _deskOperator = Members.Save(new Member
            {
                DisplayName = "班主任",
                Role = MemberRole.Owner,
            }, pin: null);
            return _deskOperator;
        }
    }

    public int StudentCount => Students.ListActive().Count;

    /// <summary>当前周期总分。</summary>
    public Dictionary<string, int> PeriodTotals() => Entries.PeriodTotals(CurrentPeriod.Id);

    /// <summary>学期累计积分（总评用这个，不是当前周期）。</summary>
    public Dictionary<string, int> TermTotals() => Entries.TermTotals(CurrentPeriod.TermKey);

    /// <summary>导出/打印需要的整包材料。</summary>
    public ExportBundle BuildExportBundle(AbsencePolicy? policy = null, IReadOnlyList<string>? warnings = null)
    {
        var period = CurrentPeriod;
        var scheme = Exams.ActiveScheme();
        var students = Students.ListActive();
        var termPoints = Entries.TermTotals(period.TermKey);
        var usePolicy = policy ?? Settings.AbsencePolicy;
        var ranking = Exams.BuildRanking(students, termPoints, period.TermKey, scheme, usePolicy);

        return new ExportBundle(
            ranking,
            Exams.ListExams(period.Id),
            Exams.ScoresForTerm(period.TermKey, Exams.ListExams(period.Id).Select(e => e.Id).ToList()),
            Entries.Entries(period.Id, limit: 100_000),
            scheme,
            period,
            usePolicy,
            period.TermKey,
            students,
            Exams.Weights(scheme.Id),
            DeskOperator.DisplayName,
            DateTime.Now,
            warnings);
    }

    public PrintContext BuildPrintContext()
    {
        var period = CurrentPeriod;
        return new PrintContext(
            ClassName,
            period.Name,
            period.RangeText,
            Exams.ActiveScheme().FormulaText,
            DeskOperator.DisplayName,
            DateTime.Now,
            Crypto.RandomHex(3).ToUpperInvariant());
    }

    // ---------------- 日志 ----------------

    private static readonly object LogGate = new();

    public void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Database.LogDir);
            var path = Path.Combine(Database.LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (LogGate)
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志写不进去不能影响主流程
        }
    }

    public void LogError(string where, Exception ex) =>
        Log($"ERROR {where}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    /// <summary>每天首次启动 + 每 50 次入账各备份一次。失败不抛，界面只提示。</summary>
    public string? TryAutoBackup(string reason)
    {
        try
        {
            var last = Settings.LastBackupAt;
            var lastDate = last <= 0 ? DateTime.MinValue : Clock.LocalDate(last).Date;
            if (reason == "startup" && lastDate == DateTime.Today) return null;

            var path = Backups.BackupNow(reason, null);
            Log($"自动备份完成 {path}");
            return path;
        }
        catch (Exception ex)
        {
            LogError("auto-backup", ex);
            return null;
        }
    }
}
