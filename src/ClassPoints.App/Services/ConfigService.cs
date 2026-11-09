using ClassPoints.Core;
using ClassPoints.Core.Config;
using ClassPoints.Data.Config;
using ClassPoints.Data.Sync;

namespace ClassPoints.App.Services;

/// <summary>应用结果：给界面一句人话，外加能不能继续。</summary>
public sealed record ConfigApplyResult(bool Ok, string Message, string? Pulled)
{
    public static ConfigApplyResult Fail(string message) => new(false, message, null);
}

/// <summary>
/// 预配置包的家务活。
///
/// 「家里配基础设施、学校只配分数」这条流程由三个动作拼成：
/// 导出（在家，带上云端地址与班级码，口令把配对密码锁住）→ 拷过去 → 导入（在学校，
/// 一次把班级名、座位、同步全配好，然后向导直接从"贴名单"开始）。
/// </summary>
public sealed class ConfigService(AppServices services)
{
    /// <summary>当前这份安装能导出什么。没配对过就没有云端部分。</summary>
    public ConfigBundle Build(string pairingPin, string passphrase, string note)
    {
        var credentials = new SyncStore().Load();
        var active = services.Periods.Active();

        var bundle = new ConfigBundle
        {
            CreatedAt = Clock.NowMs(),
            Note = note,
            ClassName = services.Settings.ClassName,
            TermKey = active?.TermKey ?? "",
            SeatRows = services.Settings.SeatRows,
            SeatCols = services.Settings.SeatCols,
            Sync = credentials is null
                ? null
                : new ConfigSync { BaseUrl = credentials.BaseUrl, ClassCode = credentials.ClassCode },
        };

        if (!string.IsNullOrWhiteSpace(pairingPin))
            bundle.Protect(pairingPin.Trim(), passphrase);

        return bundle;
    }

    /// <summary>给人看的摘要，导出前确认、导入前预览都用它。</summary>
    public string Describe(ConfigBundle bundle)
    {
        var sync = bundle.Sync is null
            ? "未连接云端（纯本地）"
            : $"{bundle.Sync.BaseUrl} · 班级码 {bundle.Sync.ClassCode}";

        var secret = bundle.NeedsPassphrase ? "含配对口令（口令加密）" : "不含配对口令";
        var term = string.IsNullOrWhiteSpace(bundle.TermKey) ? "未指定" : bundle.TermKey;

        return $"{bundle.ClassName} · 学期 {term} · 座位 {bundle.SeatRows} 排 × {bundle.SeatCols} 座\n" +
               $"{sync}\n{secret}";
    }

    /// <summary>
    /// 把配置包落到这台机器：先写本地设置，再拿班级码 + 口令去云端换令牌。
    /// 口令错、地址不通都会抛 ConfigException，界面直接把话说给老师听。
    /// </summary>
    public async Task<ConfigApplyResult> ApplyAsync(ConfigBundle bundle, string? passphrase,
        string deviceKind, CancellationToken ct = default)
    {
        var problems = bundle.Validate();
        if (problems.Count > 0) throw new ConfigException(string.Join("；", problems));

        // 先解密：口令不对就别改任何设置，免得留下半套配置
        var pin = bundle.RevealPin(passphrase);

        services.Settings.ClassName = bundle.ClassName;
        services.Settings.SeatRows = bundle.SeatRows;
        services.Settings.SeatCols = bundle.SeatCols;

        if (bundle.Sync is not { } sync)
            return new ConfigApplyResult(true, $"已套用「{bundle.ClassName}」的本地配置。", null);

        services.Settings.ClassCode = sync.ClassCode;

        if (string.IsNullOrEmpty(pin))
            return new ConfigApplyResult(true,
                $"已套用「{bundle.ClassName}」。包里没有配对口令，去「设置 → 同步」手动配对。", null);

        try
        {
            var report = await services.Sync.PairAsync(sync.BaseUrl, sync.ClassCode, pin, deviceKind, ct);
            if (!report.Ok)
                return ConfigApplyResult.Fail("本机配置已写入，但云端配对失败：" + (report.Error ?? "未知原因"));

            var pull = await services.Sync.RunOnceAsync(ct: ct);
            services.Pump.Start();

            var pulled = pull.Ok
                ? $"拉取 {pull.Pulled} 条"
                : $"拉取失败（{pull.Error}），稍后会自动重试";

            return new ConfigApplyResult(true, $"已连接 {sync.BaseUrl}，班级码 {sync.ClassCode}。", pulled);
        }
        catch (Exception ex) when (ex is not ConfigException)
        {
            return ConfigApplyResult.Fail("本机配置已写入，但云端配对失败：" + ex.Message);
        }
    }

    /// <summary>导出后顺手留一份在数据目录，换 U 盘也不丢。</summary>
    public static string? SaveAndKeep(ConfigBundle bundle, string path)
    {
        ConfigBundleFile.Save(bundle, path);
        ConfigBundleFile.KeepCopy(bundle, path);
        return path;
    }
}
