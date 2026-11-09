using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Core.Config;
using ClassPoints.Data.Config;
using ClassPoints.Data.Sync;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 电脑端设置：管理类的东西全在这边（大屏上只放本机项）。
/// 这里的每个动作都要求"点一下才生效"，不做自动保存 —— 办公室里改设置的人，
/// 需要的是一个明确的保存动作，而不是"我改了没改"的怀疑。
/// </summary>
public sealed partial class DeskSettingsPage : Page
{

    public DeskSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private static AppServices Services => App.Services;

    private DeskShell? Shell => App.Desk;

    private void Reload()
    {
        try
        {
            SubText.Text = $"{Services.ClassName} · 班级码 {Services.Settings.ClassCode} · 数据在 {Database.RootDir}";

            Sections.Children.Clear();
            BuildClassSection();
            BuildThisMachineSection();
            BuildAppearanceSection();
            BuildSecuritySection();
            BuildMemberSection();
            BuildRuleSection();
            BuildBackupSection();
            BuildProvisionSection();
            BuildSyncSection();
            BuildAboutSection();
        }
        catch (Exception ex)
        {
            Services.LogError("desk-settings", ex);
            Shell?.Toast("设置页读取失败：" + ex.Message, 6);
        }
    }

    // ---------------- 班级 ----------------

    private void BuildClassSection()
    {
        var body = Section("班级信息", "只有班级名会显示在大屏上。");

        var nameBox = new TextBox { Text = Services.Settings.ClassName, Width = 300 };
        var save = Small("保存", () =>
        {
            var name = NameNormalizer.Clean(nameBox.Text);
            if (name.Length == 0)
            {
                Shell?.Toast("班级名不能空");
                return;
            }
            Services.Settings.ClassName = name;
            Services.Audit.Add("class.rename", null, null, name);
            Shell?.Toast("已保存", 2);
            Reload();
        });

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(nameBox);
        line.Children.Add(save);
        body.Children.Add(Labelled("班级名称", line));

        body.Children.Add(ValueRow("班级码", Services.Settings.ClassCode,
            "电脑端配对要用它。没连云时只是个标识。"));

        body.Children.Add(ValueRow("数据文件", Database.DefaultDbPath,
            "积分、成绩、名单都在这一个文件里。备份请用「立即备份」，别手动复制正在写的库。"));

        var seatButton = Small("打开名单与座位", () => Shell?.Navigate(DeskNav.Students));
        body.Children.Add(Labelled($"座位布局　{Services.Settings.SeatRows} 排 × {Services.Settings.SeatCols} 座",
            seatButton));
    }

    // ---------------- 这台电脑 ----------------

    private void BuildThisMachineSection()
    {
        var body = Section("这台电脑", "同一个程序三种用法。换形态只换界面，名单和积分一份都不动。");

        body.Children.Add(ValueRow("当前形态", App.Mode.ToDisplay() + " · " + App.Mode.ToHint(),
            "形态存在本机，不影响别的机器。"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var mode in new[] { AppMode.Desk, AppMode.Board, AppMode.Viewer })
        {
            var button = Small(mode.ToDisplay(), () => SwitchMode(mode));
            if (mode == App.Mode) button.Style = Styled("DeskPrimaryButton");
            row.Children.Add(button);
        }
        body.Children.Add(Labelled("切换为", row));

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(Small("重新运行首次配置…", async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "重新运行首次配置",
                Content = "会重新问形态、班级名、配置包。名单和积分不受影响，但当前页面会切回向导。",
                PrimaryButtonText = "开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            App.Window?.RunWizard();
        }));
        body.Children.Add(footer);
    }

    private void SwitchMode(AppMode mode)
    {
        if (mode == App.Mode) return;

        Services.Audit.Add("app.mode", null, null, App.Mode.ToDb() + " → " + mode.ToDb());
        App.Window?.SwitchToMode(mode);
    }

    // ---------------- 外观 ----------------

