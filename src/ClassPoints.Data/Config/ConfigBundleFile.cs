using ClassPoints.Core.Config;

namespace ClassPoints.Data.Config;

/// <summary>
/// 配置包的落盘与发现。
///
/// 查找顺序有意从"跟着程序走"到"跟着数据走"：
/// ① 命令行显式指定（最优先，测试与多班并行都靠它）；
/// ② exe 同目录（U 盘拷过去、就地双击就能用）；
/// ③ 数据目录（老师把它放进 LOCALAPPDATA，或者从设置里导入后我们会顺手留一份）。
/// </summary>
public static class ConfigBundleFile
{
    public const string DefaultName = "classpoints" + ConfigBundle.Extension;

    public static string Save(ConfigBundle bundle, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, bundle.ToJson(), new System.Text.UTF8Encoding(false));
        return path;
    }

    public static ConfigBundle Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException($"找不到配置文件：{path}");
        return ConfigBundle.Parse(File.ReadAllText(path));
    }

    /// <summary>没指定就跑一遍自动发现；找不到返回 null（纯本地模式，正常情况）。</summary>
    public static string? Discover(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        foreach (var dir in Candidates())
        {
            try
            {
                var path = System.IO.Path.Combine(dir, DefaultName);
                if (File.Exists(path)) return path;
            }
            catch (Exception)
            {
                // 目录不可访问就跳过，别为了找一个可选文件把启动搞挂
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var exe = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(exe)) yield return exe;
        yield return Database.RootDir;
    }

    /// <summary>导入成功后把文件收进数据目录一份，下次换 USB 也不会丢配置。</summary>
    public static string? KeepCopy(ConfigBundle bundle, string? sourcePath)
    {
        try
        {
            var target = System.IO.Path.Combine(Database.RootDir, DefaultName);
            if (sourcePath is not null &&
                string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(target),
                    StringComparison.OrdinalIgnoreCase))
                return target;

            Save(bundle, target);
            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
