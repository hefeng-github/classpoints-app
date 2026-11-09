using ClassPoints.Core.Config;

namespace ClassPoints.Tests;

/// <summary>
/// 预配置包的测试。重点不是"能存能读"，而是三件会真出事的事：
/// ① 口令不对必须明确报错，而不是静默给个空密码去配对（那会被云端当成密码错，老师查半天）；
/// ② 文件被改过必须拒绝（AES-GCM 的 tag 校验就是干这个的）；
/// ③ 不设口令时文件里不能出现明文密码。
/// </summary>
internal static class ConfigTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        // ---- 不带密码：最轻的一份配置，家里没连云端时就是这样 ----
        var plain = new ConfigBundle
        {
            ClassName = "初二（3）班",
            TermKey = "2026-AUT",
            SeatRows = 6,
            SeatCols = 8,
            Note = "育才中学",
        };
        plain.Sync = new ConfigSync { BaseUrl = "https://cp.example.workers.dev", ClassCode = "A1B2C3D4" };

        var json = plain.ToJson();
        var back = ConfigBundle.Parse(json);

        Check("配置包往返不丢字段",
            back.ClassName == "初二（3）班" && back.SeatRows == 6 && back.Sync?.ClassCode == "A1B2C3D4",
            back.ClassName);
        Check("中文备注不被转义成 \\u",
            json.Contains("育才中学"), json.Contains("\\u4f1a") ? "被转义了" : "");
        Check("没有密码时不要求口令", !back.NeedsPassphrase);
        Check("没有密码时 RevealPin 返回 null", back.RevealPin(null) is null);
        Check("合法配置包校验通过", back.Validate().Count == 0, string.Join("；", back.Validate()));

        // ---- 带密码：口令加密，文件里不能出现明文 ----
        var secret = new ConfigBundle
        {
            ClassName = "初二（3）班",
            Sync = new ConfigSync { BaseUrl = "https://cp.example.workers.dev", ClassCode = "A1B2C3D4" },
        };
        secret.Protect("246810", "banzhuren2026");

        Check("加密后文件里没有明文密码", !secret.ToJson().Contains("246810"));
        Check("加密后要求口令", secret.NeedsPassphrase);
        Check("正确口令能取回密码", secret.RevealPin("banzhuren2026") == "246810");

        // 每次加密的 salt/iv 都是随机的，所以同一份配置两次导出，密文不该一样
        var secret2 = new ConfigBundle { ClassName = "初二（3）班" };
        secret2.Protect("246810", "banzhuren2026");
        Check("每次加密结果不同（salt/iv 随机）", secret2.Secret!.Ct != secret.Secret!.Ct);

        var wrong = Catch(() => secret.RevealPin("wrong-pass"));
        Check("错口令给出可读的报错", wrong is ConfigException && wrong.Message.Contains("口令"),
            wrong?.Message ?? "没有抛异常");

        var missing = Catch(() => secret.RevealPin(null));
        Check("该要口令却没给时明确提示", missing is ConfigException && missing.Message.Contains("口令"),
            missing?.Message ?? "没有抛异常");

        // 篡改：把密文最后一位改掉，GCM 校验必须失败
        var tampered = ConfigBundle.Parse(secret.ToJson());
        var blob = Convert.FromBase64String(tampered.Secret!.Ct);
        blob[^1] ^= 0xFF;
        tampered.Secret.Ct = Convert.ToBase64String(blob);
        var bad = Catch(() => tampered.RevealPin("banzhuren2026"));
        Check("密文被改动会被发现", bad is ConfigException, bad?.Message ?? "居然解开了");

        // ---- 校验：把常见的手误挡在导入之前 ----
        var noName = new ConfigBundle { ClassName = "", SeatRows = 6, SeatCols = 8 };
        Check("缺班级名会被拦下", noName.Validate().Any(p => p.Contains("班级")));

        var badSeat = new ConfigBundle { ClassName = "初二（3）班", SeatRows = 30, SeatCols = 2 };
        Check("座位数越界会被拦下", badSeat.Validate().Count >= 2, string.Join("；", badSeat.Validate()));

        var badUrl = new ConfigBundle { ClassName = "初二（3）班" };
        badUrl.Sync = new ConfigSync { BaseUrl = "cp.example.workers.dev", ClassCode = "A1B2C3D4" };
        Check("云端地址不是绝对地址会被拦下", badUrl.Validate().Any(p => p.Contains("地址")));

        var badCode = new ConfigBundle { ClassName = "初二（3）班" };
        badCode.Sync = new ConfigSync { BaseUrl = "https://cp.example.workers.dev", ClassCode = "12" };
        Check("班级码太短会被拦下", badCode.Validate().Any(p => p.Contains("班级码")));

        // ---- 文件级：类型字段不对、版本太新 ----
        var wrongKind = Catch(() => ConfigBundle.Parse("""{"v":1,"kind":"something.else","className":"甲"}"""));
        Check("别的 JSON 不会被当成配置包", wrongKind is ConfigException, wrongKind?.Message ?? "居然解析成功");

        var tooNew = Catch(() => ConfigBundle.Parse("""{"v":99,"kind":"classpoints.config","className":"甲"}"""));
        Check("更新版本导出的包会提示升级", tooNew is ConfigException && tooNew.Message.Contains("升级"),
            tooNew?.Message ?? "");

        var garbage = Catch(() => ConfigBundle.Parse("not json at all"));
        Check("乱码文件给出可读报错", garbage is ConfigException, garbage?.Message ?? "没有抛异常");
    }

    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
