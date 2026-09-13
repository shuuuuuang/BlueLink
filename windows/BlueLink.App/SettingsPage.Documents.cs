using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = Wpf.Ui.Controls.Button;

namespace BlueLink;

public partial class SettingsPage
{
    private void ShowPrivacy_Click(object sender, RoutedEventArgs e) => ShowDocument(Localization.Strings.Get("隐私政策"),
        Localization.Strings.Get("蓝联以本地处理为原则，消息与文件传输不依赖云端服务。"), PrivacySections);

    private void ShowAgreement_Click(object sender, RoutedEventArgs e) => ShowDocument(Localization.Strings.Get("用户协议"),
        Localization.Strings.Get("使用蓝联即表示你理解并同意以下使用规则。"),
        [
            (Localization.Strings.Get("01  服务范围"), Localization.Strings.Get("蓝联用于授权设备间的 Bluetooth 消息和文件传输，并提供本地历史记录管理。")),
            (Localization.Strings.Get("02  用户责任"), Localization.Strings.Get("你应确保发送内容合法且已获得必要授权，并妥善管理设备访问权限和可信设备。")),
            (Localization.Strings.Get("03  设备与数据安全"), Localization.Strings.Get("重置设备标识或移除可信设备后，需要重新核对安全码。请自行备份重要文件。")),
            (Localization.Strings.Get("04  更新与联网"), Localization.Strings.Get("检查和下载新版本需要联网；消息与文件通过 Bluetooth 或 USB 在设备间直接传输。")),
        ]);

    private void ConfigureHelpColumns(double navigation, double detail, double minimumNavigation)
    {
        HelpNavigationColumn.Width = new(navigation, GridUnitType.Star);
        HelpNavigationColumn.MinWidth = minimumNavigation;
        HelpDetailColumn.Width = new(detail, GridUnitType.Star);
    }

