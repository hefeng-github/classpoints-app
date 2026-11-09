namespace ClassPoints.Core;

/// <summary>一次入账请求（单人）。</summary>
public sealed record AwardRequest(
    string StudentId,
    Rule Rule,
    int RequestedDelta,
    int AlreadyAwardedInPeriod,
    bool ForceOverCap = false,
    string? Note = null,
    string? NoteTemplateId = null);

/// <summary>封顶判定结果。</summary>
public sealed record CapResult(
    int AppliedDelta,
    bool Capped,
    bool OverCap,
    int? Remaining,
    string? Message)
{
    public bool IsBlocked => AppliedDelta == 0 && Capped && !OverCap;
}

/// <summary>
/// 封顶规则。三条不能破的约定：
/// 1) 只对加分（delta &gt; 0）生效，扣分不受上限约束；
/// 2) 超限要压回并明说（Message 非空），不能静默；
/// 3) 软上限 —— 管理者确认后可按原值计入并标 over_cap。
/// </summary>
public static class CapPolicy
{
    public static CapResult Apply(Rule rule, int requestedDelta, int alreadyAwardedInPeriod, bool forceOverCap = false)
    {
        // 扣分/零分：不封顶
        if (requestedDelta <= 0 || !rule.HasCap || rule.CapValue is not int cap || cap <= 0)
            return new CapResult(requestedDelta, false, false, null, null);

        if (rule.CapScope == CapScope.Single)
        {
            if (requestedDelta <= cap)
                return new CapResult(requestedDelta, false, false, cap, null);

            if (forceOverCap)
                return new CapResult(requestedDelta, false, true, cap, $"本次按 {requestedDelta} 计入（超出单次上限 {cap}）");

            return new CapResult(cap, true, false, cap, $"单次上限 {cap}，已压到 {cap}");
        }

        // 周期累计
        var remaining = System.Math.Max(0, cap - System.Math.Max(0, alreadyAwardedInPeriod));
        if (requestedDelta <= remaining)
            return new CapResult(requestedDelta, false, false, remaining - requestedDelta, null);

        if (forceOverCap)
            return new CapResult(requestedDelta, false, true, 0, $"本次按 {requestedDelta} 计入（超出本周期上限 {cap}）");

        if (remaining <= 0)
            return new CapResult(0, true, false, 0, $"本周期已达上限 {cap}");

        return new CapResult(remaining, true, false, 0, $"本周期上限 {cap}，剩余 {remaining}，已压到 {remaining}");
    }

    /// <summary>按钮是否该置灰（周期口径且已到顶）。单击默认分时用。</summary>
    public static bool IsExhausted(Rule rule, int alreadyAwardedInPeriod) =>
        rule.HasCap && rule.CapScope == CapScope.Period && rule.CapValue is int cap &&
        System.Math.Max(0, alreadyAwardedInPeriod) >= cap;

    /// <summary>「余 20」这种角标文字；不适用时返回 null。</summary>
    public static string? RemainingBadge(Rule rule, int alreadyAwardedInPeriod)
    {
        if (rule.CapScope != CapScope.Period || rule.CapValue is not int cap) return null;
        var left = System.Math.Max(0, cap - System.Math.Max(0, alreadyAwardedInPeriod));
        return left <= 0 ? "已达上限" : $"余 {left}";
    }
}
