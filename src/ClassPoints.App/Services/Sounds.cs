using System.Runtime.InteropServices;

namespace ClassPoints.App.Services;

public enum Cue
{
    Tap,
    Thud,
    Ding,
    Undo,
    Identify,
    Error,
}

/// <summary>
/// 音效：教室嘈杂时它是"确认"而不是"表演"，所以音量低、时长短，且必须能关。
/// 用 winmm 的 PlaySound 直接播内存里的 WAV —— 不引第三方库，也不占 UI 线程。
/// </summary>
public static class Sounds
{
    private const uint SNDAsync = 0x0001;
    private const uint SNDNoDefault = 0x0002;
    private const uint SNDMemory = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[]? data, IntPtr module, uint flags);

    private static readonly Dictionary<Cue, byte[]> Cache = new();
    private static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void Play(Cue cue)
    {
        if (!_enabled) return;
        try
        {
            if (!Cache.TryGetValue(cue, out var wav))
            {
                wav = Build(cue);
                Cache[cue] = wav;
            }
            PlaySound(wav, IntPtr.Zero, SNDMemory | SNDAsync | SNDNoDefault);
        }
        catch (Exception)
        {
            // 没有音频设备时静默失败，不能因为放不出声就崩
        }
    }

    private static byte[] Build(Cue cue)
    {
        // (基频, 时长秒, 音量, 是否双音)
        var (freq, seconds, volume, second) = cue switch
        {
            Cue.Tap => (1320.0, 0.05, 0.16, 0.0),
            Cue.Thud => (220.0, 0.12, 0.24, 0.0),
            Cue.Ding => (880.0, 0.18, 0.20, 1760.0),
            Cue.Undo => (520.0, 0.14, 0.18, 340.0),
            Cue.Identify => (660.0, 0.20, 0.20, 990.0),
            _ => (300.0, 0.22, 0.22, 220.0),
        };
        return Tone(freq, seconds, volume, second);
    }

    /// <summary>16 位单声道 44.1k，指数衰减包络，避免爆音。</summary>
    private static byte[] Tone(double freq, double seconds, double volume, double secondFreq)
    {
        const int rate = 44100;
        var count = (int)(rate * seconds);
        var data = new short[count];

        for (var i = 0; i < count; i++)
        {
            var t = (double)i / rate;
            var progress = (double)i / count;
            var env = System.Math.Exp(-4.5 * progress) * (1 - progress);
            var f = secondFreq > 0 && progress > 0.45 ? secondFreq : freq;
            var sample = System.Math.Sin(2 * System.Math.PI * f * t) * env * volume;
            data[i] = (short)(sample * short.MaxValue);
        }

        using var ms = new MemoryStream(44 + count * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + count * 2);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(count * 2);
        foreach (var s in data) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
