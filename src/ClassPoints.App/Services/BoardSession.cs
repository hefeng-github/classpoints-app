using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.App.Services;

public enum IdentityState
{
    Anonymous,
    Identified,
    Admin,
}

/// <summary>会话态：只影响返回键行为与超时，不影响数据。</summary>
public enum InteractionState
{
    Idle,
    Scoring,
    Composing,
}

/// <summary>
/// 大屏端的三个正交状态位（方案 9.9）。
/// 不要用一个 currentUser 变量扛所有事：身份、会话、同步这三件事的寿命完全不同。
/// 同步态只影响状态条，永远不阻塞操作 —— 一旦允许它挡 UI，网络一抖老师就点不动了。
/// </summary>
public sealed class BoardSession(MemberRepository members)
{
    private int _pinFailures;
    private long _lockedUntil;

    public IdentityState Identity { get; private set; } = IdentityState.Anonymous;

    public Member? Current { get; private set; }

    public InteractionState Interaction { get; set; } = InteractionState.Idle;

    public int PendingCount { get; set; }

    public bool SyncEnabled { get; set; }

    public bool IsOnline { get; set; } = true;

    public DateTime LastActivity { get; private set; } = DateTime.Now;

    public DateTime AdminEnteredAt { get; private set; } = DateTime.MinValue;

    public event Action? Changed;

    public bool CanWrite => Identity != IdentityState.Anonymous;

    public bool IsAdmin => Identity == IdentityState.Admin;

    public bool IsPinLocked => _lockedUntil > Clock.NowMs();

    public int PinLockSecondsLeft =>
        IsPinLocked ? (int)System.Math.Ceiling((_lockedUntil - Clock.NowMs()) / 1000.0) : 0;

    public string IdentityText => Identity switch
    {
        IdentityState.Anonymous => "未认人",
        IdentityState.Admin => $"{Current?.DisplayName ?? "班主任"} · 管理",
        _ => Current?.DisplayName ?? "已认人",
    };

    public string SyncText
    {
        get
        {
            if (!SyncEnabled) return "本机存储";
            if (!IsOnline) return PendingCount > 0 ? $"离线 · 待同步 {PendingCount} 条" : "离线";
            return PendingCount > 0 ? $"待同步 {PendingCount} 条" : "已同步";
        }
    }

    public void Touch() => LastActivity = DateTime.Now;

    /// <summary>刷卡 / PIN 成功。同一个人再刷一次不重算；换人要上层先确认。</summary>
    public bool Identify(Member member, bool asAdmin = false)
    {
        var same = Current?.Id == member.Id && Identity != IdentityState.Anonymous;
        Current = member;
        Identity = asAdmin || member.IsManager ? (asAdmin ? IdentityState.Admin : IdentityState.Identified) : IdentityState.Identified;
        if (asAdmin) AdminEnteredAt = DateTime.Now;
        Touch();
        _pinFailures = 0;
        Changed?.Invoke();
        return !same;
    }

    public void EnterAdmin()
    {
        if (Current is null) return;
        Identity = IdentityState.Admin;
        AdminEnteredAt = DateTime.Now;
        Touch();
        Changed?.Invoke();
    }

    /// <summary>管理超时只退管理模式，不退身份 —— 班主任改完名单被叫走，回来还能记分。</summary>
    public void ExitAdmin()
    {
        if (Identity != IdentityState.Admin) return;
        Identity = Current is null ? IdentityState.Anonymous : IdentityState.Identified;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Current = null;
        Identity = IdentityState.Anonymous;
        Interaction = InteractionState.Idle;
        Changed?.Invoke();
    }

    public Member? VerifyPin(string pin)
    {
        if (IsPinLocked) return null;
        var member = members.VerifyPin(pin);
        if (member is not null)
        {
            _pinFailures = 0;
            return member;
        }

        _pinFailures++;
        if (_pinFailures >= 5)
        {
            _lockedUntil = Clock.NowMs() + 60_000;
            _pinFailures = 0;
        }
        return null;
    }

    public void ResetPinFailures() => _pinFailures = 0;

    /// <summary>班委会话是否该退：仅 idle 态计时，scoring/composing 不算（老师低头看名单很正常）。</summary>
    public bool ShouldIdleExit(int idleSeconds) =>
        Identity != IdentityState.Anonymous &&
        Interaction == InteractionState.Idle &&
        (DateTime.Now - LastActivity).TotalSeconds >= idleSeconds;

    public bool ShouldAdminExit(int lockSeconds) =>
        Identity == IdentityState.Admin &&
        (DateTime.Now - AdminEnteredAt).TotalSeconds >= lockSeconds;
}
