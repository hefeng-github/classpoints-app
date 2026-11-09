using ClassPoints.Core;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text;

namespace ClassPoints.Data;

public sealed record CardRead(string Uid, DateTime At)
{
    public string ShortUid => Uid.Length <= 8 ? Uid : Uid[^8..];
}

public sealed record CardPortInfo(string PortName, string Description, bool LikelyPn532)
{
    public string Display => $"{PortName} · {Description}" + (LikelyPn532 ? "（像是 PN532）" : "");
}

public enum ReaderState
{
    Stopped,
    Opening,
    Running,
    NoDevice,
    Error,
}

public sealed record ReaderStatus(ReaderState State, string Port, string Message, long LastCardAt, int CardsRead)
{
    public bool IsRunning => State == ReaderState.Running;

    /// <summary>三个灯：设备 / 帧 / 卡。</summary>
    public (bool Device, bool Frame, bool Card) Lamps =>
        (State is ReaderState.Running or ReaderState.Opening,
         State == ReaderState.Running,
         LastCardAt > 0 && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastCardAt) < 60_000);
}

/// <summary>
/// PN532 串口读卡。
///
/// 为什么要自己拼帧：PN532 的 UART 接口不是键盘模拟，它只回二进制帧。
/// 键盘模拟那条路要求界面上始终有个隐藏输入框抢焦点，老师一点别处就失效 —— 一体机上不能这么干。
///
/// 三个坑（方案 2.4）：
/// 1) 波特率必须是 115200，出厂 9600 的要先发 SetSerialBaudRate；
/// 2) 每帧都要校验 LCS / DCS，丢一个字节就会把「有卡」读成「没卡」；
/// 3) 同一张卡在读卡器上放着会连续命中，必须按 UID 做时间窗去重，否则一次刷卡加 20 次分。
/// </summary>
public sealed class Pn532Serial : IDisposable
{
    private const byte Preamble = 0x00;
    private const byte StartCode = 0xFF;
    private const byte Postamble = 0x00;
    private const byte HostToPn = 0xD4;
    private const byte PnToHost = 0xD5;

    private readonly object _gate = new();
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private long _lastCardAt;
    private int _cardsRead;
    private string _statusMessage = "未启动";
    private string _portName = "";
    private ReaderState _state = ReaderState.Stopped;

    /// <summary>读到一张卡（已去重）。回调在后台线程，界面要自己 DispatcherQueue 切回来。</summary>
    public event Action<CardRead>? CardDetected;

    public event Action<ReaderStatus>? StatusChanged;

    public ReaderStatus Status
    {
        get
        {
            lock (_gate) return new ReaderStatus(_state, _portName, _statusMessage, _lastCardAt, _cardsRead);
        }
    }

    /// <summary>去重窗口：同一张卡 1.5 秒内只认一次。</summary>
    public int DebounceMs { get; set; } = 1500;

    public int PollIntervalMs { get; set; } = 150;

