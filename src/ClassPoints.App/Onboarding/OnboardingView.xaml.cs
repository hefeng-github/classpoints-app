using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Core.Config;
using ClassPoints.Data;
using ClassPoints.Data.Config;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Onboarding;

/// <summary>
/// 首次启动向导。它不是产品介绍页，是配置向导。
///
/// 第一件事是问「这台电脑做什么」：以前靠 --mode 参数分，老师换台机器就得重做快捷方式，
/// 一旦忘了改就静默进错形态（大屏端没有导入入口，电脑端没有记分台），
/// 现在这件事只在这里问一次，答案存进 settings.app_mode，之后启动直接进对应外壳。
///
/// 一条硬规矩：「完成」之前一个字节都不写库（配置包除外 —— 它是老师明确点了「套用」才落盘的）。
/// 所有选择先攒在内存里，最后一次性落盘，中途关掉不会留下半截配置。
/// </summary>
public sealed partial class OnboardingView : UserControl
{
    private enum StepKind
    {
        Mode,
        Config,
        Class,
        Names,
        Rules,
        Account,
        Data,
        SelfCheck,
    }

    private sealed class RuleDraft
    {
        public string? ExistingId { get; init; }
        public string? PresetKey { get; init; }
        public string Icon { get; set; } = IconCatalog.Default;
        public string Label { get; set; } = "";
        public int Delta { get; set; } = 10;
        public int? CapValue { get; init; }
        public CapScope CapScope { get; init; } = CapScope.None;
        public int Sort { get; init; } = 100;
        public bool Enabled { get; set; } = true;
    }

    private static readonly int MinCardW = 210;
    private static readonly int MinCardH = 120;
    private static readonly int Gap = 12;

    private readonly List<StepKind> _steps = [];
    private readonly List<string> _titles = [];
    private readonly List<RuleDraft> _rules = [];
    private readonly List<Student> _previewStudents = [];

    private AppMode _mode = AppMode.Desk;
    private bool _modeChosen;
    private int _step;

    private string _className = "";
    private List<string> _names = [];
    private string _pin = "";
    private string _pinAgain = "";
    private string? _recoveryCode;
    private string _classCode = "";

    private string? _configPath;
    private string? _configApplied;

    private int _awardedIndex = -1;
    private int _awardedDelta;
    private int _columns = 8;

    public OnboardingView()
    {
        InitializeComponent();

        _mode = App.Mode;
        _modeChosen = App.ModeKnown;
        _classCode = App.Services.Settings.ClassCode;
        _className = App.Services.Settings.ClassName;

        foreach (var rule in App.Services.Rules.ListAll())
        {
            _rules.Add(new RuleDraft
            {
                ExistingId = rule.Id,
                PresetKey = rule.PresetKey,
                Icon = rule.Glyph,
                Label = rule.Label,
                Delta = rule.Delta,
                CapValue = rule.CapValue,
                CapScope = rule.CapScope,
                Sort = rule.Sort,
            });
        }

        RebuildSteps();
        ShowStep(0);
    }

    public event EventHandler? Completed;

    /// <summary>验收用：直接跳到某一步（CP_SNAP 抓向导各步截图时用，正常流程不会碰它）。</summary>
    public void DebugGoToStep(int step) => ShowStep(step);

    public int StepCount => _titles.Count;

    private static AppServices Services => App.Services;

    private StepKind Current => _steps[_step];

    private bool IsSelfCheck => Current == StepKind.SelfCheck;

    // ---------------- 步骤表 ----------------

