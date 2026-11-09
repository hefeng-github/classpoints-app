using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Windows.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Board;

/// <summary>
/// 大屏设置：只放本机相关的项。管理类的全在电脑端 ——
/// 否则老师站在教室改备份路径，是很容易出事的那种"顺手操作"。
/// </summary>
public sealed partial class BoardSettingsPage : Page
{
    private static readonly SolidColorBrush Primary = Pal.Primary;
    private static readonly SolidColorBrush Secondary = Pal.Secondary;
    private static readonly SolidColorBrush Positive = Pal.Positive;
    private static readonly SolidColorBrush Negative = Pal.Negative;
    private static readonly SolidColorBrush Warn = Pal.Warn;

    private readonly DispatcherTimer _lamp = new() { Interval = TimeSpan.FromSeconds(2) };
    private Member? _pendingCardFor;
    private string? _freshRecoveryCode;

    public BoardSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppServices Services => AppServices.Instance;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WhoText.Text = Services.Session.IdentityText;
        Build();

        Services.Reader.StatusChanged += OnReaderStatus;
        Services.Reader.CardDetected += OnCardDetected;
        _lamp.Tick += (_, _) => RefreshLamps();
        _lamp.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lamp.Stop();
        Services.Reader.StatusChanged -= OnReaderStatus;
        Services.Reader.CardDetected -= OnCardDetected;
    }

    // ================= 骨架 =================

    private void Build()
    {
        Sections.Children.Clear();
        Sections.Children.Add(BuildHostSection());
        Sections.Children.Add(BuildAppearanceSection());
        Sections.Children.Add(BuildSecuritySection());
        Sections.Children.Add(BuildRuleSection());
        Sections.Children.Add(BuildMemberSection());
        Sections.Children.Add(BuildRosterSection());
        Sections.Children.Add(BuildReaderSection());
        Sections.Children.Add(BuildProvisionSection());
        Sections.Children.Add(BuildPeriodSection());
    }

    private static Border Section(string title, string? sub, params UIElement[] rows)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = Primary,
        });
        if (!string.IsNullOrWhiteSpace(sub))
            panel.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 14,
                Foreground = Secondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -6, 0, 2),
            });
        foreach (var row in rows) panel.Children.Add(row);

        return new Border
        {
            Background = Pal.Card,
            BorderBrush = Pal.Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 14, 18, 16),
            Child = panel,
        };
    }

    private static Grid Row(string label, string? sub, params UIElement[] controls)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 56 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 19,
            Foreground = Primary,
        });
        if (!string.IsNullOrWhiteSpace(sub))
            text.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 13,
                Foreground = Secondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        Grid.SetColumn(text, 0);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var control in controls) right.Children.Add(control);
        Grid.SetColumn(right, 1);

        grid.Children.Add(text);
        grid.Children.Add(right);
        return grid;
    }

    // ================= 预配置 =================

    private UIElement BuildProvisionSection()
    {
        var credentials = new ClassPoints.Data.Sync.SyncStore().Load();
        var status = credentials is null
            ? "未连接云端（纯本地）"
            : $"已连接 {credentials.BaseUrl} · 班级码 {credentials.ClassCode}";

        var import = SmallButton("导入配置包…", () => _ = ImportBundleAsync());

        return Section("预配置",
            "电脑端导出的配置包放 U 盘带过来，在这里导入：地址、班级码、座位一次配好。" +
            "剩下的只贴名单、定分值。",
            Row("当前配置", status, import));
    }

    private async Task ImportBundleAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".cpcfg");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.Window));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        ClassPoints.Core.Config.ConfigBundle bundle;
        try
        {
            bundle = ClassPoints.Data.Config.ConfigBundleFile.Load(file.Path);
        }
        catch (Exception ex)
        {
            App.Board?.Toast(ex.Message, 8);
            return;
        }

        var passBox = new PasswordBox { PlaceholderText = "配置包口令", PasswordChar = "●" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入配置包",
            Content = new StackPanel
            {
                Spacing = 12,
                Width = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = App.Services.Config.Describe(bundle),
                        FontSize = 15,
                        Foreground = Primary,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = bundle.NeedsPassphrase
                            ? "这份配置带加密的配对密码，请输入导出时设的口令。"
                            : "这份配置没有加密的配对密码，导入后到「同步」里手动配对。",
                        FontSize = 13,
                        Foreground = Secondary,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    passBox,
                },
            },
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var result = await App.Services.Config.ApplyAsync(bundle, passBox.Password, "board");
            ClassPoints.Data.Config.ConfigBundleFile.KeepCopy(bundle, file.Path);
            App.Services.Audit.Add("config.import", null, null, $"{bundle.ClassName} ← {file.Path}");
            App.Board?.Toast(result.Message + (result.Pulled is null ? "" : "，" + result.Pulled), 8);
            Build();
        }
        catch (ClassPoints.Core.Config.ConfigException ex)
        {
            App.Board?.Toast(ex.Message, 8);
        }
        catch (Exception ex)
        {
            App.Services.LogError("board-config-import", ex);
            App.Board?.Toast("导入失败：" + ex.Message, 8);
        }
    }

    private Button SmallButton(string text, Action onClick, bool accent = false)
    {
        var b = new Button
        {
            Content = text,
            Height = 48,
            MinWidth = 96,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(10),
            FontSize = 16,
            Background = Pal.TileBg,
            Foreground = accent ? Positive : Primary,
            BorderBrush = Pal.Stroke,
            BorderThickness = new Thickness(1),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private UIElement NumberRow(string label, string? sub, Func<int> get, Action<int> set, int step, int min, int max, string unit)
    {
        var value = new TextBlock
        {
            Text = $"{get()}{unit}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 20,
            Foreground = Primary,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 82,
            TextAlignment = TextAlignment.Center,
        };

        void Apply(int delta)
        {
            var next = System.Math.Clamp(get() + delta, min, max);
            set(next);
            value.Text = $"{next}{unit}";
        }

        var minus = SmallButton("−", () => Apply(-step));
        var plus = SmallButton("+", () => Apply(step));
        return Row(label, sub, minus, value, plus);
    }

    private UIElement ToggleRow(string label, string? sub, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = get(), OnContent = "开", OffContent = "关", MinWidth = 100 };
        toggle.Toggled += (_, _) => set(toggle.IsOn);
        return Row(label, sub, toggle);
    }

    // ================= 1. 本机设置 =================

    private UIElement BuildHostSection()
    {
        var s = Services.Settings;
        return Section("本机设置",
            "只影响这台一体机。班级名、名单在电脑端改。",
            NumberRow("空闲回看板", "选中学生时不计时，不会被踢回看板",
                () => s.IdleTimeoutSec, v => s.IdleTimeoutSec = v, 10, 10, 600, " 秒"),
            NumberRow("管理模式锁定", "超时只退管理态，不退身份",
                () => s.LockTimeoutSec, v => s.LockTimeoutSec = v, 30, 60, 3600, " 秒"),
            NumberRow("查分页超时", "到时回看板并清屏",
                () => s.LookupTimeoutSec, v => s.LookupTimeoutSec = v, 10, 10, 300, " 秒"),
            NumberRow("看板轮播", "个人榜 ↔ 小组榜",
                () => s.BoardRotateSec, v => s.BoardRotateSec = v, 4, 8, 120, " 秒"),
            ToggleRow("音效", "嘈杂时是个确认音，不吵",
                () => s.SoundOn, v =>
                {
                    s.SoundOn = v;
                    Sounds.Enabled = v;
                    if (v) Sounds.Play(Cue.Ding);
                }));
    }

    // ================= 外观 =================

    private UIElement BuildAppearanceSection()
    {
        var current = Services.Settings.Theme;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        void Add(AppTheme theme, string label)
        {
            var button = SmallButton(label, () =>
            {
                App.ApplyTheme(theme);
                Build();
            });
            if (theme == current)
            {
                button.Background = Pal.AccentBg;
                button.BorderBrush = Pal.Accent;
                button.FontWeight = FontWeights.SemiBold;
            }
            row.Children.Add(button);
        }

        Add(AppTheme.Light, "浅色");
        Add(AppTheme.Dark, "深色");
        Add(AppTheme.System, "跟随系统");

        // 三个按钮横排，不能塞进 Row 的右侧 Auto 列 —— 三枚 96 宽的按钮会顶出右边界。
        // 标签在上、按钮在下，多宽的屏都不会被裁。
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "配色", FontSize = 19, Foreground = Primary });
        content.Children.Add(new TextBlock
        {
            Text = "立即生效，并记进本机设置",
            FontSize = 13,
            Foreground = Secondary,
        });
        content.Children.Add(row);

        return Section("外观", "灯亮着用浅色更清楚，熄灯用深色不刺眼。", content);
    }

    // ================= 2. 密码与恢复码 =================

    private UIElement BuildSecuritySection()
    {
        var owner = Services.Members.Owner();

        var changePin = SmallButton("修改密码", async () =>
        {
            var shell = App.Board;
            if (shell is null) return;

            if (!await shell.AskPinAsync("先验证当前密码", "改密码要先证明是你", requireManager: true)) return;
            Services.Session.EnterAdmin();

            var first = await shell.AskDigitsAsync("设置新密码", "输入 6 位数字");
            if (first is null) return;
            var second = await shell.AskDigitsAsync("再输一次", "两次要一样");
            if (second is null) return;

            if (first != second)
            {
                shell.Toast("两次输入不一样，没改");
                return;
            }

            var member = Services.Session.Current ?? owner;
            if (member is null)
            {
                shell.Toast("还没有班主任账号");
                return;
            }
            Services.Members.Save(member, first);
            shell.Toast("密码已更新");
        });

        var recoveryButton = SmallButton(_freshRecoveryCode is null ? "生成恢复码" : "重新生成", async () =>
        {
            var shell = App.Board;
            if (shell is null) return;
            if (!await shell.AskPinAsync("生成恢复码", "需要管理密码", requireManager: true)) return;

            var code = RecoveryCode.Generate();
            Services.Settings.RecoveryCodeHash = PasswordHasher.Hash(RecoveryCode.Normalize(code));
            _freshRecoveryCode = code;
            Build();

            var dialog = new ContentDialog
            {
                Title = "恢复码（只显示这一次）",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = code,
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 34,
                            FontWeight = FontWeights.Bold,
                            Foreground = Warn,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = "忘密码或换设备接管时用它。抄纸上收好，别留在屏幕上。",
                            FontSize = 14,
                            Foreground = Secondary,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
                CloseButtonText = "我已抄好",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }, accent: true);

        return Section("密码与恢复码",
            owner is null ? "还没有班主任账号，去电脑端建。" : $"当前班主任：{owner.DisplayName}",
            Row("管理模式密码", "6 位数字。改名单、改分值都要它", changePin),
            Row("恢复码", Services.Settings.HasRecovery
                ? (_freshRecoveryCode is null ? "已设置（只能重置）" : $"刚生成：{_freshRecoveryCode}")
                : "还没有恢复码。忘密码只能靠它", recoveryButton));
    }

    // ================= 3. 加分项 =================

    private UIElement BuildRuleSection()
    {
        var rows = new List<UIElement>();

        foreach (var rule in Services.Rules.ListAll(includeArchived: true))
        {
            var archived = rule.IsArchived;
            var panel = new StackPanel { Spacing = 6, Opacity = archived ? 0.55 : 1 };

            var head = new Grid { ColumnSpacing = 10 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleRow.Children.Add(new FontIcon
            {
                Glyph = rule.Glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 18,
                Foreground = Primary,
                VerticalAlignment = VerticalAlignment.Center,
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = $"{rule.Label}{(archived ? "（已归档）" : "")}",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Primary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            titleStack.Children.Add(titleRow);
            titleStack.Children.Add(new TextBlock
            {
                Text = rule.HasCap ? rule.CapText + " · 每次入账都要确认" : "不封顶 · 每次入账都要确认",
                FontSize = 13,
                Foreground = rule.HasCap ? Warn : Secondary,
            });
            Grid.SetColumn(titleStack, 0);

            var deltaText = new TextBlock
            {
                Text = rule.DeltaText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = rule.Delta >= 0 ? Positive : Negative,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 70,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(deltaText, 1);

            head.Children.Add(titleStack);
            head.Children.Add(deltaText);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(SmallButton("−5", () => AdjustDelta(rule, -5)));
            actions.Children.Add(SmallButton("+5", () => AdjustDelta(rule, 5)));
            actions.Children.Add(SmallButton("封顶", () => CycleCap(rule)));
            actions.Children.Add(SmallButton(archived ? "恢复" : "归档", () =>
            {
                Services.Rules.Archive(rule.Id, !archived);
                Build();
            }));

            panel.Children.Add(head);
            panel.Children.Add(actions);
            rows.Add(panel);

            var divider = new Border
            {
                Height = 1,
                Background = Pal.TileBg,
                Margin = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            rows.Add(divider);
        }

        var add = SmallButton("新建一项", () =>
        {
            Services.Rules.Save(new Rule { Label = "新项目", Delta = 10, Icon = IconCatalog.Default });
            Build();
            App.Board?.Toast("已新建。名称到电脑端改");
        });
        rows.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { add } });

        var restore = SmallButton("恢复默认三条预设", async () =>
        {
            var present = Services.Rules.ListAll(true).Count(r => r.PresetKey is not null);
            var shell = App.Board;
            if (shell is null) return;

            // 先给预览：一句话的成本，换掉一整类投诉
            var ok = await shell.ConfirmAsync("恢复默认预设",
                $"将新增 {3 - System.Math.Min(3, present)} 项，跳过 {System.Math.Min(3, present)} 项。改过的不覆盖。",
                "执行", "算了");
            if (!ok) return;

            var (added, skipped) = Services.Rules.RestorePresets();
            shell.Toast($"新增 {added} 项，跳过 {skipped} 项");
            Build();
        });
        rows.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { restore } });

        return Section("加分项", "预设和自建的一样。名称在电脑端改。", rows.ToArray());
    }

    private void AdjustDelta(Rule rule, int step)
    {
        rule.Delta = System.Math.Clamp(rule.Delta + step, -500, 500);
        Services.Rules.Save(rule);
        Build();
    }

    private async void CycleCap(Rule rule)
    {
        // 无 → 单次 → 每周期的三态循环；数值用同一套数字键盘输
        if (!rule.HasCap)
        {
            rule.CapValue = System.Math.Max(10, System.Math.Abs(rule.Delta));
            rule.CapScope = CapScope.Single;
            Services.Rules.Save(rule);
            Build();
            return;
        }

        if (rule.CapScope == CapScope.Single)
        {
            rule.CapScope = CapScope.Period;
            Services.Rules.Save(rule);
            Build();
            return;
        }

        var shell = App.Board;
        if (shell is null) return;
        var ok = await shell.ConfirmAsync("取消封顶", "这一项将不再有上限。确定吗？", "取消封顶", "保留");
        if (!ok) return;

        rule.CapValue = null;
        rule.CapScope = CapScope.None;
        Services.Rules.Save(rule);
        Build();
    }

    // ================= 4. 班委 =================

    private UIElement BuildMemberSection()
    {
        var rows = new List<UIElement>();
        var members = Services.Members.ListAll(includeRevoked: true);

        foreach (var member in members)
        {
            var cards = Services.Members.Identities(member.Id);
            var revoked = member.RevokedAt is not null;

            var status = revoked ? "已停用" : member.HasPin ? "已设密码" : "未设密码";
            var cardText = cards.Count == 0
                ? "还没有卡"
                : string.Join("　", cards.Select(c =>
                    $"{c.Label ?? "卡"} {c.Credential[..6]}…{(c.LastUsedAt is long t ? " 最近 " + Clock.ToLocal(t).ToString("MM-dd HH:mm") : "")}"));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(SmallButton("发卡", () =>
            {
                _pendingCardFor = member;
                App.Board?.Toast($"把要登记给 {member.DisplayName} 的卡放上去（30 秒内）");
                ResetCardPending();
            }));

            if (cards.Count > 0)
                actions.Children.Add(SmallButton("注销卡", async () =>
                {
                    var shell = App.Board;
                    if (shell is null) return;
                    if (!await shell.ConfirmAsync("注销卡", $"注销 {member.DisplayName} 的全部卡？丢了就立刻注销。", "注销", "取消"))
                        return;
                    foreach (var c in Services.Members.Identities(member.Id))
                        Services.Members.RevokeCard(c.Id);
                    shell.Toast("已注销");
                    Build();
                }));

            if (!revoked)
                actions.Children.Add(SmallButton("停用此人", () =>
                {
                    Services.Members.Revoke(member.Id);
                    Build();
                }));

            rows.Add(Row($"{member.DisplayName} · {member.Role.ToDisplay()}",
                $"{status} · {cardText}", actions));
        }

        rows.Add(new TextBlock
        {
            Text = "卡丢了立刻注销，否则捡到的人能记分。班主任密码在上一节改。",
            FontSize = 13,
            Foreground = Secondary,
            TextWrapping = TextWrapping.Wrap,
        });

        return Section("班委管理", "刷卡记分。没带卡的用密码。", rows.ToArray());
    }

    private DispatcherTimer? _cardPendingTimer;

    private void ResetCardPending()
    {
        _cardPendingTimer?.Stop();
        _cardPendingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _cardPendingTimer.Tick += (_, _) =>
        {
            _cardPendingTimer?.Stop();
            _pendingCardFor = null;
        };
        _cardPendingTimer.Start();
    }

    private void OnCardDetected(CardRead read)
    {
        var target = _pendingCardFor;
        if (target is null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            _pendingCardFor = null;
            _cardPendingTimer?.Stop();

            var existing = Services.Members.FindByCard(read.Uid);
            if (existing is not null)
            {
                App.Board?.Toast($"这张卡已经属于 {existing.DisplayName}，先注销再发");
                return;
            }

            Services.Members.AddCard(target.Id, read.Uid, "校园卡");
            Sounds.Play(Cue.Identify);
            App.Board?.Toast($"已把 {read.ShortUid} 登记给 {target.DisplayName}");
            Build();
        });
    }

    // ================= 5. 名单（只读） =================

    private UIElement BuildRosterSection()
    {
        var students = Services.Students.ListActive();
        var grouped = students.Count(s => !string.IsNullOrWhiteSpace(s.GroupName));
        var seated = students.Count(s => s.SeatNo is not null);

        return Section("学生名单（只读）",
            "名单在电脑端改。",
            Row("在册学生", $"{students.Count} 人 · 已排座 {seated} 人 · 已分组 {grouped} 人",
                SmallButton("到电脑端改", () => App.Board?.Toast("请在电脑端「名单」里修改"))),
            Row("座位布局", $"{Services.Settings.SeatRows} 排 × {Services.Settings.SeatCols} 列（电脑端可改）"));
    }

    // ================= 6. 读卡器 =================

    private Grid? _lampGrid;
    private TextBlock? _readerStatusText;
    private ComboBox? _portCombo;

    private UIElement BuildReaderSection()
    {
        _lampGrid = new Grid { ColumnSpacing = 18 };
        _lampGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _lampGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _lampGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _readerStatusText = new TextBlock
        {
            FontSize = 14,
            Foreground = Secondary,
            TextWrapping = TextWrapping.Wrap,
        };

        _portCombo = new ComboBox { MinWidth = 260, Height = 44, FontSize = 15 };
        foreach (var port in Pn532Serial.ListPorts())
            _portCombo.Items.Add(new ComboBoxItem { Content = port.Display, Tag = port.PortName });

        if (_portCombo.Items.Count == 0)
            _portCombo.Items.Add(new ComboBoxItem { Content = "没有发现串口设备", Tag = "" });

        var saved = Services.Settings.ReaderPort;
        for (var i = 0; i < _portCombo.Items.Count; i++)
            if (_portCombo.Items[i] is ComboBoxItem item && (string?)item.Tag == saved)
                _portCombo.SelectedIndex = i;
        if (_portCombo.SelectedIndex < 0) _portCombo.SelectedIndex = 0;

        _portCombo.SelectionChanged += (_, _) =>
        {
            if (_portCombo?.SelectedItem is not ComboBoxItem item) return;
            var port = (string?)item.Tag ?? "";
            Services.Settings.ReaderPort = port;
            Services.Reader.Stop();
            Services.Reader.Start(string.IsNullOrEmpty(port) ? null : port);
        };

        var restart = SmallButton("重新连接", () =>
        {
            Services.Reader.Stop();
            Services.Reader.Start(string.IsNullOrWhiteSpace(Services.Settings.ReaderPort)
                ? null
                : Services.Settings.ReaderPort);
            RefreshLamps();
        });

        var simulate = SmallButton("模拟刷一张测试卡", () =>
        {
            Services.Reader.SimulateSwipe("04A1B2C3D4E5F6");
            App.Board?.Toast("已模拟一次刷卡（调试通道，用于没有硬件时验链路）");
        });

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Row("读卡器端口", "PN532 转串口，设备管理器里是一个 COM 口",
            _portCombo, restart));
        panel.Children.Add(_lampGrid);
        panel.Children.Add(_readerStatusText);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { simulate },
        });
        panel.Children.Add(new TextBlock
        {
            Text = "三个灯：设备（串口开）· 帧（握手通）· 卡（读到过卡）。都亮才真能用。",
            FontSize = 13,
            Foreground = Secondary,
            TextWrapping = TextWrapping.Wrap,
        });

        RefreshLamps();

        return Section("读卡器自检",
            "没硬件也能验：用「模拟刷一张测试卡」。", panel);
    }

    private void OnReaderStatus(ReaderStatus status) =>
        DispatcherQueue.TryEnqueue(RefreshLamps);

    private void RefreshLamps()
    {
        if (_lampGrid is null) return;

        var status = Services.Reader.Status;
        var lamps = status.Lamps;
        _lampGrid.Children.Clear();

        (string Label, bool On)[] data =
        {
            ("设备", lamps.Device),
            ("帧", lamps.Frame),
            ("卡", lamps.Card),
        };

        for (var i = 0; i < data.Length; i++)
        {
            var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = data[i].On
                    ? Pal.Positive
                    : Pal.StrokeStrong,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = data[i].Label,
                FontSize = 13,
                Foreground = data[i].On ? Primary : Secondary,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            Grid.SetColumn(stack, i);
            _lampGrid.Children.Add(stack);
        }

        if (_readerStatusText is not null)
        {
            _readerStatusText.Text = status.State switch
            {
                ReaderState.Running => $"运行中 · {status.Port} · {status.Message}",
                ReaderState.NoDevice => "没找到读卡器。" + status.Message,
                ReaderState.Error => "出错了：" + status.Message,
                ReaderState.Opening => "正在打开端口…",
                _ => "已停止（点「重新连接」）",
            };
            _readerStatusText.Foreground = status.State switch
            {
                ReaderState.Running => Positive,
                ReaderState.Error => Negative,
                ReaderState.NoDevice => Warn,
                _ => Secondary,
            };
        }
    }

    // ================= 7. 历史周期 =================

    private UIElement BuildPeriodSection()
    {
        var rows = new List<UIElement>();
        var active = Services.Periods.Active();
        if (active is not null)
            rows.Add(Row($"当前周期：{active.Name}", $"{active.RangeText} · {Services.Periods.EntryCount(active.Id)} 条记录"));

        var archived = Services.Periods.Archived();
        if (archived.Count == 0)
        {
            rows.Add(new TextBlock
            {
                Text = "还没有归档周期。重置积分后，旧周期会出现在这里。",
                FontSize = 15,
                Foreground = Secondary,
            });
        }
        else
        {
            foreach (var period in archived.Take(12))
                rows.Add(Row($"{period.Name}", $"{period.RangeText} · {Services.Periods.EntryCount(period.Id)} 条记录"));
        }

        rows.Add(new TextBlock
        {
            Text = "重置与导出在电脑端「周期」页。大屏这里只看，不动数据。",
            FontSize = 13,
            Foreground = Secondary,
        });

        return Section("周期", "重置不是删除：旧记录一行不动，只是归档。", rows.ToArray());
    }

    // ================= 管理态 =================

    private async void OnAdminClick(object sender, RoutedEventArgs e)
    {
        var shell = App.Board;
        if (shell is null) return;

        if (Services.Session.IsAdmin)
        {
            Services.Session.ExitAdmin();
            WhoText.Text = Services.Session.IdentityText;
            shell.Toast("已退出管理模式");
            return;
        }

        if (await shell.AskPinAsync("管理模式密码", "改设置、改分值、撤别人的记录都要它", requireManager: true))
        {
            Services.Session.EnterAdmin();
            WhoText.Text = Services.Session.IdentityText;
            Build();
            shell.Toast("已进入管理模式（5 分钟无操作自动退出）");
        }
    }
}
