using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Services;

/// <summary>设置的强类型门面。所有 key 只在这里出现一次，界面里不再出现裸字符串。</summary>
public sealed class AppSettings(Database db)
{
    private string? _cachedClassCode;

    public string ClassName
    {
        get => db.GetSetting("class_name") ?? "";
        set => db.SetSetting("class_name", value);
    }

    /// <summary>6 位班级码：建班时生成，本机显示用；云端未接线时它只是给人看的一个标识。</summary>
    public string ClassCode
    {
        get
        {
            var v = db.GetSetting("class_code");
            if (!string.IsNullOrWhiteSpace(v)) return v!;
            if (_cachedClassCode is not null) return _cachedClassCode;
            _cachedClassCode = Random.Shared.Next(100000, 999999).ToString();
            db.SetSetting("class_code", _cachedClassCode);
            return _cachedClassCode;
        }
        set => db.SetSetting("class_code", value);
    }

    public int SeatCols
    {
        get => System.Math.Clamp(db.GetIntSetting("seat_cols", 8), 4, 12);
        set => db.SetSetting("seat_cols", System.Math.Clamp(value, 4, 12).ToString());
    }

    public int SeatRows
    {
        get => System.Math.Clamp(db.GetIntSetting("seat_rows", 6), 2, 10);
        set => db.SetSetting("seat_rows", System.Math.Clamp(value, 2, 10).ToString());
    }

    /// <summary>班委会话：30 秒无操作回看板。有选中项时不计时（方案 9.9）。</summary>
    public int IdleTimeoutSec
    {
        get => System.Math.Clamp(db.GetIntSetting("idle_timeout_sec", 30), 10, 600);
        set => db.SetSetting("idle_timeout_sec", value.ToString());
    }

    /// <summary>管理模式：5 分钟无操作只退管理态，不退身份。</summary>
    public int LockTimeoutSec
    {
        get => System.Math.Clamp(db.GetIntSetting("lock_timeout_sec", 300), 60, 3600);
        set => db.SetSetting("lock_timeout_sec", value.ToString());
    }

    /// <summary>查分页：30 秒严格计时，退出时清屏。</summary>
    public int LookupTimeoutSec
    {
        get => System.Math.Clamp(db.GetIntSetting("lookup_timeout_sec", 30), 10, 300);
        set => db.SetSetting("lookup_timeout_sec", value.ToString());
    }

    public bool SoundOn
    {
        get => db.GetBoolSetting("sound_on", true);
        set => db.SetSetting("sound_on", value ? "1" : "0");
    }

    public int BoardRotateSec
    {
        get => System.Math.Clamp(db.GetIntSetting("board_rotate_sec", 20), 8, 120);
        set => db.SetSetting("board_rotate_sec", value.ToString());
    }

    public bool OnboardingDone
    {
        get => db.GetBoolSetting("onboarding_done", false);
        set => db.SetSetting("onboarding_done", value ? "1" : "0");
    }

    /// <summary>
    /// 这台机器的角色（大屏 / 电脑 / 只读）。null = 还没选过，启动时进向导。
    /// 只写一次错一次的那类设置：改它等于改整台机器的用法，所以要重跑向导。
    /// </summary>
    public AppMode? AppMode
    {
        get => AppModeNames.Parse(db.GetSetting("app_mode"));
        set => db.SetSetting("app_mode", value?.ToDb() ?? "");
    }

    /// <summary>
    /// 外观：light（默认）/ dark / system。
    /// 默认浅色是因为教室投影仪普遍偏暗，浅底黑字投出来最清楚；夜里想省眼可以切深色。
    /// </summary>
    public AppTheme Theme
    {
        get => (db.GetSetting("theme") ?? "").ToLowerInvariant() switch
        {
            "dark" => AppTheme.Dark,
            "system" => AppTheme.System,
            _ => AppTheme.Light,
        };
        set => db.SetSetting("theme", value switch
        {
            AppTheme.Dark => "dark",
            AppTheme.System => "system",
            _ => "light",
        });
    }

    public AbsencePolicy AbsencePolicy
    {
        get => ExamRepository.ParsePolicy(db.GetSetting("absence_policy"));
        set => db.SetSetting("absence_policy", ExamRepository.PolicyToDb(value));
    }

    public string ReaderPort
    {
        get => db.GetSetting("reader_port") ?? "";
        set => db.SetSetting("reader_port", value);
    }

    public string RecoveryCodeHash
    {
        get => db.GetSetting("recovery_hash") ?? "";
        set => db.SetSetting("recovery_hash", value);
    }

    /// <summary>恢复码明文只在生成那一刻出现一次，之后只留哈希。</summary>
    public bool HasRecovery => !string.IsNullOrEmpty(RecoveryCodeHash);

    public void SetWindow(double w, double h, int? x, int? y)
    {
        db.SetSetting("window_w", ((int)w).ToString());
        db.SetSetting("window_h", ((int)h).ToString());
        db.SetSetting("window_x", x?.ToString() ?? "");
        db.SetSetting("window_y", y?.ToString() ?? "");
    }

    public (int W, int H, int? X, int? Y) GetWindow()
    {
        var w = db.GetIntSetting("window_w", 0);
        var h = db.GetIntSetting("window_h", 0);
        int? x = int.TryParse(db.GetSetting("window_x"), out var xv) ? xv : null;
        int? y = int.TryParse(db.GetSetting("window_y"), out var yv) ? yv : null;
        return (w, h, x, y);
    }

    /// <summary>上一次备份超过 3 天要提醒，超过 7 天要弹一次。</summary>
    public long LastBackupAt
    {
        get => long.TryParse(db.GetSetting("last_backup_at"), out var v) ? v : 0;
        set => db.SetSetting("last_backup_at", value.ToString());
    }
}