    /// <summary>形态一改，后面几步整组换掉，所以步骤表是现算的，不是写死的数组。</summary>
    private void RebuildSteps()
    {
        _steps.Clear();
        _titles.Clear();

        if (!_modeChosen)
        {
            _steps.Add(StepKind.Mode);
            _titles.Add("这台电脑做什么");
        }

        _steps.Add(StepKind.Config);
        _titles.Add("预配置");

        switch (_mode)
        {
            case AppMode.Board:
                _steps.Add(StepKind.Class); _titles.Add("班级");
                _steps.Add(StepKind.Names); _titles.Add("名单");
                _steps.Add(StepKind.Rules); _titles.Add("分值");
                _steps.Add(StepKind.Account); _titles.Add("密码与恢复码");
                break;

            case AppMode.Desk:
                _steps.Add(StepKind.Class); _titles.Add("班级与数据");
                break;

            default:
                _steps.Add(StepKind.Data); _titles.Add("数据来源");
                break;
        }

        _steps.Add(StepKind.SelfCheck);
        _titles.Add("自检");
    }

    // ---------------- 步骤控制 ----------------

    private void ShowStep(int step)
    {
        _step = System.Math.Clamp(step, 0, _titles.Count - 1);

        TitleText.Text = $"{_step + 1}. {_titles[_step]}";
        KickerText.Text = _modeChosen
            ? $"班级积分 · {_mode.ToDisplay()} · 首次配置"
            : "班级积分 · 首次配置";

        RenderDots();

        FormScroll.Visibility = IsSelfCheck ? Visibility.Collapsed : Visibility.Visible;
        SelfCheckScroll.Visibility = IsSelfCheck ? Visibility.Visible : Visibility.Collapsed;

        Body.Children.Clear();
        HintText.Text = "";

        if (IsSelfCheck) BuildSelfCheck();
        else
        {
            switch (Current)
            {
                case StepKind.Mode: BuildModeStep(); break;
                case StepKind.Config: BuildConfigStep(); break;
                case StepKind.Class: BuildClassStep(); break;
                case StepKind.Names: BuildNamesStep(); break;
                case StepKind.Rules: BuildRulesStep(); break;
                case StepKind.Account: BuildAccountStep(); break;
                default: BuildDataStep(); break;
            }
        }

        TryPanel.Visibility = IsSelfCheck && _mode != AppMode.Viewer
            ? Visibility.Visible
            : Visibility.Collapsed;
        BackButton.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        SkipAllButton.Visibility = IsSelfCheck ? Visibility.Collapsed : Visibility.Visible;
        SkipAllButton.Content = "跳到自检";
        NextButton.Content = IsSelfCheck ? "完成，开始用" : "下一步";
    }

