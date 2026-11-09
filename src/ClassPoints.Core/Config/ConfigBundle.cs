using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassPoints.Core.Config;

/// <summary>配置包格式不对、口令不对、或者文件被人改过时抛这个。</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>云端部分：地址 + 班级码。这两个不算秘密，家里配好直接下发。</summary>
public sealed class ConfigSync
{
    public string BaseUrl { get; set; } = "";
    public string ClassCode { get; set; } = "";
}

/// <summary>
/// 配对口令的密文。用口令派生密钥加密，文件丢了别人也拿不到班里那 6 位密码。
/// </summary>
public sealed class ConfigSecret
{
    public string Alg { get; set; } = "A256GCM";
    public string Kdf { get; set; } = "PBKDF2-SHA256";
    public int Iter { get; set; } = 120_000;
    public string Salt { get; set; } = "";
    public string Iv { get; set; } = "";
    public string Ct { get; set; } = "";
}

/// <summary>
/// 预配置包（.cpcfg）。
///
/// 存在的理由：家里有网、有电脑，学校那台一体机可能连不上网也不好打字。
/// 所以基础设施（云端地址、班级码、班级名、学期、座位布局）在家里配一次，
/// 存成一个文件带到学校；到学校只需要做和分数有关的事：贴名单、定分值、配权重。
///
/// 文件里**不含**任何账号令牌：令牌是机器绑定的，拷到另一台机器必然失效。
/// 带的是「班级码 + 配对口令（口令加密）」，到了学校由目标机器自己去换令牌。
/// </summary>
public sealed class ConfigBundle
{
    public const string KindName = "classpoints.config";
    public const int CurrentVersion = 1;
    public const string Extension = ".cpcfg";

    public int V { get; set; } = CurrentVersion;
    public string Kind { get; set; } = KindName;
    public long CreatedAt { get; set; }

    /// <summary>给人看的备注，比如「育才中学 初二(3)班」。</summary>
    public string Note { get; set; } = "";

    public string ClassName { get; set; } = "";
    public string TermKey { get; set; } = "";
    public int SeatRows { get; set; } = 6;
    public int SeatCols { get; set; } = 8;

    public ConfigSync? Sync { get; set; }
    public ConfigSecret? Secret { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ConfigBundle Parse(string json)
    {
        ConfigBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<ConfigBundle>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("这不是一个配置包文件（JSON 读不了）：" + ex.Message);
        }

        if (bundle is null) throw new ConfigException("配置文件是空的。");
        if (bundle.Kind != KindName) throw new ConfigException($"文件类型不对，期望 {KindName}，实际是「{bundle.Kind}」。");
        if (bundle.V > CurrentVersion)
            throw new ConfigException($"这个配置包来自更新的版本（v{bundle.V}），请先升级程序。");

        return bundle;
    }

    /// <summary>把配对口令用口令加密放进文件。没有口令就不加密，此时文件里没有密码。</summary>
    public void Protect(string pairingPin, string passphrase)
    {
        if (string.IsNullOrEmpty(pairingPin)) return;
        if (passphrase.Length < 6) throw new ConfigException("配置包口令至少 6 位。");

        var salt = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(12);
        var key = Derive(passphrase, salt, 120_000);

        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new PinBox { Pin = pairingPin }, Json));
        var ct = new byte[plain.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(iv, plain, ct, tag);

        Secret = new ConfigSecret
        {
            Alg = "A256GCM",
            Kdf = "PBKDF2-SHA256",
            Iter = 120_000,
            Salt = Convert.ToBase64String(salt),
            Iv = Convert.ToBase64String(iv),
            Ct = Convert.ToBase64String([.. ct, .. tag]),
        };
    }

    /// <summary>取出配对口令。文件里没加密口令时返回 null（说明不需要口令）。</summary>
    public string? RevealPin(string? passphrase)
    {
        if (Secret is null) return null;

        if (string.IsNullOrEmpty(passphrase))
            throw new ConfigException("这个配置包设了口令，请先输入口令。");
        if (Secret.Alg != "A256GCM" || Secret.Kdf != "PBKDF2-SHA256")
            throw new ConfigException($"不认识的加密方式：{Secret.Alg}/{Secret.Kdf}。");

        byte[] salt, iv, blob;
        try
        {
            salt = Convert.FromBase64String(Secret.Salt);
            iv = Convert.FromBase64String(Secret.Iv);
            blob = Convert.FromBase64String(Secret.Ct);
        }
        catch (FormatException)
        {
            throw new ConfigException("配置包里的加密数据损坏了，请重新导出一份。");
        }

        if (blob.Length < 17) throw new ConfigException("配置包里的加密数据不完整。");

        var key = Derive(passphrase, salt, Secret.Iter);
        var ct = blob[..^16];
        var tag = blob[^16..];
        var plain = new byte[ct.Length];

        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(iv, ct, tag, plain);
        }
        catch (CryptographicException)
        {
            // GCM 校验失败只有两种可能：口令错，或文件被改过
            throw new ConfigException("口令不对（或配置包被修改过），请重新输入。");
        }

        var box = JsonSerializer.Deserialize<PinBox>(plain, Json);
        return box?.Pin ?? "";
    }

    [JsonIgnore]
    public bool NeedsPassphrase => Secret is not null;

    /// <summary>回来一串人话，空表示没问题。</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(ClassName))
            problems.Add("缺班级名称。");
        if (SeatRows is < 2 or > 10)
            problems.Add($"座位排数 {SeatRows} 超出范围（2–10）。");
        if (SeatCols is < 4 or > 12)
            problems.Add($"座位列数 {SeatCols} 超出范围（4–12）。");

        if (Sync is not null)
        {
            if (!Uri.TryCreate(Sync.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                problems.Add("云端地址不是合法的 http/https 地址。");

            var code = (Sync.ClassCode ?? "").Trim();
            if (code.Length is < 6 or > 12)
                problems.Add("班级码长度不对（6–12 位）。");
        }

        return problems;
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    private sealed class PinBox
    {
        public string Pin { get; set; } = "";
    }
}
