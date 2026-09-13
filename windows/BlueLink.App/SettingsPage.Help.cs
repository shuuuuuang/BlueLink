using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueLink.Feedback;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BlueLink;

public partial class SettingsPage
{
    private bool _isGeneratingFeedback;
    private FeedbackPresentationState _feedbackState;
    internal enum FeedbackPresentationState { Idle, Validation, Generating, Failure }

    private void ShowHelp_Click(object sender, RoutedEventArgs e) => ShowFeedback();
    private void HelpBack_Click(object sender, RoutedEventArgs e) { if (CanLeave) SetPage("about"); }

    private void ShowFeedback()
    {
        SetPage("help");
        ConfigureHelpColumns(280, 836, 180);
        HelpNavigationCard.Visibility = Visibility.Visible;
        HelpNavigationCard.Padding = new(7, 6, 7, 6);
        HelpDetailCard.Padding = new(16);
        HelpPageTitle.Text = Localization.Strings.Get("帮助与反馈");
        HelpPageSubtitle.Text = Localization.Strings.Get("查找解决方案，或生成由你自行提交的反馈信息");
        HelpTopics.Visibility = Visibility.Visible;
        DocumentTopics.Visibility = LicenseNavigation.Visibility = Visibility.Collapsed;
        FeedbackForm.Visibility = Visibility.Visible;
        HelpDetail.Visibility = Visibility.Collapsed;
        RenderHelpTopics(null);
    }

    private void FeedbackDescription_Changed(object sender, TextChangedEventArgs e)
    {
        if (FeedbackCharacterCount is not null)
            FeedbackCharacterCount.Text = $"{FeedbackDescription.Text.Length} / {FeedbackDraft.MaximumDescriptionLength}";
        if (_feedbackState == FeedbackPresentationState.Validation &&
            !string.IsNullOrWhiteSpace(FeedbackDescription.Text))
            SetFeedbackState(FeedbackPresentationState.Idle);
    }

    private async void GenerateFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (_isGeneratingFeedback) return;
        var draft = new FeedbackDraft(FeedbackCategory.SelectedValue as string ?? "",
            FeedbackDescription.Text, FeedbackDiagnostics.IsChecked == true);
        if (draft.ValidationError is { } error)
        {
            SetFeedbackState(FeedbackPresentationState.Validation, error);
            FeedbackDescription.Focus();
            return;
        }
        var picker = new SaveFileDialog
        {
            Title = Localization.Strings.Get("保存蓝联反馈包"),
            FileName = $"BlueLink-feedback-{DateTime.Now:yyyy-MM-dd}.zip",
            Filter = Localization.Strings.Get("反馈包 (*.zip)|*.zip"),
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (picker.ShowDialog(HostWindow) != true) return;
        SetFeedbackState(FeedbackPresentationState.Generating);
        FeedbackPackage? package = null;
        try
        {
            package = await FeedbackPackageService.GenerateAsync(draft, picker.FileName,
                _model.DiagnosticsPath, overwrite: true);
            SetFeedbackState(FeedbackPresentationState.Idle);
        }
        catch (Exception failure)
        {
            SetFeedbackState(FeedbackPresentationState.Failure, failure.Message);
        }
        finally
        {
            if (_feedbackState == FeedbackPresentationState.Generating)
                SetFeedbackState(FeedbackPresentationState.Idle);
        }
        if (package is not null)
        {
            ShowFeedbackPackage(package);
        }
    }

    internal void ShowFeedbackPackage(FeedbackPackage package)
    {
        using var overlay = BlueLinkDialog.DimOwner(HostWindow, "#4D0D1729", 0);
        new FeedbackReadyWindow(package) { Owner = HostWindow }.ShowDialog();
    }