    private void RenderDots()
    {
        StepDots.Children.Clear();
        for (var i = 0; i < _titles.Count; i++)
        {
            var done = i < _step;
            var active = i == _step;
            StepDots.Children.Add(new Border
            {
                Width = active ? 26 : 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = active ? Pal.Accent : done ? Pal.Positive : Pal.Stroke,
            });
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (!ValidateStep()) return;

        if (IsSelfCheck)
        {
            Finish();
            return;
        }

        ShowStep(_step + 1);
    }

    private bool ValidateStep()
    {
        switch (Current)
        {
            case StepKind.SelfCheck:
            case StepKind.Config:
            case StepKind.Rules:
                return true;

            case StepKind.Mode:
                if (!_modeChosen)
                {
                    HintText.Text = "先选一个，选错可以在设置里重来。";
                    return false;
                }
                return true;

            case StepKind.Class:
                if (NameInput is null || NameNormalizer.Clean(NameInput.Text).Length == 0)
                {
                    HintText.Text = "班级名不能空";
                    return false;
                }
                _className = NameNormalizer.Clean(NameInput.Text);
                return true;

            case StepKind.Names:
                if (NamesInput is not null)
                    _names = NamesInput.Text
                        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(NameNormalizer.Clean)
                        .Where(n => n.Length > 0)
                        .ToList();
                return true;

            case StepKind.Account:
                if (_pin.Length != 6)
                {
                    HintText.Text = "密码得是 6 位数字";
                    return false;
                }
                if (_pin != _pinAgain)
                {
                    HintText.Text = "两次输入不一致";
                    return false;
                }
                return true;

            default:
                return true;
        }
    }

    private void OnSkipAll(object sender, RoutedEventArgs e) => ShowStep(_titles.Count - 1);

    // ---------------- 第 0 步：形态 ----------------

    private void BuildModeStep()
    {
        Body.Children.Add(new TextBlock
        {
            Text = "同一个程序，三种用法。选完就不用再记启动参数。",
            Style = Styled("DeskBodyText"),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(ModeCard(AppMode.Board, "\uE7F4", "教室大屏",
            "触屏点一下就记分；迟到这类事件也在这里按＋。界面字大、按钮厚。"));
        row.Children.Add(ModeCard(AppMode.Desk, "\uE977", "班主任电脑",
            "导入名单和成绩、算加权总评、打印、备份。键盘鼠标，密度高。"));
        row.Children.Add(ModeCard(AppMode.Viewer, "\uE890", "只读查询",
            "放在办公室或走廊的机器上，学生自己来查排名和明细，改不了任何东西。"));
        Body.Children.Add(row);

        Body.Children.Add(new TextBlock
        {
            Text = "形态存在本机。以后要换，到「设置 → 这台电脑」里重新选，不用重装。",
            Style = Styled("SmallText"),
        });
    }

    private Button ModeCard(AppMode mode, string glyph, string title, string hint)
    {
        var card = new Button
        {
            Width = 268,
            Height = 176,
            Padding = new Thickness(18, 16, 18, 16),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(_modeChosen && _mode == mode ? 2 : 1),
            BorderBrush = _modeChosen && _mode == mode ? Pal.Accent : Pal.Stroke,
            Background = _modeChosen && _mode == mode ? Pal.AccentBg : Pal.Card,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily(IconCatalog.FontFamily),
            FontSize = 26,
            Foreground = _modeChosen && _mode == mode ? Pal.Accent : Pal.Secondary,
        });
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Pal.Primary,
        });
        stack.Children.Add(new TextBlock
        {
            Text = hint,
            Style = Styled("SmallText"),
            TextWrapping = TextWrapping.Wrap,
        });

        card.Content = stack;
        card.Click += (_, _) =>
        {
            _mode = mode;
            _modeChosen = true;
            RebuildSteps();
            ShowStep(0);
        };
        return card;
    }

    // ---------------- 1. 预配置 ----------------

