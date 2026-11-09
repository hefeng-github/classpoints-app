namespace ClassPoints.App.Services;

/// <summary>
/// 两个外壳的页面序号。以前这些数字是散在 Navigate(4) / RefreshRail 里的裸字面量，
/// 加一页就得全仓库找一遍 —— 尤其是"周期"那种只有一处引用的，漏了就是跳到别的页。
/// </summary>
public static class BoardNav
{
    public const int Score = 0;
    public const int Wall = 1;
    public const int Lookup = 2;
    public const int Entries = 3;
    public const int Counters = 4;
    public const int Settings = 5;
    public const int Count = 6;
}

public static class DeskNav
{
    public const int Home = 0;
    public const int Students = 1;
    public const int Counters = 2;
    public const int Exams = 3;
    public const int Print = 4;
    public const int Periods = 5;
    public const int Settings = 6;
    public const int Count = 7;
}

public static class ViewerNav
{
    public const int Lookup = 0;
    public const int Rank = 1;
    public const int Counters = 2;
    public const int Count = 3;
}