    internal void SetFeedbackState(FeedbackPresentationState state, string? detail = null)
    {
        _feedbackState = state;
        _isGeneratingFeedback = state == FeedbackPresentationState.Generating;
        var value = !_isGeneratingFeedback;
        FeedbackCategory.IsEnabled = value;
        FeedbackDescription.IsEnabled = value;
        FeedbackDiagnostics.IsEnabled = value;
        GenerateFeedbackButton.IsEnabled = value;
        GenerateFeedbackButton.Content = Localization.Strings.Get(state switch
        {
            FeedbackPresentationState.Generating => "正在生成…",
            FeedbackPresentationState.Failure => "重试生成",
            _ => "生成反馈包",
        });
        System.Windows.Automation.AutomationProperties.SetName(GenerateFeedbackButton, (string)GenerateFeedbackButton.Content);
        SettingsNavigation.IsEnabled = value;
        HelpTopics.IsEnabled = value;
        HelpBackButton.IsEnabled = value;
        ReturnToHomeButton.IsEnabled = value;
        FeedbackProgress.Visibility = _isGeneratingFeedback ? Visibility.Visible : Visibility.Collapsed;
        var validation = state == FeedbackPresentationState.Validation;
        FeedbackDescriptionLabel.Text = Localization.Strings.Get(validation ? "问题描述（必填）" : "问题描述");
        FeedbackDescriptionLabel.SetResourceReference(TextBlock.ForegroundProperty, validation ? "SettingsDangerBrush" : "SettingsInkBrush");
        FeedbackDescriptionBorder.SetResourceReference(Border.BorderBrushProperty, validation ? "SettingsDangerBrush" : "SettingsBorderBrush");
        FeedbackValidationMessage.Text = Localization.Strings.Get(
            string.IsNullOrWhiteSpace(FeedbackDescription.Text) ? "请填写问题描述后再生成反馈包。" : detail ?? "");
        FeedbackValidationMessage.Visibility = validation ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetHelpText(FeedbackDescription,
            validation ? FeedbackValidationMessage.Text : "");
        var failure = state == FeedbackPresentationState.Failure;
        FeedbackStatus.SetResourceReference(Border.BackgroundProperty, failure ? "SoftDangerBrush" : "SettingsSoftBlueBrush");
        FeedbackStatusMessage.SetResourceReference(TextBlock.ForegroundProperty, failure ? "SettingsDangerBrush" : "SettingsBlueBrush");
        FeedbackStatusMessage.Text = Localization.Strings.Get(state switch
        {
            FeedbackPresentationState.Generating => FeedbackDiagnostics.IsChecked == true
                ? "正在生成反馈包并附加诊断信息；不会自动上传。"
                : "正在生成反馈包，不附加诊断信息；不会自动上传。",
            FeedbackPresentationState.Failure => "生成失败：描述与诊断设置已保留，请更换保存位置或重试。",
            _ => "仅在点击“生成反馈包”后创建本地文件，不会自动上传。",
        });
        FeedbackStatusMessage.ToolTip = detail;
    }