    private void BuildConfigStep()
    {
        _configPath ??= ConfigBundleFile.Discover();

        Body.Children.Add(new TextBlock
        {
            Text = "家里配好的那套（班级、座位、云端地址）可以打包成 .cpcfg 带过来，" +
                   "在这里导入一次，后面只剩贴名单和填分数。没有就跳过。",
            Style = Styled("DeskBodyText"),
        });

        var pathBox = new TextBox
        {
            Header = "配置包路径",
            Text = _configPath ?? "",
            PlaceholderText = @"例如 U:\班级.cpcfg，或本程序目录下的 classpoints.cpcfg",
            Width = 520,
            IsReadOnly = false,
        };

        // 「选文件」贴着路径框、「套用」贴口令框 —— 按钮挨着它作用的那个输入框，
        // 否则四个控件并排一站，老师会以为「选文件」是给口令用的。
        var browse = new Button
        {
            Content = "选文件…",
            Style = Styled("TouchSecondaryButton"),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browse);
        Body.Children.Add(pathRow);

        var pass = new PasswordBox
        {
            Header = "配置包口令（设过才要）",
            PlaceholderText = "没设口令就留空",
            PasswordChar = "●",
            Width = 260,
        };

        var summary = new TextBlock
        {
            Style = Styled("SmallText"),
            TextWrapping = TextWrapping.Wrap,
            Text = string.IsNullOrWhiteSpace(_configPath) ? "没找到配置包，可以手动选文件。" : "点「套用」先看一眼里面是什么。",
        };

        var apply = new Button
        {
            Content = "套用",
            Style = Styled("TouchPrimaryButton"),
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var passRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        passRow.Children.Add(pass);
        passRow.Children.Add(apply);

        Body.Children.Add(passRow);
        Body.Children.Add(summary);

        if (_configApplied is not null)
            summary.Text = "已套用：" + _configApplied + "。下一步会接着问还缺什么。";

        browse.Click += async (_, _) =>
        {
            var picked = await PickConfigFileAsync();
            if (picked is null) return;
            _configPath = picked;
            pathBox.Text = picked;
            summary.Text = "已选中，点「套用」。";
        };

        apply.Click += async (_, _) =>
        {
            var path = pathBox.Text.Trim();
            if (path.Length == 0)
            {
                summary.Text = "先选一个配置文件。";
                return;
            }

            ConfigBundle bundle;
            try
            {
                bundle = ConfigBundleFile.Load(path);
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
            {
                summary.Text = "读不了这个文件：" + ex.Message;
                return;
            }

            summary.Text = Services.Config.Describe(bundle);

            try
            {
                var kind = _mode == AppMode.Board ? "board" : "desk";
                var result = await Services.Config.ApplyAsync(bundle, pass.Password, kind);
                if (!result.Ok)
                {
                    summary.Text = result.Message + (result.Pulled is null ? "" : "（" + result.Pulled + "）");
                    return;
                }

                ConfigBundleFile.KeepCopy(bundle, path);
                _configPath = path;
                _configApplied = bundle.ClassName + "，云端 " +
                                 (bundle.Sync is null ? "未连接" : bundle.Sync.BaseUrl);
                _className = bundle.ClassName;
                _classCode = Services.Settings.ClassCode;

                summary.Text = "已套用：" + _configApplied + (result.Pulled is null ? "" : "；" + result.Pulled);
                HintText.Text = "班级名和座位已就位，下一步直接贴名单。";
            }
            catch (ConfigException ex)
            {
                summary.Text = "口令不对或文件被改过：" + ex.Message;
            }
            catch (Exception ex)
            {
                Services.LogError("wizard-config", ex);
                summary.Text = "套用失败：" + ex.Message;
            }
        };

        Body.Children.Add(Section("配置包里有什么", new[]
        {
            "班级名、学期、座位排布（没有名单，名单要在学校贴）。",
            "云端地址与班级码，配对口令是加密的。",
            "不含成绩和积分 —— 这些随本机数据走，不跟着配置包。",
        }));
    }

    private async Task<string?> PickConfigFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(ConfigBundle.Extension);
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Services.LogError("wizard-pick", ex);
            HintText.Text = "文件选择框打不开，把路径粘贴进上面的输入框就行。";
            return null;
        }
    }

    // ---------------- 2. 班级 ----------------

    private TextBox? NameInput { get; set; }

    private void BuildClassStep()
    {
        NameInput = new TextBox
        {
            Header = "班级名称",
            Text = Services.Settings.ClassName,
            PlaceholderText = "例如：初二 (3) 班",
            Width = 420,
        };
        Body.Children.Add(NameInput);

        Body.Children.Add(new TextBlock
        {
            Text = "只填班级名。学期按日期自动判断：" + Database.TermNameFor(DateTime.Now) + "。",
            Style = Styled("SmallText"),
        });

        if (_mode == AppMode.Board)
        {
            Body.Children.Add(Section("接下来", new[]
            {
                "贴一列姓名，或者稍后导入 Excel。",
                "确认三个加分项的分值，之后可改。",
                "设 6 位管理密码，生成恢复码。",
                "试加一分，看看一屏放得下不。",
            }));
        }
        else
        {
            var hasData = Services.Students.ListActive().Count > 0;
            Body.Children.Add(new TextBlock
            {
                Text = hasData
                    ? $"本机已有「{Services.ClassName}」，{Services.Students.ListActive().Count} 人。直接下一步。"
                    : "本机还没有名单。大屏建好班的话，用「整包导出 / 导入」把数据拷过来，或连云端拉取。",
                Style = Styled("SmallText"),
            });
        }
    }

    // ---------------- 3. 名单 ----------------

    private TextBox? NamesInput { get; set; }