    private void ShowDocument(string title, string introduction, (string Title, string Body)[] sections)
    {
        ShowFeedback();
        SetPage("policy");
        ConfigureHelpColumns(220, 916, 160);
        HelpNavigationCard.Padding = new(8);
        HelpDetailCard.Padding = new(16);
        HelpPageTitle.Text = title;
        HelpPageSubtitle.Text = Localization.Strings.Get("阅读适用于 Windows 客户端的完整文档");
        HelpTopics.Visibility = FeedbackForm.Visibility = Visibility.Collapsed;
        DocumentTopics.Visibility = HelpDetail.Visibility = Visibility.Visible;
        DocumentTopics.Children.Clear();
        var directoryTitle = DocumentText(Localization.Strings.Get("目录"), 16, true);
        directoryTitle.Margin = new(8, 0, 0, 12);
        DocumentTopics.Children.Add(directoryTitle);
        HelpDetail.Children.Clear();
        var documentTitle = DocumentText(title, 18, true, 26);
        documentTitle.Margin = new(0, 0, 0, 4);
        HelpDetail.Children.Add(documentTitle);
        HelpDetail.Children.Add(DocumentText(Localization.Strings.Get("生效日期：2026 年 9 月 4 日"), 12, muted: true));
        HelpDetail.Children.Add(new Border
        {
            Background = (Brush)FindResource("SettingsSoftBlueBrush"), Padding = new(16),
            CornerRadius = new(8), MinHeight = 0, Margin = new(0, 12, 0, 16),
            Child = DocumentText(introduction, 12, lineHeight: 20, muted: true),
        });
        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            var localizedTitle = Localization.Strings.Get(section.Title);
            var parts = localizedTitle.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var number = parts.Length == 2 ? parts[0] : (index + 1).ToString("D2");
            var headingText = parts.Length == 2 ? parts[1].Trim() : localizedTitle;
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = new(32) });
            row.ColumnDefinitions.Add(new());
            var numberText = DocumentText(number, 11, true, 22);
            numberText.Foreground = (Brush)FindResource("SettingsBlueBrush");
            row.Children.Add(numberText);
            var article = new StackPanel();
            Grid.SetColumn(article, 1);
            var heading = DocumentText(headingText, 15, true, 22);
            article.Children.Add(heading);
            var body = DocumentText(Localization.Strings.Get(section.Body), 12, lineHeight: 19, muted: true);
            body.Margin = new(0, 8, 0, 0);
            article.Children.Add(body);
            row.Children.Add(article);
            HelpDetail.Children.Add(new Border
            {
                Child = row, BorderBrush = (Brush)FindResource("SettingsBorderBrush"),
                BorderThickness = new(0, 0, 0, index < sections.Length - 1 ? 1 : 0),
                Padding = new(0, 0, 0, 16), Margin = new(0, 0, 0, 14),
            });
            var entry = new Button
            {
                Content = DocumentText(localizedTitle, 13),
                Style = (Style)FindResource("SettingsHelpTopicStyle"),
                Height = double.NaN, MinHeight = 32, Padding = new(8, 6, 8, 6), CornerRadius = new(8),
                Margin = new(0),
            };
            entry.Click += (_, _) =>
            {
                SelectDocumentEntry(entry);
                row.BringIntoView();
            };
            DocumentTopics.Children.Add(entry);
            if (index < sections.Length - 1)
                DocumentTopics.Children.Add(DocumentDivider(new(8, 4, 8, 4)));
        }
        SelectDocumentEntry(DocumentTopics.Children.OfType<Button>().First());
    }

    private TextBlock DocumentText(string value, double size, bool bold = false, double lineHeight = 20, bool muted = false) =>
        new()
        {
            Text = value, FontFamily = (FontFamily)FindResource("HomeFont"), FontSize = size,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = (Brush)FindResource(muted ? "SettingsMutedBrush" : "SettingsInkBrush"),
            TextWrapping = TextWrapping.Wrap, LineHeight = lineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };

    private Border DocumentDivider(Thickness margin) =>
        new() { Height = 1, Background = (Brush)FindResource("SettingsBorderBrush"), Margin = margin };

    private void SelectDocumentEntry(Button selected)
    {
        foreach (var entry in DocumentTopics.Children.OfType<Button>())
        {
            entry.Background = entry == selected ? (Brush)FindResource("SettingsSoftBlueBrush") : Brushes.Transparent;
            if (entry.Content is TextBlock label)
                label.Foreground = (Brush)FindResource(entry == selected ? "SettingsBlueBrush" : "SettingsInkBrush");
        }
    }

    private void ShowLicenses_Click(object sender, RoutedEventArgs e)
    {
        ShowFeedback();
        SetPage("policy");
        ConfigureHelpColumns(330, 776, 220);
        HelpNavigationCard.Visibility = Visibility.Collapsed;
        HelpDetailCard.Padding = new(16);
        HelpPageTitle.Text = Localization.Strings.Get("开源许可");
        HelpPageSubtitle.Text = Localization.Strings.Get("查看 Windows 客户端使用的第三方组件及许可证");
        HelpTopics.Visibility = FeedbackForm.Visibility = Visibility.Collapsed;
        LicenseNavigation.Visibility = HelpDetail.Visibility = Visibility.Visible;
        LicenseQuery.Text = "";
        RefreshLicenses();
    }

    private void LicenseQuery_Changed(object sender, TextChangedEventArgs e)
    {
        if (LicenseEntries is not null) RefreshLicenses();
    }

    private void RefreshLicenses()
    {
        LicenseEntries.Children.Clear();
        var query = LicenseQuery.Text.Trim();
        var entries = Legal.LicenseCatalog.All.Where(value =>
            $"{value.Name} {value.License}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var license in entries)
        {
            if (LicenseEntries.Children.Count > 0) LicenseEntries.Children.Add(DocumentDivider(new(8, 8, 8, 8)));
            var content = new Grid { Margin = new(8, 6, 8, 6) };
            content.ColumnDefinitions.Add(new() { Width = new(32) });
            content.ColumnDefinitions.Add(new());
            content.ColumnDefinitions.Add(new() { Width = new(20) });
            var initial = DocumentText(license.Name[..1], 14, true);
            initial.Foreground = (Brush)FindResource("SettingsBlueBrush");
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(new Border
            {
                Width = 32, Height = 32, CornerRadius = new(10),
                Background = (Brush)FindResource("SettingsSoftBlueBrush"), Child = initial,
            });
            var labels = new StackPanel { Margin = new(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = DocumentText(license.Name, 13);
            name.Name = "LicenseEntryTitle";
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(name);
            var subtitle = DocumentText(license.License, 11, lineHeight: 16, muted: true);
            subtitle.TextWrapping = TextWrapping.NoWrap;
            subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            subtitle.Margin = new(0, 3, 0, 0);
            labels.Children.Add(subtitle);
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);
            var arrow = new Image { Width = 20, Height = 20, Source = (ImageSource)FindResource("SettingsChevronRight") };
            Grid.SetColumn(arrow, 2);
            content.Children.Add(arrow);
            var entry = new Button
            {
                Content = content, Style = (Style)FindResource("SettingsHelpTopicStyle"),
                MinHeight = 52, Padding = new(0), CornerRadius = new(8),
                ToolTip = $"{license.Name}\n{license.License}",
            };
            System.Windows.Automation.AutomationProperties.SetName(entry, license.Name);
            entry.Click += (_, _) => ShowLicense(license, entry);
            LicenseEntries.Children.Add(entry);
        }
        if (entries.Length > 0) ShowLicense(entries[0], LicenseEntries.Children.OfType<Button>().First());
        else
        {
            var empty = DocumentText(Localization.Strings.Get("没有匹配的组件或许可证"), 12, muted: true);
            empty.Margin = new(0, 20, 0, 0);
            LicenseEntries.Children.Add(empty);
            HelpDetail.Children.Clear();
            HelpDetail.Children.Add(DocumentText(Localization.Strings.Get("没有匹配结果"), 22, true, 30));
            HelpDetail.Children.Add(DocumentText(Localization.Strings.Get("尝试输入组件名称或许可证类型。"), 12, muted: true));
        }
    }

    private void ShowLicense(Legal.LicenseEntry license, Button selected)
    {
        foreach (var entry in LicenseEntries.Children.OfType<Button>())
        {
            var active = entry == selected;
            entry.Background = active ? (Brush)FindResource("SettingsSoftBlueBrush") : Brushes.Transparent;
            var content = (Grid)entry.Content;
            var title = ((StackPanel)content.Children[1]).Children.OfType<TextBlock>().First();
            title.SetResourceReference(TextBlock.ForegroundProperty, active ? "SettingsBlueBrush" : "SettingsInkBrush");
            foreach (var marker in content.Children.OfType<Border>().Where(b => b.Name == "LicenseSelectionMarker").ToArray())
                content.Children.Remove(marker);
            if (active)
            {
                var marker = new Border { Name = "LicenseSelectionMarker", Width = 3, Height = 20,
                    CornerRadius = new(2), HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new(-8, 0, 0, 0) };
                marker.SetResourceReference(Border.BackgroundProperty, "SettingsBlueBrush");
                content.Children.Add(marker);
            }
        }
        HelpDetail.Children.Clear();
        HelpDetail.Children.Add(DocumentText(license.Name, 18, true, 26));
        var licenseType = DocumentText(license.License, 13);
        licenseType.Foreground = (Brush)FindResource("SettingsBlueBrush");
        licenseType.Margin = new(0, 3, 0, 0);
        HelpDetail.Children.Add(licenseType);
        HelpDetail.Children.Add(DocumentDivider(new(0, 14, 0, 14)));
        HelpDetail.Children.Add(DocumentText(Localization.Strings.Get("许可证全文"), 15, true, 22));
        var note = DocumentText(Localization.Strings.Get("下方显示应用随包提供的许可证原文。"), 12, lineHeight: 20, muted: true);
        note.Margin = new(0, 8, 0, 12);
        HelpDetail.Children.Add(note);
        var licenseText = DocumentText(license.Text, 12, lineHeight: 20, muted: true);
        HelpDetail.Children.Add(new Border
        {
            Background = (Brush)FindResource("SettingsCanvasBrush"), Padding = new(16), CornerRadius = new(8),
            Margin = new(0, 0, 0, 20),
            Child = licenseText,
        });
        HelpDetail.Children.Add(DocumentText(Localization.Strings.Get("清单来自本次 Windows 客户端实际打包的组件及字体。"), 11, lineHeight: 16, muted: true));
        HelpDetailScroll.ScrollToTop();
    }
}
