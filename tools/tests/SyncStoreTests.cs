using ClassPoints.Data.Sync;

namespace ClassPoints.Tests;

/// <summary>
/// 同步凭据的存取。测的是「什么该落盘、什么不该」。
///
/// 为什么单独测它：这个文件里存着 refresh token，它是唯一能长期换到访问权的东西。
/// 存错地方的后果不是崩溃，而是安静地把一个可以直接用的凭据留在了磁盘上 ——
/// 这类问题没人会去读代码发现，只有测例能钉住。
/// </summary>
internal static class SyncStoreTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cp-syncstore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "sync.json");
            new SyncStore(path).Save(new SyncCredentials(
                "https://sync.example.com:8443/edge", "cid-1", "access-in-memory", "refresh-1", "ABC12345"));

            // 新实例读，模拟「重启后从文件加载」
            var loaded = new SyncStore(path).Load();
            check("重启后能读回地址（含端口与子路径）",
                loaded?.BaseUrl == "https://sync.example.com:8443/edge", loaded?.BaseUrl ?? "null");
            check("班级 id、班级码、refresh 都还在",
                loaded?.ClassId == "cid-1" && loaded.ClassCode == "ABC12345" && loaded.Refresh == "refresh-1", "");

            var raw = File.ReadAllText(path);
            check("refresh 是密文落盘", !raw.Contains("refresh-1"), "");
            check("access 不落盘（只在内存里活 12 小时）", !raw.Contains("access-in-memory"), "");

            // 文件不存在 = 没配对，不是异常
            check("没有文件时返回 null", new SyncStore(Path.Combine(dir, "absent.json")).Load() is null, "");

            // 损坏的文件同样当成没配对：宁可让老师重新配一次，也不要每次启动都抛
            var broken = Path.Combine(dir, "broken.json");
            File.WriteAllText(broken, "{ 这不是 json");
            check("文件损坏时返回 null 而不是抛异常", new SyncStore(broken).Load() is null, "");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录，删不掉无所谓 */ }
        }
    }
}