    private void BuildNamesStep()
    {
        NamesInput = new TextBox
        {
            Header = "粘贴一列姓名（一行一个）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 220,
            Width = 520,
            PlaceholderText = "王小明\n李思远\n张伟\n…",
        };
        if (_names.Count > 0) NamesInput.Text = string.Join("\n", _names);

        Body.Children.Add(NamesInput);
        Body.Children.Add(new TextBlock
        {
            Text = "大屏只支持粘贴。带学号、座位、分组的导入在电脑端做。",
            Style = Styled("SmallText"),
        });

        var later = new Button { Content = "稍后再说，先看界面", Style = Styled("TouchSecondaryButton") };
        later.Click += (_, _) =>
        {
            _names.Clear();
            ShowStep(_step + 1);
        };
        Body.Children.Add(later);
    }

    // ---------------- 4. 分值 ----------------

    private void BuildRulesStep()
    {
        Body.Children.Add(new TextBlock
        {
            Text = "三个预设，分值就地改。",
            Style = Styled("DeskBodyText"),
        });

        foreach (var draft in _rules)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new FontIcon
            {
                Glyph = draft.Icon,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Pal.Secondary,
            });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = draft.Label, Style = Styled("BodyText") });
            info.Children.Add(new TextBlock
            {
                Text = draft.CapValue is int cap
                    ? (draft.CapScope == CapScope.Single ? $"单次最多 {cap}" : $"每周期最多 {cap}")
                    : "不封顶",
                Style = Styled("SmallText"),
            });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var delta = new NumberBox
            {
                Value = draft.Delta,
                Minimum = -500,
                Maximum = 500,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Width = 150,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var target = draft;
            delta.ValueChanged += (_, e) =>
            {
                if (!double.IsNaN(e.NewValue)) target.Delta = (int)e.NewValue;
            };
            Grid.SetColumn(delta, 2);
            row.Children.Add(delta);

            var enabled = new CheckBox
            {
                IsChecked = true,
                Content = "启用",
                VerticalAlignment = VerticalAlignment.Center,
            };
            enabled.Checked += (_, _) => target.Enabled = true;
            enabled.Unchecked += (_, _) => target.Enabled = false;
            Grid.SetColumn(enabled, 3);
            row.Children.Add(enabled);

            Body.Children.Add(row);
            Body.Children.Add(new Border { Height = 1, Background = Pal.Stroke });
        }

        Body.Children.Add(new TextBlock
        {
            Text = "没有扣分项。要加就到「设置 → 加分项」；只数次数不记分的，去「事件计数」。",
            Style = Styled("SmallText"),
        });
    }

    // ---------------- 5. 密码与恢复码 ----------------

    private void BuildAccountStep()
    {
        Body.Children.Add(Section("班级码", new[]
        {
            $"本机班级号：{_classCode}",
            "电脑端用它配对。没连云时只是个编号。",
        }));

        var pinLabel = new StackPanel { Spacing = 4, Width = 220 };
        pinLabel.Children.Add(new TextBlock { Text = "管理模式密码（6 位数字）", Style = Styled("SmallText") });
        var pinBox = new PasswordBox { MaxLength = 6, PasswordChar = "●", Width = 200 };
        pinLabel.Children.Add(pinBox);

        var againLabel = new StackPanel { Spacing = 4, Width = 220 };
        againLabel.Children.Add(new TextBlock { Text = "再输一次", Style = Styled("SmallText") });
        var againBox = new PasswordBox { MaxLength = 6, PasswordChar = "●", Width = 200 };
        againLabel.Children.Add(againBox);

        pinBox.PasswordChanged += (_, _) =>
            _pin = new string(pinBox.Password.Where(char.IsDigit).ToArray());
        againBox.PasswordChanged += (_, _) =>
            _pinAgain = new string(againBox.Password.Where(char.IsDigit).ToArray());

        var pinRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        pinRow.Children.Add(pinLabel);
        pinRow.Children.Add(againLabel);
        Body.Children.Add(Section("管理模式密码", null, pinRow));
        Body.Children.Add(new TextBlock
        {
            Text = "进管理要输它。班委刷卡只能记分。忘了只能靠恢复码。",
            Style = Styled("SmallText"),
        });

        var codeText = new TextBlock
        {
            Text = _recoveryCode ?? "还没有生成",
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Pal.Warn,
            IsTextSelectionEnabled = true,
        };

        var generate = new Button
        {
            Content = _recoveryCode is null ? "生成恢复码" : "重新生成",
            Style = Styled("TouchSecondaryButton"),
        };
        generate.Click += (_, _) =>
        {
            _recoveryCode = RecoveryCode.Generate();
            codeText.Text = _recoveryCode;
            HintText.Text = "抄到纸上，或到电脑端打一张恢复卡。";
        };

        var recoverRow = new StackPanel { Spacing = 10 };
        recoverRow.Children.Add(codeText);
        recoverRow.Children.Add(generate);
        Body.Children.Add(Section("恢复码", null, recoverRow));
        Body.Children.Add(new TextBlock
        {
            Text = "只显示这一次。忘了密码或换了电脑，都靠它。",
            Style = Styled("SmallText"),
        });

        Body.Children.Add(Section("班委卡（可选，之后也能发）", new[]
        {
            "没读卡器也行。插上后到「设置 → 读卡器自检」确认能读，再发卡。",
            "也可以不刷卡：给班委设 6 位密码。",
        }));
    }

    // ---------------- 只读端：数据来源 ----------------

    private void BuildDataStep()
    {
        Body.Children.Add(new TextBlock
        {
            Text = "只读端不产生数据，得先告诉它去哪儿看。两条路，选一条就行。",
            Style = Styled("DeskBodyText"),
        });

        var url = new TextBox
        {
            Header = "云端地址",
            PlaceholderText = "https://classpoints.xxx.workers.dev",
            Width = 420,
        };
        var code = new TextBox { Header = "班级码", Text = _classCode, Width = 200 };
        var pin = new PasswordBox { Header = "配对口令", Width = 200, PasswordChar = "●" };
        var connect = new Button { Content = "连接并拉取", Style = Styled("TouchPrimaryButton"), MinWidth = 150 };
        var status = new TextBlock { Style = Styled("SmallText"), TextWrapping = TextWrapping.Wrap };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(code);
        row.Children.Add(pin);
        row.Children.Add(connect);

        Body.Children.Add(Section("一、连云端（推荐）", null, new StackPanel
        {
            Spacing = 10,
            Children = { url, row, status },
        }));

        connect.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(url.Text))
            {
                status.Text = "先填云端地址。";
                return;
            }

            status.Text = "连接中…";
            connect.IsEnabled = false;
            try
            {
                var report = await Services.Sync.PairAsync(url.Text.Trim(), code.Text.Trim(), pin.Password, "desk");
                if (!report.Ok)
                {
                    status.Text = "配对失败：" + (report.Error ?? "未知原因");
                    return;
                }

                var pull = await Services.Sync.RunOnceAsync();
                Services.Pump.Start();
                status.Text = pull.Ok
                    ? $"已连接，拉取 {pull.Pulled} 条。之后后台每 5 分钟自动拉一次。"
                    : $"已配对，但这次拉取失败：{pull.Error}。稍后会自动重试。";
            }
            catch (Exception ex)
            {
                Services.LogError("wizard-viewer-pair", ex);
                status.Text = "连接失败：" + ex.Message;
            }
            finally
            {
                connect.IsEnabled = true;
            }
        };

        Body.Children.Add(Section("二、整包导入", new[]
        {
            "在大屏或电脑端「设置 → 整包导出」拿到一份数据包，拷过来导入。",
            "导入入口也在设置里 —— 这一步不导入也能先把界面跑起来。",
        }));

        Body.Children.Add(Section("只读的意思", new[]
        {
            "没有记分台、没有导入、没有撤销；所有按钮都不会改库。",
            "后台仍会拉取云端数据，那是同步不是编辑。",
        }));
    }

    // ---------------- 最后一步：自检 ----------------

    private void BuildSelfCheck()
    {
        BuildPreviewStudents();
        RenderSelfCheckGrid();

        SelfCheckHost.SizeChanged -= OnSelfCheckSizeChanged;
        SelfCheckHost.SizeChanged += OnSelfCheckSizeChanged;
        SelfCheckScroll.SizeChanged -= OnSelfCheckSizeChanged;
        SelfCheckScroll.SizeChanged += OnSelfCheckSizeChanged;
        UpdateVerdict();
    }

    private void OnSelfCheckSizeChanged(object sender, SizeChangedEventArgs e) => RenderSelfCheckGrid();

    private void BuildPreviewStudents()
    {
        _previewStudents.Clear();

        if (_mode == AppMode.Board && _names.Count > 0)
        {
            for (var i = 0; i < _names.Count; i++)
                _previewStudents.Add(new Student
                {
                    Id = "preview-" + i,
                    Name = _names[i],
                    SeatNo = i + 1,
                });
            return;
        }

        var existing = Services.Students.ListActive();
        if (_mode != AppMode.Board && existing.Count > 0)
        {
            _previewStudents.AddRange(existing);
            return;
        }

        // 没名单就用 48 个演示卡片：不做这一步的话，跳过名单的人会卡在这里面对空网格，
        // 然后关掉向导 —— 而自检恰恰是向导存在的唯一理由。
        for (var i = 1; i <= 48; i++)
            _previewStudents.Add(new Student
            {
                Id = "demo-" + i,
                Name = $"学生 {i:D2}",
                SeatNo = i,
            });
    }

    private void RenderSelfCheckGrid()
    {
        var width = SelfCheckHost.ActualWidth - 16;
        if (width <= 100) return;

        _columns = System.Math.Clamp((int)((width + Gap) / (MinCardW + Gap)), 4, 12);

        var grid = new Grid { ColumnSpacing = Gap, RowSpacing = Gap };
        for (var c = 0; c < _columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (_previewStudents.Count + _columns - 1) / _columns;
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MinCardH) });

        for (var i = 0; i < _previewStudents.Count; i++)
        {
            var card = new Controls.StudentCard { Index = i };
            card.Bind(_previewStudents[i], i * 7 % 120, i % 3 == 0 ? 10 : 0);
            Grid.SetRow(card, i / _columns);
            Grid.SetColumn(card, i % _columns);
            grid.Children.Add(card);

            if (i == _awardedIndex) card.FloatDelta(_awardedDelta);
        }

        SelfCheckHost.Child = grid;
        // 滚动高度要等这一轮布局完成才准，所以推迟到下一个消息里再读
        DispatcherQueue.TryEnqueue(UpdateVerdict);
    }

    private void UpdateVerdict()
    {
        if (!IsSelfCheck) return;

        var rows = (_previewStudents.Count + _columns - 1) / _columns;
        // 判词里的"放得下"直接问 ScrollViewer：能滚动就说明没放下。
        // 自己算像素会因为边距差一两像素而说反话，那比不写更糟。
        var fitsHeight = SelfCheckScroll.ScrollableHeight <= 1;

        var count = _previewStudents.Count;
        var lead = _mode switch
        {
            AppMode.Board => $"当前 {_columns} 列 × {rows} 行，{count} 人",
            AppMode.Desk => $"名单页按 {_columns} 列排，共 {count} 人",
            _ => $"查询页每屏 {_columns} 列，共 {count} 人",
        };

        VerdictText.Text = lead + (fitsHeight ? "放得下。" : "放不下，要滚动。") +
                           "　拖窄窗口会自动少一列。";

        if (_mode == AppMode.Board && _names.Count == 0)
            VerdictText.Text += "（演示卡片。导入真名单后排版一样。）";

        if (_mode == AppMode.Viewer)
            VerdictText.Text += "只读端只用来看，不记分。";
    }

    private async void OnTryAward(object sender, RoutedEventArgs e)
    {
        var rule = _rules.FirstOrDefault(r => r.Enabled) ?? new RuleDraft { Label = "回答问题", Delta = 10 };
        var realRule = new Rule
        {
            Id = "preview",
            Label = rule.Label,
            Delta = rule.Delta,
            Icon = rule.Icon,
            CapValue = rule.CapValue,
            CapScope = rule.CapScope,
        };

        var notes = Services.Notes.Top(6);
        Pad.Show(realRule, 1, notes);
        await Task.CompletedTask;

        Pad.Confirmed -= OnPadConfirmed;
        Pad.Confirmed += OnPadConfirmed;
    }

    private void OnPadConfirmed(int delta, string? note)
    {
        if (_previewStudents.Count == 0) return;

        _awardedIndex = 0;
        _awardedDelta = delta;
        _previewStudents[0].Points = System.Math.Max(0, _previewStudents[0].Points + delta);

        RenderSelfCheckGrid();
        TryResultText.Text = $"刚才加的是 {_previewStudents[0].Name} {(delta >= 0 ? "+" : "")}{delta}" +
                             (string.IsNullOrWhiteSpace(note) ? "" : $"（{note}）") + "，只在内存里。";
        UndoButton.Visibility = Visibility.Visible;
    }

    private void OnTryUndo(object sender, RoutedEventArgs e)
    {
        if (_awardedIndex < 0) return;

        _previewStudents[_awardedIndex].Points =
            System.Math.Max(0, _previewStudents[_awardedIndex].Points - _awardedDelta);
        _awardedIndex = -1;
        RenderSelfCheckGrid();
        TryResultText.Text = "已撤销，卡上的分数回到原样。";
        UndoButton.Visibility = Visibility.Collapsed;
        ToastLayer.Show("真实记分台上也会这样出现一条「已撤销」提示条", 4);
    }

    // ---------------- 落盘 ----------------

    private void Finish()
    {
        try
        {
            ApplyAll();
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Services.LogError("onboarding-finish", ex);
            ToastLayer.Show("保存失败：" + ex.Message, 8);
        }
    }

    private void ApplyAll()
    {
        var settings = Services.Settings;

        App.SetMode(_mode);              // 形态是最先要定的，别的都跟着它走
        if (_className.Length > 0) settings.ClassName = _className;
        if (!string.IsNullOrWhiteSpace(_classCode)) settings.ClassCode = _classCode;

        if (_mode == AppMode.Board)
        {
            if (_names.Count > 0)
            {
                var added = Services.Students.ImportNames(_names, 1);
                Services.Log($"向导导入名单 {added} 人");
            }

            foreach (var draft in _rules)
            {
                if (draft.ExistingId is null) continue;

                var existing = Services.Rules.Get(draft.ExistingId);
                if (existing is null) continue;

                existing.Delta = draft.Delta;
                if (!draft.Enabled) existing.ArchivedAt = Clock.NowMs();
                Services.Rules.Save(existing);
            }

            if (_pin.Length == 6)
            {
                var owner = Services.Members.Owner() ?? new Member { DisplayName = "班主任", Role = MemberRole.Owner };
                Services.Members.Save(owner, _pin);
            }

            if (_recoveryCode is not null)
                settings.RecoveryCodeHash = PasswordHasher.Hash(RecoveryCode.Normalize(_recoveryCode));
        }

        Services.Audit.Add("onboarding.done", null, null,
            $"{_mode.ToDb()}，班级 {settings.ClassName}，名单 {Services.Students.ListActive().Count} 人");
        Services.TryAutoBackup("onboarding");
    }

    // ---------------- 小工具 ----------------

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];

    private static UIElement Section(string title, IEnumerable<string>? bullets, UIElement? content = null)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(new TextBlock { Text = title, Style = Styled("DeskH2Text") });

        if (content is not null) panel.Children.Add(content);

        if (bullets is not null)
            foreach (var line in bullets)
                panel.Children.Add(new TextBlock
                {
                    Text = "· " + line,
                    Style = Styled("SmallText"),
                    TextWrapping = TextWrapping.Wrap,
                });

        return panel;
    }
}
