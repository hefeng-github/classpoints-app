using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassPoints.Core;

namespace ClassPoints.Data.Sync;

/// <summary>
/// 同步凭据：地址和班级 id 明文，refresh token 用 DPAPI 加密后存文件。
/// access token 只放内存 —— 它 12 小时就过期，落盘没有意义，反而多一个泄漏面。
/// </summary>
public sealed record SyncCredentials(string BaseUrl, string ClassId, string Access, string Refresh, string ClassCode = "")
{
    public bool HasAccess => !string.IsNullOrWhiteSpace(Access);
}

public sealed class SyncStore(string? path = null)
{
    private sealed class Dto
    {
        [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
        [JsonPropertyName("classId")] public string? ClassId { get; set; }
        [JsonPropertyName("refresh")] public string? Refresh { get; set; }
        [JsonPropertyName("classCode")] public string? ClassCode { get; set; }
        [JsonPropertyName("savedAt")] public long SavedAt { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path = path ?? Path.Combine(Database.RootDir, "sync.json");

    /// <summary>内存里的 access token，进程重启就没了，靠 refresh 换新的。</summary>
    private string _access = "";

    public SyncCredentials? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path), Json);
            if (dto?.BaseUrl is null || dto.ClassId is null || dto.Refresh is null) return null;

            var refresh = Unprotect(dto.Refresh);
            if (refresh is null) return null;

            return new SyncCredentials(dto.BaseUrl, dto.ClassId, _access, refresh, dto.ClassCode ?? "");
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(SyncCredentials credentials)
    {
        _access = credentials.Access;
        var dto = new Dto
        {
            BaseUrl = credentials.BaseUrl,
            ClassId = credentials.ClassId,
            Refresh = Protect(credentials.Refresh),
            ClassCode = credentials.ClassCode,
            SavedAt = Clock.NowMs(),
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(dto, Json), Encoding.UTF8);
    }

    public void UpdateAccess(string access) => _access = access;

    public void Clear()
    {
        _access = "";
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception)
        {
            // 删不掉就算了，下次 Save 会覆盖
        }
    }

    private static string? Protect(string plain)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = Dpapi.Protect(Encoding.UTF8.GetBytes(plain));
            return Convert.ToBase64String(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher) || !OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(cipher)));
        }
        catch (Exception)
        {
            // 换了 Windows 用户就解不开 —— 这正是我们要的效果，静默当作没配对
            return null;
        }
    }
}