    private void BuildAppearanceSection()
    {
        var body = Section("外观", "浅色是默认，投影出来更清楚。切换立即生效。");

        var current = Services.Settings.Theme;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        (AppTheme Theme, string Label)[ ] choices =
        [
            (AppTheme.Light, "浅色"),
            (AppTheme.Dark, "深色"),
            (AppTheme.System, "跟随系统"),
        ];

        foreach (var (theme, label) in choices)
        {
            var button = Small(label, () =>
            {
                App.ApplyTheme(theme);
                Reload();
            });
            if (theme == current) button.Style = Styled("DeskPrimaryButton");
            row.Children.Add(button);
        }

        body.Children.Add(Labelled("配色", row));
        body.Children.Add(Note("两端共用。这里切成深色，大屏下次同步也会跟着变。"));
    }

    // ---------------- 密码与恢复码 ----------------

    private void BuildSecuritySection()
    {
        var body = Section("密码与恢复码", "密码在大屏端设，这里只看状态。");

        var owner = Services.Members.Owner();
        body.Children.Add(ValueRow("管理模式密码", owner?.HasPin == true ? "已设置" : "未设置",
            owner?.HasPin == true
                ? "忘了就用恢复码在大屏端重置。"
                : "还没设。这期间谁都能进管理模式。"));

        body.Children.Add(ValueRow("恢复码", Services.Settings.HasRecovery
                ? "已生成（只存哈希）"
                : "未生成",
            Services.Settings.HasRecovery
                ? "明文只显示一次。要留纸质的，去大屏端重新生成并打印。"
                : "没有恢复码。忘密码就只能重置数据。"));
    }

    // ---------------- 班委 ----------------

    private void BuildMemberSection()
    {
        var body = Section("班委", "班委刷卡只能记分，进不了管理。");

        var members = Services.Members.ListAll(includeRevoked: false);
        if (members.Count == 0)
            body.Children.Add(Note("还没有班委。加一位再发卡。"));

        foreach (var m in members)
        {
            var cards = Services.Members.Identities(m.Id);
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(new TextBlock
            {
                Text = m.DisplayName + "　" + RoleName(m.Role),
                Style = Styled("DeskBodyText"),
            });
            info.Children.Add(new TextBlock
            {
                Text = cards.Count == 0
                    ? "没有卡 · 只能用密码"
                    : $"{cards.Count} 张卡 · " + string.Join("、", cards.Select(c => c.Label ?? "未命名卡")),
                Style = Styled("DeskSecondaryText"),
            });
            row.Children.Add(info);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            actions.Children.Add(Small("发卡", () => IssueCard(m)));
            if (cards.Count > 0)
                actions.Children.Add(Small("注销卡", () => RevokeCards(m)));
            actions.Children.Add(Small("停用", () => RevokeMember(m)));

            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            body.Children.Add(row);
        }

        body.Children.Add(Small("新增班委", AddMember));
    }

    private static string RoleName(MemberRole role) => role switch
    {
        MemberRole.Owner => "班主任",
        MemberRole.CoOwner => "副班主任",
        _ => "班委",
    };