    private void HelpTopic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string topic }) ShowHelpTopic(topic);
    }

    internal void ShowHelpTopic(string topic)
    {
        if (!CanLeave) return;
        ShowFeedback();
        FeedbackForm.Visibility = Visibility.Collapsed;
        HelpDetail.Visibility = Visibility.Visible;
        HelpDetailCard.Padding = new(16);
        RenderHelpTopics(topic);
        HelpDetail.Children.Clear();
        var article = HelpArticles[topic];
        HelpDetail.Children.Add(HelpText(article.Title, "SettingsSectionTitleStyle", 18));
        HelpDetail.Children.Add(HelpText(article.Subtitle, "SettingsDescriptionStyle", 13));
        var banner = new Grid();
        banner.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        banner.ColumnDefinitions.Add(new() { Width = new(12) });
        banner.ColumnDefinitions.Add(new());
        banner.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/BlueLink;component/Assets/Figma/toast-info.png")), Width = 24, Height = 24, Name = "HelpNoteIcon", VerticalAlignment = VerticalAlignment.Center });
        var bannerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        bannerText.Children.Add(HelpText(article.NoteTitle, "SettingsLabelStyle", 13));
        bannerText.Children.Add(HelpText(article.Note, "SettingsDescriptionStyle", 12));
        Grid.SetColumn(bannerText, 2);
        banner.Children.Add(bannerText);
        HelpDetail.Children.Add(new Border
        {
            Background = (Brush)FindResource("SettingsSoftBlueBrush"), CornerRadius = new(8),
            Padding = new(16, 10, 16, 10), MinHeight = 48, Margin = new(0, 10, 0, 10),
            Child = banner,
        });
        foreach (var (question, answer) in article.Questions)
        {
            var expander = new Expander
            {
                Header = HelpText(question, "SettingsLabelStyle", 14),
                Content = new Border { MinHeight = 0,
                    Child = HelpText(answer, "SettingsDescriptionStyle", 12) },
                IsExpanded = HelpDetail.Children.OfType<Expander>().Any() == false,
                Margin = new(0, 0, 0, 10),
                Padding = new(12, 8, 12, 8),
                MinHeight = 42,
                BorderThickness = new(1),
                BorderBrush = (Brush)FindResource("SettingsBorderBrush"),
            };
            expander.Resources["ExpanderContentBackground"] = FindResource("SettingsCardBrush");
            expander.Resources["ExpanderHeaderBackground"] = FindResource("SettingsCardBrush");
            expander.Resources["ExpanderContentMargin"] = new Thickness(0);
            expander.Resources["ControlCornerRadius"] = new CornerRadius(8);
            System.Windows.Automation.AutomationProperties.SetName(expander, question);
            HelpDetail.Children.Add(expander);
        }
        var feedback = new Button
        {
            Content = Localization.Strings.Get("填写问题反馈"), Appearance = ControlAppearance.Primary,
            Style = (Style)FindResource("SettingsButtonStyle"), HorizontalAlignment = HorizontalAlignment.Right,
            Width = 140, Height = 36, FontSize = 13, Margin = new(12, 0, 0, 0),
            Foreground = (Brush)FindResource("OnAccentBrush"),
            Background = (Brush)FindResource("PrimaryBrush"), BorderBrush = (Brush)FindResource("PrimaryBrush"),
        };
        feedback.Click += (_, _) => ShowFeedback();
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var footerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        footerText.Children.Add(HelpText(topic == "faq" ? "没有找到答案？" : "仍未解决？", "SettingsLabelStyle", 13));
        footerText.Children.Add(HelpText("仅打开反馈表单，不会生成文件", "SettingsDescriptionStyle", 11));
        footer.Children.Add(footerText);
        Grid.SetColumn(feedback, 1);
        footer.Children.Add(feedback);
        HelpDetail.Children.Add(new Border { Background = (Brush)FindResource("SettingsSoftBlueBrush"),
            CornerRadius = new(8), Padding = new(16, 12, 16, 12), MinHeight = 56, Child = footer });
    }

    private void RenderHelpTopics(string? selected)
    {
        foreach (var button in HelpTopics.Children.OfType<Button>())
        {
            var topic = (string)button.Tag;
            var active = topic == selected;
            button.Background = active ? (Brush)FindResource("SettingsSoftBlueBrush") : Brushes.Transparent;
            if (active)
            {
                button.Resources["ButtonBackgroundPointerOver"] = FindResource("SettingsSoftBlueBrush");
                button.Resources["ButtonBackgroundPressed"] = FindResource("SettingsSoftBlueBrush");
            }
            else
            {
                button.Resources.Remove("ButtonBackgroundPointerOver");
                button.Resources.Remove("ButtonBackgroundPressed");
            }
            var grid = new Grid();
            var copy = new StackPanel { Margin = new(8, 6, 28, 6), VerticalAlignment = VerticalAlignment.Center };
            var title = HelpText(HelpArticles[topic].Title, "SettingsLabelStyle", 14);
            title.SetResourceReference(TextBlock.ForegroundProperty, active ? "SettingsBlueBrush" : "SettingsInkBrush");
            title.Margin = new(0, 0, 0, 4);
            copy.Children.Add(title);
            var subtitle = topic switch { "connection" => "解决发现、配对与自动连接问题",
                "messages" => "了解发送、接收和历史记录", _ => "查看常见故障和处理方法" };
            var note = HelpText(subtitle, "SettingsDescriptionStyle", 11);
            note.Margin = new(0);
            note.LineHeight = 17;
            copy.Children.Add(note);
            grid.Children.Add(copy);
            grid.Children.Add(new Image { Source = (ImageSource)FindResource("SettingsChevronRight"), Width = 20,
                Height = 20, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 8, 0) });
            if (active) grid.Children.Add(new Border { Width = 3, Height = 20, CornerRadius = new(2),
                Background = (Brush)FindResource("SettingsBlueBrush"), HorizontalAlignment = HorizontalAlignment.Left });
            button.Content = grid;
        }
    }

    private TextBlock HelpText(string value, string style, double? size = null)
    {
        var text = new TextBlock
        {
            Text = Localization.Strings.Get(value), Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 4), LineHeight = size >= 18 ? 30 : 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (size.HasValue) text.FontSize = size.Value;
        return text;
    }

    private sealed record HelpArticle(string Title, string Subtitle, string NoteTitle, string Note, (string, string)[] Questions);
    private static readonly Dictionary<string, HelpArticle> HelpArticles = new()
    {
        ["connection"] = new("连接与配对", "按顺序排查设备发现、配对和自动连接问题",
            "先确认基础状态", "先通过蓝牙连接并核对安全码，再开启两端的 USB 高速传输。手机 USB 用途选择“传输文件”，首次开启时授权蓝联专用中转文件夹，例如 Download/BlueLinkUSB。设备名称旁出现闪电后，新文件自动优先使用 USB。同一手机的双向文件共用队列，后续文件等待；不同手机独立调度。暂停队首会阻止后续文件启动，取消后下一项继续。中断后重新发送整个文件。共享目录只保存加密中转文件，无需 USB 调试或替换驱动，资源管理器仍可浏览手机。",
            [
                ("附近设备列表中找不到目标设备", "1. 在目标设备上保持蓝联前台运行。\n2. 返回设备列表后点击“扫描”，等待扫描完成。\n3. 仍未出现时，关闭再重新开启两端蓝牙。"),
                ("配对失败或安全码不一致", "核对两端显示的安全码。安全码不一致时拒绝连接；确认目标设备后重新连接，不要跳过核验。"),
                ("已信任设备无法自动连接", "在“连接与设备”中确认自动连接已开启。确认目标设备正在运行蓝联且蓝牙可用。"),
                ("连接后频繁断开", "缩短设备距离并检查两端电量与蓝牙状态。发生断开后可重新扫描连接；可在问题反馈中附加诊断事件以辅助排查。"),
            ]),
        ["messages"] = new("消息与文件", "了解消息发送、文件传输、接收位置与历史记录",
            "传输仅发生在已连接设备之间", "消息与文件通过 Bluetooth 或 USB 在设备间传输，不会同步到互联网。",
            [
                ("消息发送失败", "1. 确认标题区显示当前设备已连接。\n2. 等待上一条消息完成发送后再试。\n3. 若连接已断开，重新连接后即可继续，已保存的历史记录不会丢失。"),
                ("文件传输停滞或失败", "检查连接状态、剩余磁盘空间和接收上限。暂停中的任务可以继续；失败后检查原因，再从原文件重新发起发送。"),
                ("接收的文件保存在哪里", "在“文件与存储”中查看文件保存位置，或在已完成文件的菜单中打开文件夹。"),
                ("离线后如何查看历史记录", "在设备列表中选择离线设备即可阅读本机保存的消息，并使用搜索或文件筛选。已关闭历史保存或已清除的记录无法恢复。"),
            ]),
        ["faq"] = new("常见问题", "查看蓝联的传输方式、历史记录和本机数据规则",
            "本地优先，不依赖云端同步", "聊天记录、传输记录和接收文件均保存在你的设备上。",
            [
                ("蓝联如何传输消息和文件？", "蓝联支持 Bluetooth 和 USB 设备直连，不通过服务器中转消息或文件。这样可以减少网络依赖，并让传输内容保留在用户设备之间。"),
                ("设备离线后还能查看历史吗？", "可以查看已保存在本机的历史记录。离线时无法向对方发送新消息或文件。"),
                ("重置设备标识会发生什么？", "本机身份变化后，对方需要重新核对并信任此设备。不要将其他设备身份变化视为普通重连。"),
                ("如何分别清理聊天和传输记录？", "在“隐私与数据”中分别选择清除聊天记录或传输记录。清除记录不会删除已接收的用户文件。"),
            ]),
    };

    private static readonly (string, string)[] PrivacySections =
    [
        ("01  我们处理的数据", "设备名称、设备标识、连接状态、聊天记录与传输记录默认保存在本机。"),
        ("02  Bluetooth 传输", "消息和文件仅在用户授权的设备之间通过 Bluetooth 传输，不通过互联网同步。"),
        ("03  诊断与反馈", "应用不会自动上传诊断数据；只有你主动提交反馈包时，相关信息才会离开本机。"),
        ("04  权限与删除", "可分别清除聊天与传输记录；清除记录不会删除 Download 中已接收的文件。"),
    ];
}
