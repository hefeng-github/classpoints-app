using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ClassPoints.Data.Sync;

/// <summary>
/// DPAPI：只做一件事 —— 只有当前 Windows 用户能解开。
///
/// 为什么不用 PasswordVault：那是 UWP API，在 WinUI 3 / WPF 的 full-trust 桌面应用里
/// 凭据会写进桌面应用共享的存储（凭据管理器里直接能看见），并不按应用隔离；
/// 我们又是 unpackaged，连包身份都没有，指望它隔离纯属自欺。
///
/// 为什么不用 ProtectedData 包：这只是两个 crypt32 导出函数，
/// 引一个 NuGet 包进来只为包一层 DllImport 不划算。
/// </summary>
[SupportedOSPlatform("windows")]
public static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>固定应用盐：防止同一个用户下的别的程序直接复用密文。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClassPoints.Sync.v1");

    public static byte[] Protect(byte[] plain)
    {
        var input = Blob(plain);
        var entropy = Blob(Entropy);
        try
        {
            if (!CryptProtectData(ref input, "ClassPoints sync token", ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
                throw new InvalidOperationException($"DPAPI 加密失败（{Marshal.GetLastWin32Error()}）");

            try { return Take(output); }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally
        {
            Free(input);
            Free(entropy);
        }
    }

    public static byte[] Unprotect(byte[] cipher)
    {
        var input = Blob(cipher);
        var entropy = Blob(Entropy);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
                throw new InvalidOperationException($"DPAPI 解密失败（{Marshal.GetLastWin32Error()}）");

            try { return Take(output); }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally
        {
            Free(input);
            Free(entropy);
        }
    }

    private static DataBlob Blob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob { Size = data.Length, Data = ptr };
    }

    private static byte[] Take(DataBlob blob)
    {
        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    private static void Free(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero) Marshal.FreeHGlobal(blob.Data);
    }
}