    private async void AddMember()
    {
        var nameBox = new TextBox { Header = "姓名", Width = 300 };
        var roleCombo = new ComboBox { Header = "角色", Width = 300 };
        roleCombo.Items.Add("班委");
        roleCombo.Items.Add("副班主任");
        roleCombo.SelectedIndex = 0;

        var pinBox = new TextBox { Header = "6 位数字密码（可空：只刷卡）", Width = 300, MaxLength = 6 };

        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(nameBox);
        panel.Children.Add(roleCombo);
        panel.Children.Add(pinBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新增班委",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = NameNormalizer.Clean(nameBox.Text);
        if (name.Length == 0)
        {
            Shell?.Toast("姓名不能为空");
            return;
        }

        var pin = pinBox.Text.Trim();
        if (pin.Length > 0 && (pin.Length != 6 || !pin.All(char.IsDigit)))
        {
            Shell?.Toast("密码必须是 6 位数字");
            return;
        }

        Services.Members.Save(new Member
        {
            DisplayName = name,
            Role = roleCombo.SelectedIndex == 1 ? MemberRole.CoOwner : MemberRole.Officer,
        }, pin.Length == 0 ? null : pin);

        Services.Audit.Add("member.add", null, null, name);
        Shell?.Toast($"已添加 {name}", 2);
        Reload();
    }

    private async void IssueCard(Member m)
    {
        var uidBox = new TextBox
        {
            Header = "卡号（UID）",
            PlaceholderText = "10~14 位十六进制，例如 04A2B3C4D5E680",
            Width = 340,
        };
        var labelBox = new TextBox { Header = "备注（可空）", Text = "校园卡", Width = 340 };

        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "两种方式：\n· 在大屏「设置 → 班委管理」点「发卡」再刷卡（推荐）。\n" +
                   "· 在这里手输 UID：手机 NFC 工具读，或抄卡面丝印。",
            Style = Styled("DeskSecondaryText"),
        });
        panel.Children.Add(uidBox);
        panel.Children.Add(labelBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"给 {m.DisplayName} 发卡",
            Content = panel,
            PrimaryButtonText = "登记",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var id = Services.Members.AddCard(m.Id, uidBox.Text.Trim(), labelBox.Text.Trim());
            Services.Audit.Add("member.card_add", null, id, m.DisplayName);
            var uid = Crypto.NormalizeUid(uidBox.Text);
            var tail = uid.Length >= 4 ? uid[^4..] : uid;
            Shell?.Toast($"已登记，卡号尾号 {tail}", 4);
            Reload();
        }
        catch (Exception ex)
        {
            Services.LogError("issue-card", ex);
            Shell?.Toast("登记失败：" + ex.Message, 5);
        }
    }

    private async void RevokeCards(Member m)
    {
        var cards = Services.Members.Identities(m.Id);
        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "注销后这张卡立刻失效。数据不受影响。",
            Style = Styled("DeskSecondaryText"),
        });

        foreach (var card in cards)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = $"{card.Label ?? "未命名卡"} · 登记于 {Clock.LocalDate(card.CreatedAt):MM-dd}",
                Style = Styled("DeskBodyText"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = Small("注销", () =>
            {
                Services.Members.RevokeCard(card.Id);
                Services.Audit.Add("member.card_revoke", null, card.Id, m.DisplayName);
                Shell?.Toast("已注销这张卡", 3);
            });
            Grid.SetColumn(button, 1);
            row.Children.Add(button);

            panel.Children.Add(row);
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{m.DisplayName} 的卡",
            Content = panel,
            CloseButtonText = "关闭",
        }.ShowAsync();

        Reload();
    }

    private async void RevokeMember(Member m)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"停用 {m.DisplayName}？",
            Content = "停用后这个人的卡和密码都不能用。他记的积分一条不动，" +
                      "流水里仍显示是他记的。",
            PrimaryButtonText = "停用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Services.Members.Revoke(m.Id);
        Services.Audit.Add("member.revoke", null, m.Id, m.DisplayName);
        Reload();
    }

    // ---------------- 加分项 ----------------

    private bool _showArchivedRules;

    private void BuildRuleSection()
    {
        var body = Section("加分项", "预设有三个，和自建的一样能改能归档。");

        var rules = Services.Rules.ListAll(includeArchived: _showArchivedRules);
        foreach (var rule in rules)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 1 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new FontIcon
            {
                Glyph = rule.Glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 15,
                Foreground = Pal.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = rule.IsArchived ? 0.5 : 1,
            });
            head.Children.Add(new TextBlock
            {
                Text = $"{rule.Label}　{rule.DeltaText}",
                Style = Styled("DeskBodyText"),
                Opacity = rule.IsArchived ? 0.5 : 1,
            });
            info.Children.Add(head);
            info.Children.Add(new TextBlock
            {
                Text = rule.HasCap
                    ? $"封顶：{(rule.CapScope == CapScope.Single ? "单次最多" : "每周期最多")} {rule.CapValue}"
                    : "封顶：不封顶",
                Style = Styled("DeskSecondaryText"),
            });
            row.Children.Add(info);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (!rule.IsArchived)
            {
                actions.Children.Add(Small("−5", () => Nudge(rule, -5)));
                actions.Children.Add(Small("+5", () => Nudge(rule, 5)));
                actions.Children.Add(Small("编辑", () => EditRule(rule)));
                actions.Children.Add(Small("归档", () =>
                {
                    Services.Rules.Archive(rule.Id);
                    Shell?.Toast($"「{rule.Label}」已归档，大屏上不再出现", 4);
                    Reload();
                }));
            }
            else
            {
                actions.Children.Add(Small("恢复", () =>
                {
                    Services.Rules.Restore(rule.Id);
                    Reload();
                }));
            }

            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            body.Children.Add(row);
        }

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(Small("新建加分项", () => EditRule(null)));
        footer.Children.Add(Small(_showArchivedRules ? "只看启用中的" : "查看已归档", () =>
        {
            _showArchivedRules = !_showArchivedRules;
            Reload();
        }));
        footer.Children.Add(Small("恢复默认预设…", RestorePresetsPreview));
        body.Children.Add(footer);
    }

    private void Nudge(Rule rule, int delta)
    {
        rule.Delta += delta;
        if (rule.Delta == 0) rule.Delta = delta > 0 ? 5 : -5;
        Services.Rules.Save(rule);
        Reload();
    }

    private async void EditRule(Rule? existing)
    {
        var isNew = existing is null;
        var rule = existing ?? new Rule { Label = "", Delta = 10, Icon = IconCatalog.Default, Sort = 100 };

        var labelBox = new TextBox { Header = "名称", Text = rule.Label, Width = 320 };
        var deltaBox = new NumberBox
        {
            Header = "默认分（负数表示扣分）",
            Value = rule.Delta,
            Minimum = -500,
            Maximum = 500,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 200,
        };

        var icon = rule.Glyph;
        var iconPicker = Controls.IconChooser.Build(IconCatalog.RuleIcons, icon, g => icon = g);

        var capCombo = new ComboBox { Header = "封顶", Width = 240 };
        capCombo.Items.Add("不封顶");
        capCombo.Items.Add("单次最多");
        capCombo.Items.Add("每周期最多");
        capCombo.SelectedIndex = rule.HasCap ? (rule.CapScope == CapScope.Single ? 1 : 2) : 0;

        var capBox = new NumberBox
        {
            Header = "上限值",
            Value = rule.CapValue ?? 50,
            Minimum = 1,
            Maximum = 10000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 200,
            IsEnabled = rule.HasCap,
        };
        capCombo.SelectionChanged += (_, _) => capBox.IsEnabled = capCombo.SelectedIndex > 0;

        var panel = new StackPanel { Spacing = 10, Width = 340 };
        panel.Children.Add(labelBox);
        panel.Children.Add(deltaBox);
        panel.Children.Add(new TextBlock { Text = "图标", Style = Styled("DeskColumnHeader") });
        panel.Children.Add(iconPicker);
        panel.Children.Add(capCombo);
        panel.Children.Add(capBox);
        panel.Children.Add(new TextBlock
        {
            Text = "「入账确认」不可关闭：分账总要对得上。",
            Style = Styled("DeskSecondaryText"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isNew ? "新建加分项" : "编辑加分项",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var label = NameNormalizer.Clean(labelBox.Text);
        if (label.Length == 0)
        {
            Shell?.Toast("名称不能为空");
            return;
        }

        rule.Label = label;
        rule.Delta = double.IsNaN(deltaBox.Value) ? 10 : (int)deltaBox.Value;
        rule.Icon = IconCatalog.Normalize(icon);
        rule.CapValue = capCombo.SelectedIndex == 0 ? null : (int)capBox.Value;
        rule.CapScope = capCombo.SelectedIndex == 0 ? CapScope.None
            : capCombo.SelectedIndex == 1 ? CapScope.Single : CapScope.Period;

        Services.Rules.Save(rule);
        Reload();
        Shell?.Toast(isNew ? $"已新建「{label}」" : "已保存", 2);
    }

    private async void RestorePresetsPreview()
    {
        // 先算一遍"会新增几项、跳过几项"，再让人确认 —— 一句话的成本，换掉一整类投诉
        var existing = Services.Rules.ListAll(includeArchived: true)
            .Where(r => r.PresetKey is not null)
            .Select(r => r.PresetKey!)
            .ToHashSet();

        var presets = new (string Key, string Label)[]
        {
            ("std.speak", "回答问题"),
            ("std.recite", "早读检查背课文"),
            ("std.board", "课上黑板演题"),
        };

        var missing = presets.Where(p => !existing.Contains(p.Key)).ToList();
        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = $"将新增 {missing.Count} 项，跳过 {presets.Length - missing.Count} 项已存在。",
            Style = Styled("DeskBodyText"),
        });
        foreach (var p in missing)
            panel.Children.Add(new TextBlock { Text = "＋ " + p.Label, Style = Styled("DeskSecondaryText") });
        panel.Children.Add(new TextBlock
        {
            Text = "只补缺的。改过的名字和分值不动，删掉的也不会复活。",
            Style = Styled("DeskSecondaryText"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "恢复默认预设",
            Content = panel,
            PrimaryButtonText = missing.Count == 0 ? null : "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var (added, skipped) = Services.Rules.RestorePresets();
        Reload();
        Shell?.Toast($"已新增 {added} 项，跳过 {skipped} 项", 4);
    }

    // ---------------- 备份 ----------------

    private void BuildBackupSection()
    {
        var body = Section("备份与恢复",
            "整库快照 + 可选 Excel。每天首次启动自动备份，留 30 天。");

        body.Children.Add(ValueRow("备份目录", Services.Backups.BackupRoot,
            "建议改到 OneDrive 之类同步目录，换机器能直接拿到。"));

        body.Children.Add(ValueRow("上次备份", Services.Backups.LastBackupText,
            $"状态：{StalenessText()}"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Small("立即备份", () =>
        {
            try
            {
                var path = Services.Backups.BackupNow("manual", Services.BuildExportBundle());
                Shell?.Toast("已备份：" + Path.GetFileName(path), 4);
                Reload();
            }
            catch (Exception ex)
            {
                Shell?.Toast("备份失败：" + ex.Message, 6);
            }
        }));
        buttons.Children.Add(Small("清理旧备份", () =>
        {
            Services.Backups.Cleanup();
            Shell?.Toast("已按规则清理（保留 30 天 + 每月 1 号）", 4);
            Reload();
        }));
        buttons.Children.Add(Small("整包导出（换机用）", ExportPack));
        buttons.Children.Add(Small("从整包导入…", ImportPack));
        body.Children.Add(buttons);

        var list = Services.Backups.List(8);
        if (list.Count == 0)
        {
            body.Children.Add(Note("还没有备份文件。"));
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = "最近的备份（恢复会覆盖当前库，先留档）",
                Style = Styled("DeskColumnHeader"),
                Margin = new Thickness(0, 8, 0, 2),
            });

            foreach (var file in list)
            {
                var row = new Grid { Padding = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { Spacing = 1 };
                info.Children.Add(new TextBlock { Text = file.Name, Style = Styled("DeskBodyText") });
                info.Children.Add(new TextBlock
                {
                    Text = $"{file.KindText} · {file.AtText} · {file.SizeText}",
                    Style = Styled("DeskSecondaryText"),
                });
                row.Children.Add(info);

                var restore = Small("恢复", () => RestoreFrom(file));
                Grid.SetColumn(restore, 1);
                row.Children.Add(restore);

                body.Children.Add(row);
            }
        }
    }

    private string StalenessText() => Services.Backups.StalenessLevel switch
    {
        0 => "今天备份过",
        1 => "两三天没备份了",
        _ => "一周没备份了，数据只有一份",
    };

    private async void RestoreFrom(BackupFile file)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "用这份备份覆盖当前数据？",
            Content = $"{file.Name}\n\n当前库会改名留档（不删），再用这份恢复。恢复完重启软件。",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var check = Services.Backups.Restore(file.Path);
        Shell?.Toast(check.Message, check.Ok ? 8 : 8);
        Services.Audit.Add("backup.restore", null, file.Name, check.Message);
        if (check.Ok) Reload();
    }

    private async void ExportPack()
    {
        try
        {
            var path = Services.Backups.ExportPack();
            Shell?.Toast("整包已导出：" + path, 8);
        }
        catch (Exception ex)
        {
            Services.LogError("export-pack", ex);
            Shell?.Toast("整包导出失败：" + ex.Message, 6);
        }
    }

    private async void ImportPack()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.Window!));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "从整包恢复？",
                Content = $"{file.Name}\n\n会先校验清单（校验码、表结构、条数、原库留档），" +
                          "四项都对上才替换。",
                PrimaryButtonText = "校验并恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var check = Services.Backups.ImportPack(file.Path);
            Shell?.Toast(check.Message, 10);
            Services.Audit.Add("pack.import", null, file.Name, check.Message);
            if (check.Ok) Reload();
        }
        catch (Exception ex)
        {
            Services.LogError("import-pack", ex);
            Shell?.Toast("整包导入失败：" + ex.Message, 6);
        }
    }

    // ---------------- 预配置 ----------------

    private void BuildProvisionSection()
    {
        var body = Section("预配置",
            "在家里配好云端地址、班级码、座位，存成文件带到学校。" +
            "学校那台导入后只管名单、分值、权重。");

        var credentials = new SyncStore().Load();
        body.Children.Add(ValueRow("当前可下发的配置",
            string.IsNullOrWhiteSpace(Services.ClassName) ? "还没配班级信息" : Services.ClassName,
            credentials is null
                ? "这台还没连云端。导出的是纯本地配置（班级名 + 座位）。"
                : $"云端 {credentials.BaseUrl}，班级码 {credentials.ClassCode}。" +
                  "文件里带班级码和配对口令，到学校导入即自动配对。"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Async("导出配置包…", ExportBundleAsync));
        buttons.Children.Add(Async("导入配置包…", ImportBundleAsync));
        body.Children.Add(buttons);

        body.Children.Add(Note("配对口令用配置包口令加密。U 盘丢了也看不到班主任密码。" +
                              "不设口令也能导出，只是文件里不带密码。"));
    }

    private async Task ExportBundleAsync()
    {
        var note = new TextBox { Header = "备注（给文件起个名）", PlaceholderText = "例：育才中学 初二（3）班" };
        var pairingPin = new PasswordBox { Header = "班主任密码（配对用）", PasswordChar = "●" };
        var passphrase = new PasswordBox { Header = "配置包口令（至少 6 位）", PasswordChar = "●" };
        var confirm = new PasswordBox { Header = "再输一次口令", PasswordChar = "●" };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出配置包",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "带上班主任密码，学校那台机器导入时就能自动配对；不填就只下发地址和班级码。",
                        Style = Styled("DeskSecondaryText"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    note, pairingPin, passphrase, confirm,
                },
            },
            PrimaryButtonText = "选择保存位置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var pin = pairingPin.Password.Trim();
        var pass = passphrase.Password;

        if (pin.Length > 0)
        {
            if (pass.Length < 6)
            {
                Shell?.Toast("要带密码，就得设一个至少 6 位的配置包口令", 5);
                return;
            }
            if (pass != confirm.Password)
            {
                Shell?.Toast("两次输入的口令不一样", 4);
                return;
            }
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            SuggestedFileName = string.IsNullOrWhiteSpace(note.Text)
                ? "classpoints"
                : "classpoints-" + Services.ClassName,
        };
        picker.FileTypeChoices.Add("班级积分配置包", new List<string> { ".cpcfg" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.Window));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            var bundle = Services.Config.Build(pin, pass, (note.Text ?? "").Trim());
            ConfigService.SaveAndKeep(bundle, file.Path);
            Services.Audit.Add("config.export", null, null, $"{bundle.ClassName} → {file.Path}");
            Shell?.Toast($"已导出到 {file.Path}" +
                         (bundle.NeedsPassphrase ? "（含加密的配对口令）" : "（不含配对口令）"), 8);
        }
        catch (Exception ex)
        {
            Services.LogError("config-export", ex);
            Shell?.Toast("导出失败：" + ex.Message, 6);
        }
    }

    private async Task ImportBundleAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add(".cpcfg");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.Window));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        ConfigBundle bundle;
        try
        {
            bundle = ConfigBundleFile.Load(file.Path);
        }
        catch (Exception ex)
        {
            Shell?.Toast(ex.Message, 8);
            return;
        }

        var passphrase = "";
        var passBox = new PasswordBox { PlaceholderText = "配置包口令", PasswordChar = "●" };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入配置包",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = Services.Config.Describe(bundle),
                        Style = Styled("DeskBodyText"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = bundle.NeedsPassphrase
                            ? "这份配置带加密的配对密码，请输入导出时设的口令。"
                            : "这份配置没有加密的配对密码，导入后到「同步」里手动配对即可。",
                        Style = Styled("DeskSecondaryText"),
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
        passphrase = passBox.Password;

        try
        {
            var result = await Services.Config.ApplyAsync(bundle, passphrase, "desk");
            ConfigBundleFile.KeepCopy(bundle, file.Path);
            Services.Audit.Add("config.import", null, null, $"{bundle.ClassName} ← {file.Path}");

            Shell?.Toast(result.Message + (result.Pulled is null ? "" : "，" + result.Pulled), 8);
            Reload();
        }
        catch (ConfigException ex)
        {
            // 口令错、文件被改、字段不符 —— 都是能说清楚的话，原样告诉老师
            Shell?.Toast(ex.Message, 8);
        }
        catch (Exception ex)
        {
            Services.LogError("config-import", ex);
            Shell?.Toast("导入失败：" + ex.Message, 8);
        }
    }

    // ---------------- 同步 ----------------

    private void BuildSyncSection()
    {
        var sync = Services.Sync;
        var configured = sync.IsConfigured;
        var creds = sync.IsConfigured ? new SyncStore().Load() : null;

        var body = Section("同步",
            configured
                ? $"已连接到 {creds?.BaseUrl}。两端各记各的，靠 UUID 幂等合并，断了网也照常用。"
                : "这台电脑目前是纯本地模式：它就是数据的唯一副本。「备份」就是你的同步。");

        body.Children.Add(ValueRow("同步状态", Services.Pump.StatusText,
            "本地写入永远先成功，同步只在后台跑。断网时界面只会多一个「待推送」角标。"));
        body.Children.Add(ValueRow("待推送", Services.Outbox.Count() + " 条",
            "每条写操作都有幂等键，重连后重复提交不会重复入账。"));

        if (configured)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Async("立即同步", async () =>
            {
                var report = await Services.Pump.RunAsync();
                Shell?.Toast(report is null ? "同步没有执行" : report.Ok
                    ? $"推送 {report.Pushed} 条，拉取 {report.Pulled} 条"
                    : $"同步失败：{report.Error}", 5);
                Reload();
            }));
            buttons.Children.Add(Small("换一台设备配对…", () =>
            {
                sync.SignOut();
                Shell?.Toast("凭据已清除，下面可以重新配对", 5);
                Reload();
            }));
            body.Children.Add(buttons);
            return;
        }

        // 还没配对：两个入口。建班只该做一次，配对是每台设备做一次。
        var urlBox = new TextBox
        {
            PlaceholderText = "https://classpoints-api.你的账号.workers.dev",
            Width = 460,
            Height = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = "http://127.0.0.1:8787",
        };
        var codeBox = new TextBox
        {
            PlaceholderText = "8 位，建班时给的",
            Width = 220,
            Height = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var pinBox = new PasswordBox
        {
            PasswordChar = "●",
            Width = 220,
            Height = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        body.Children.Add(Labelled("服务器地址", urlBox));

        // 两个输入框并排。各自用 Labelled 把标签压在框上方，
        // 和上面「班级名称」用同一套写法，高度与基线才不会叉开。
        var pairRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        pairRow.Children.Add(Labelled("班级码", codeBox));
        pairRow.Children.Add(Labelled("班主任密码", pinBox));
        body.Children.Add(pairRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Async("配对到已有班级", async () =>
        {
            try
            {
                var baseUrl = (urlBox.Text ?? "").Trim();
                var code = (codeBox.Text ?? "").Trim();
                var pin = pinBox.Password;
                if (baseUrl.Length == 0 || code.Length == 0 || pin.Length == 0)
                {
                    Shell?.Toast("地址、班级码、密码都要填", 4);
                    return;
                }

                var report = await sync.PairAsync(baseUrl, code, pin, "desk");
                Services.Log($"同步配对成功 {baseUrl}");
                Services.Pump.Start();
                Shell?.Toast(report.Ok ? "配对成功，开始后台同步" : "配对失败：" + report.Error, 5);
                Reload();
            }
            catch (Exception ex)
            {
                Services.LogError("sync-pair", ex);
                Shell?.Toast("配对失败：" + ex.Message, 6);
            }
        }));
        actions.Children.Add(Async("在这台电脑上建班", async () =>
        {
            try
            {
                var baseUrl = (urlBox.Text ?? "").Trim();
                var pin = pinBox.Password;
                if (baseUrl.Length == 0 || pin.Length < 6)
                {
                    Shell?.Toast("地址要填，密码至少 6 位", 4);
                    return;
                }

                var report = await sync.CreateClassAsync(
                    baseUrl, Services.ClassName, Services.DeskOperator.DisplayName, pin,
                    Crypto.RandomHex(4).ToUpperInvariant(), "desk");
                Services.Log($"建班成功 {baseUrl}");
                Services.Pump.Start();
                Shell?.Toast($"建班成功，班级码 {report.CreatedCode}（大屏配对要用它）", 10);
                Reload();
            }
            catch (Exception ex)
            {
                Services.LogError("sync-create-class", ex);
                Shell?.Toast("建班失败：" + ex.Message, 6);
            }
        }));
        body.Children.Add(actions);

        body.Children.Add(Note("先把云端跑起来：worker/ 目录下 wrangler d1 migrations apply --remote，" +
            "再 wrangler deploy，然后把地址填到上面。没配也不影响用，只是换机器要靠整包导入。"));
    }

    private Button Async(string text, Func<Task> work)
    {
        var button = new Button
        {
            Content = text,
            Style = Styled("DeskButton"),
            Height = 30,
            MinWidth = 64,
            FontSize = 13,
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Services.LogError("settings-async", ex);
                Shell?.Toast("操作失败：" + ex.Message, 5);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    // ---------------- 关于 ----------------

    private void BuildAboutSection()
    {
        var body = Section("其它", "");

        // 重跑向导的入口只在上面「这台电脑」那一节留一个 —— 同一件事放两个按钮，
        // 一个"下次启动生效"、一个"立刻就换"，老师点错就要骂人。
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Small("打开日志目录", () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Database.LogDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Shell?.Toast("打不开：" + ex.Message, 4);
            }
        }));
        body.Children.Add(buttons);

        body.Children.Add(ValueRow("版本", "班级积分 v" + AppServices.AppVersion,
            "数据库表结构版本 " + Sql.SchemaVersion + "。升级时只做向前迁移，不降级。"));
    }

    // ---------------- 构件 ----------------

    private StackPanel Section(string title, string hint)
    {
        var border = new Border { Style = Styled("DeskCardBorder") };

        var outer = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(new TextBlock { Text = title, Style = Styled("DeskH2Text") });
        if (!string.IsNullOrWhiteSpace(hint))
            head.Children.Add(new TextBlock { Text = hint, Style = Styled("DeskSecondaryText") });
        outer.Children.Add(head);

        var body = new StackPanel { Spacing = 4 };
        outer.Children.Add(body);

        border.Child = outer;
        Sections.Children.Add(border);
        return body;
    }

    private UIElement ValueRow(string label, string value, string? hint = null)
    {
        var panel = new StackPanel { Spacing = 1, Padding = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock { Text = label, Style = Styled("DeskColumnHeader") });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Style = Styled("DeskBodyText"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(hint))
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                Style = Styled("DeskSecondaryText"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        return panel;
    }

    private UIElement Labelled(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock { Text = label, Style = Styled("DeskColumnHeader") });
        panel.Children.Add(control);
        return panel;
    }

    private UIElement Note(string text) => new TextBlock
    {
        Text = text,
        Style = Styled("DeskSecondaryText"),
        Margin = new Thickness(0, 4, 0, 4),
    };

    private Button Small(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Style = Styled("DeskButton"),
            Height = 30,
            MinWidth = 64,
            FontSize = 13,
        };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Services.LogError("settings-action", ex);
                Shell?.Toast("操作失败：" + ex.Message, 5);
            }
        };
        return button;
    }

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];
}