    // WMI 查询只在 Windows 上有；Data 工程仍按 net10.0 编，好在 Linux 上跑 CI 的编译检查
    [SupportedOSPlatform("windows")]
    public static List<CardPortInfo> ListPorts()
    {
        var list = new List<CardPortInfo>();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                var start = name.LastIndexOf("(COM", StringComparison.Ordinal);
                if (start < 0) continue;
                var end = name.IndexOf(')', start);
                if (end < 0) continue;
                var port = name.Substring(start + 1, end - start - 1);
                descriptions[port] = name[..start].Trim();
            }
        }
        catch (Exception)
        {
            // 拿不到 WMI 就只列串口名，不影响使用
        }

        foreach (var name in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var desc = descriptions.TryGetValue(name, out var d) ? d : "串口设备";
            var likely = desc.Contains("PN532", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("CH340", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("CP210", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("FT232", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("USB-SERIAL", StringComparison.OrdinalIgnoreCase);
            list.Add(new CardPortInfo(name, desc, likely));
        }
        return list;
    }

    [SupportedOSPlatform("windows")]
    public static string? GuessPort()
    {
        var ports = ListPorts();
        return ports.FirstOrDefault(p => p.LikelyPn532)?.PortName ?? ports.FirstOrDefault()?.PortName;
    }

    [SupportedOSPlatform("windows")]
    public void Start(string? portName = null)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunLoopAsync(portName, token), token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (Exception) { }
        try { _loop?.Wait(600); } catch (Exception) { }
        ClosePort();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        SetStatus(ReaderState.Stopped, "已停止", _portName);
    }

    /// <summary>没有硬件时的调试通道：直接喂一个 UID，走同一条认人链路。</summary>
    public void SimulateSwipe(string uid)
    {
        var normalized = Crypto.NormalizeUid(uid);
        if (normalized.Length == 0) return;
        Emit(new CardRead(normalized, DateTime.Now));
    }

    [SupportedOSPlatform("windows")]
    private async Task RunLoopAsync(string? preferredPort, CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_port is null || !_port.IsOpen)
                {
                    var port = preferredPort ?? GuessPort();
                    if (port is null)
                    {
                        SetStatus(ReaderState.NoDevice, "没找到串口设备，插上读卡器后会自动重试", "");
                        await Task.Delay(2500, token);
                        continue;
                    }
                    SetStatus(ReaderState.Opening, $"正在打开 {port}", port);
                    OpenPort(port);
                    if (!await HandshakeAsync(token))
                    {
                        failures++;
                        SetStatus(ReaderState.Error, "读卡器没有回应（检查波特率与接线）", port);
                        ClosePort();
                        await Task.Delay(System.Math.Min(8000, 1500 * failures), token);
                        continue;
                    }
                    failures = 0;
                    SetStatus(ReaderState.Running, "读卡中", port);
                }

                var uid = await PollOnceAsync(token);
                if (uid is not null) Emit(new CardRead(uid, DateTime.Now));

                await Task.Delay(PollIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                SetStatus(ReaderState.Error, "读卡异常：" + ex.Message, _portName);
                ClosePort();
                try { await Task.Delay(System.Math.Min(8000, 1500 * failures), token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void OpenPort(string portName)
    {
        var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 400,
            WriteTimeout = 400,
            DtrEnable = true,
            RtsEnable = true,
        };
        port.Open();
        try { port.DiscardInBuffer(); } catch (Exception) { }
        _port = port;
        _portName = portName;
    }

    private void ClosePort()
    {
        try { _port?.Close(); } catch (Exception) { }
        try { _port?.Dispose(); } catch (Exception) { }
        _port = null;
    }

    /// <summary>握手：GetFirmwareVersion。有回应才算真的通了，光打开串口不算。</summary>
    private async Task<bool> HandshakeAsync(CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var resp = await TransceiveAsync(new byte[] { 0x02 }, token);
            if (resp is { Length: >= 6 } && resp[0] == 0x03) return true;
            await Task.Delay(200, token);
        }
        return false;
    }

    /// <summary>一次寻卡。成功返回 UID（十六进制大写），没有卡返回 null。</summary>
    private async Task<string?> PollOnceAsync(CancellationToken token)
    {
        var resp = await TransceiveAsync(new byte[] { 0x4A, 0x01, 0x00 }, token);
        if (resp is null || resp.Length < 3) return null;

        // 0x4B <NrTargets> <Tg> <SENS_RES 2> <SEL_RES> <IDLength> <ID...>
        if (resp[0] != 0x4B || resp[1] < 1) return null;
        var idx = 5;
        if (resp.Length <= idx) return null;
        var idLen = resp[idx];
        idx++;
        if (idLen <= 0 || resp.Length < idx + idLen) return null;

        var sb = new StringBuilder(idLen * 2);
        for (var i = 0; i < idLen; i++) sb.Append(resp[idx + i].ToString("X2"));
        return sb.ToString();
    }

    private async Task<byte[]?> TransceiveAsync(byte[] command, CancellationToken token)
    {
        var port = _port;
        if (port is null || !port.IsOpen) return null;

        var frame = BuildFrame(command);
        port.Write(frame, 0, frame.Length);

        var buffer = new List<byte>(64);
        var deadline = DateTime.UtcNow.AddMilliseconds(700);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var one = port.ReadByte();
                if (one >= 0)
                {
                    buffer.Add((byte)one);
                    if (ContainsFullFrame(buffer, out var payload)) return payload;
                }
            }
            catch (TimeoutException)
            {
                // 没数据就继续等到 deadline，别把超时当失败
            }
            await Task.Yield();
        }
        return null;
    }

    /// <summary>00 00 FF LEN LCS D4 CMD ... DCS 00，DCS 是 TFI 起算的字节和取反加一。</summary>
    public static byte[] BuildFrame(byte[] data)
    {
        var len = data.Length + 1;                      // +TFI
        var lcs = (byte)(~(byte)len + 1);
        var frame = new List<byte>(len + 7) { Preamble, 0x00, StartCode, (byte)len, lcs, HostToPn };
        frame.AddRange(data);

        var sum = HostToPn;
        foreach (var b in data) sum += b;
        frame.Add((byte)(~(byte)sum + 1));
        frame.Add(Postamble);
        return frame.ToArray();
    }

    /// <summary>在字节流里找第一个完整且校验通过的响应帧。</summary>
    public static bool ContainsFullFrame(List<byte> buffer, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        for (var i = 0; i + 5 < buffer.Count; i++)
        {
            if (buffer[i] != Preamble || buffer[i + 1] != 0x00 || buffer[i + 2] != StartCode) continue;

            var len = buffer[i + 3];
            var lcs = buffer[i + 4];
            if ((byte)(len + lcs) != 0) continue;               // 长度校验
            if (buffer.Count < i + 5 + len + 2) continue;       // 还没收全

            if (buffer[i + 5] != PnToHost) continue;

            var sum = 0;
            for (var j = 0; j < len; j++) sum += buffer[i + 5 + j];
            var dcs = buffer[i + 5 + len];
            if ((byte)(sum + dcs) != 0) continue;               // 数据校验

            var body = new byte[len - 1];
            for (var j = 0; j < body.Length; j++) body[j] = buffer[i + 6 + j];
            payload = body;
            return true;
        }
        return false;
    }

    private void Emit(CardRead read)
    {
        lock (_gate)
        {
            var now = Clock.NowMs();
            if (_lastSeen.TryGetValue(read.Uid, out var last) && now - last < DebounceMs) return;
            _lastSeen[read.Uid] = now;
            _lastCardAt = now;
            _cardsRead++;
            _statusMessage = $"刚读到 {read.ShortUid}";
            _state = ReaderState.Running;

            if (_lastSeen.Count > 64)
                foreach (var key in _lastSeen.Where(kv => now - kv.Value > 600_000).Select(kv => kv.Key).ToList())
                    _lastSeen.Remove(key);
        }

        CardDetected?.Invoke(read);
        RaiseStatus();
    }

    private void SetStatus(ReaderState state, string message, string port)
    {
        lock (_gate)
        {
            _state = state;
            _statusMessage = message;
            _portName = port;
        }
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        try { StatusChanged?.Invoke(Status); } catch (Exception) { }
    }

    public void Dispose() => Stop();
}
