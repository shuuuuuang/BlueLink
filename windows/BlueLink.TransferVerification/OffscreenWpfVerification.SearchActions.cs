using System.IO;
using System.Windows;
using System.Windows.Controls;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Domain;
using BlueLink.Storage;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifySearchActionMenus(string dataRoot, string output, bool menusOnly = false)
    {
        var directory = Path.Combine(dataRoot, "search-action-menus");
        var main = new MainWindow(initializeRuntime: false, dataRoot: directory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, directory, "message-history"));
            foreach (var theme in new[] { "light", "dark" })
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            {
                AppearanceService.Apply(BlueLinkSettings.Defaults(directory) with { Theme = theme, Language = language });
                var search = new MessageSearchWindow("QA 搜索", main.ViewModel.Messages, true, main.ViewModel);
                try
                {
                    var root = DetachForRendering(search);
                    search.ApplyFilter("", HistoryKind.All);
                    if (!menusOnly)
                    {
                    Capture(root, output, $"search-actions-{theme}-{language}", 620, 480);
                    Check(Descendants<Wpf.Ui.Controls.Button>(root).Any(b => b.ToolTip as string == BlueLink.Localization.Strings.Get("搜索结果操作")), "visible result actions at minimum search size: " + theme + language);
                    Check(((TextBlock)search.FindName("SearchTitle")).Foreground is System.Windows.Media.SolidColorBrush heading &&
                        heading.Color == ((System.Windows.Media.SolidColorBrush)root.FindResource("InkBrush")).Color,
                        "search heading follows theme contrast: " + theme + language);
                    var selectedRow = Descendants<ListBoxItem>(root).First();
                    string Backgrounds() => string.Join(";", Descendants<Border>(selectedRow).Select(b => b.Name + ":" + b.Background));
                    var normalBackgrounds = Backgrounds();
                    var card = Descendants<Border>(selectedRow).First(b => b.Name == "ResultCard");
                    SetInteractionState(selectedRow, "IsMouseOver", true);
                    SetInteractionState(card, "IsMouseOver", true);
                    Layout(root, 620, 480);
                    Check(Backgrounds() == normalBackgrounds, "hover adds no card background: " + theme + language + " normal=" + normalBackgrounds + " hover=" + Backgrounds());
                    Capture(root, output, $"search-hover-{theme}-{language}", 620, 480);
                    SetInteractionState(card, "IsMouseOver", false);
                    SetInteractionState(selectedRow, "IsMouseOver", false);
                    selectedRow.IsSelected = true;
                    Layout(root, 620, 480);
                    Check(Backgrounds() == normalBackgrounds, "selection adds no card background: " + theme + language);
                    var selectedTitle = Descendants<TextBlock>(selectedRow).First(t => !string.IsNullOrEmpty(HighlightedText.GetText(t)));
                    Check(selectedTitle.Foreground is System.Windows.Media.SolidColorBrush ink &&
                        ink.Color == ((System.Windows.Media.SolidColorBrush)root.FindResource("InkBrush")).Color,
                        "selected result title remains readable: " + theme + language);
                    Capture(root, output, $"search-selected-{theme}-{language}", 620, 480);
                    }
                    var records = new[] {
                        main.ViewModel.Messages.First(m => m.HasText),
                        main.ViewModel.Messages.First(m => m.Attachments?.Any(a => a.CanOpen) == true),
                        new ChatItem(Guid.NewGuid(), "", false, DateTimeOffset.Now, MessageStatus.Received, ChatItemKind.File,
                            [new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "QA-active.pdf", "application/pdf", 1024, State: "Transferring")])
                    };
                    for (var index = 0; index < records.Length; index++)
                    {
                        var menu = search.BuildResultMenu(records[index]);
                        var visible = menu.Items.OfType<MenuItem>().Where(i => i.Visibility == Visibility.Visible).ToArray();
                        menu.Width = 320; menu.Height = visible.Length * 36 + 90;
                        Capture(menu, output, $"search-menu-{theme}-{language}-{index}", (int)menu.Width, (int)menu.Height);
                        Check(visible.All(i => !string.IsNullOrWhiteSpace(i.Header as string) && i.ActualHeight >= 36), "search menu labels and target heights: " + theme + language + index);
                        Check(visible.Count(i => i.Tag as string == "copy-name") == (index == 0 ? 0 : 1), "one filename copy in each file menu: " + theme + language + index);
                        Check(visible.Any(i => i.Tag as string == "message"), "locate remains an explicit action: " + theme + language + index);
                    }
                }
                finally { search.Close(); }
            }
        }
        finally
        {
            AppearanceService.Apply(BlueLinkSettings.Defaults(directory) with { Theme = "light", Language = "zh-CN" });
            WaitForUiTask(main.DisposeAsync().AsTask());
        }
    }
}
