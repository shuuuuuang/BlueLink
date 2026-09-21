using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Localization;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyDateFilters(string directory, string output)
    {
        foreach (var theme in new[] { "light", "dark" })
        foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
        {
            var main = new MainWindow(false, Path.Combine(directory, "dates-" + theme + language));
            MessageSearchWindow? search = null;
            try
            {
                WaitForUiTask(main.ViewModel.InitializeLocalStateAsync());
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme = theme, Language = language }));
                var today = DateTime.Today;
                var messages = new ObservableCollection<ChatItem>(Enumerable.Range(0, 3).Select(i =>
                    new ChatItem(Guid.NewGuid(), "date-filter needle " + i, i == 0, new DateTimeOffset(today.AddDays(-i).AddHours(12)), MessageStatus.Delivered)));
                search = new MessageSearchWindow("QA", messages);
                var root = DetachForRendering(search);
                var start = (RecordDatePicker)search.FindName("DateInput");
                var end = (RecordDatePicker)search.FindName("EndDateInput");
                var clear = (Wpf.Ui.Controls.Button)search.FindName("ClearDateFilters");
                var list = (ListBox)search.FindName("Results");
                search.ApplyFilter("needle", HistoryKind.Text);
                Capture(root,output,$"date-search-empty-{theme}-{language}",620,540);
                var fullWidth = list.ActualWidth;
                Check(start.SelectedDate is null && end.SelectedDate is null && clear.Visibility == Visibility.Collapsed,
                    "search starts without implicit date selection or a clear-dates button");
                Check(search.FindName("CalendarPane") is null && fullWidth >= 580 && !Descendants<Calendar>(root).Any(),
                    "calendar is absent from normal layout and reserves no permanent sidebar");
                Check(!Descendants<TextBox>(start).Any() && !Descendants<TextBox>(end).Any() &&
                    new ButtonAutomationPeer(start.Trigger).GetPattern(PatternInterface.Value) is null,
                    "date controls have selection-button semantics with no editable value provider");
                start.SelectedDate = today.AddDays(-1);
                WaitForUiTask(search.RefreshResultsAsync(false)); Layout(root,620,540);
                Check(list.Items.Count == 2 && clear.Visibility == Visibility.Visible && end.Calendar.DisplayDateStart is null,
                    "start-only date filtering is inclusive and constrains no hidden dates");
                end.SelectedDate = today.AddDays(-1);
                WaitForUiTask(search.RefreshResultsAsync(false));
                Capture(root,output,$"date-search-active-{theme}-{language}",620,540);
                Check(list.Items.Count == 1 && start.Calendar.DisplayDateEnd is null && list.ActualWidth == fullWidth,
                    "inclusive same-day range retains the full result width");
                var count = (FrameworkElement)search.FindName("ResultCount");
                Check(clear.TranslatePoint(new Point(clear.ActualWidth,0),root).X <= count.TranslatePoint(new Point(),root).X,
                    "date actions never overlap result count at minimum width in each language");
                var kinds=(FrameworkElement)search.FindName("SearchKinds");
                Check(Math.Abs(kinds.TranslatePoint(new Point(0,kinds.ActualHeight/2),root).Y-count.TranslatePoint(new Point(0,count.ActualHeight/2),root).Y)<1 &&
                    Math.Abs(start.TranslatePoint(new Point(0,start.ActualHeight/2),root).Y-count.TranslatePoint(new Point(0,count.ActualHeight/2),root).Y)<1,
                    "categories, date range and result count share one horizontal row");
                Capture(root,output,$"date-toolbar-wide-{theme}-{language}",860,540);
                start.SelectedDate=today;
                Check(end.SelectedDate is null && start.SelectedDate==today,"new conflicting start clears only the previous end date");
                end.SelectedDate=today.AddDays(-2);
                Check(start.SelectedDate is null && end.SelectedDate==today.AddDays(-2),"new conflicting end clears only the previous start date");
                ((IInvokeProvider)new ButtonAutomationPeer(clear).GetPattern(PatternInterface.Invoke)).Invoke(); DrainDispatcher();
                WaitForUiTask(search.RefreshResultsAsync(false)); Layout(root,620,540);
                Check(start.SelectedDate is null && end.SelectedDate is null && list.Items.Count == 3 &&
                    clear.Visibility == Visibility.Collapsed && ((Wpf.Ui.Controls.TextBox)search.FindName("QueryInput")).Text == "needle" &&
                    ((StackPanel)search.FindName("SearchKinds")).Children.OfType<RadioButton>().Single(b=>b.IsChecked==true).Tag as string == "Text",
                    "clear-dates UIA action resets both boundaries while preserving query and message type");
                end.SelectedDate = today.AddDays(-1);
                WaitForUiTask(search.RefreshResultsAsync(false));
                Check(list.Items.Count == 2 && start.Calendar.DisplayDateEnd is null,
                    "end-only date filtering includes the entire end day");
                end.SelectedDate = null;
                WaitForUiTask(search.RefreshResultsAsync(false));
                start.CalendarPopup.Child = null;
                start.Calendar.DisplayDate = today;
                var calendarPanel = start.CalendarPanel;
                Capture(calendarPanel,output,$"date-calendar-{theme}-{language}",306,350);
                var day = Descendants<CalendarDayButton>(calendarPanel).Single(b=>b.DataContext is DateTime date && date == today.AddDays(-1));
                var selection = (ISelectionItemProvider)new CalendarAutomationPeer(start.Calendar).GetChildren().OfType<DateTimeAutomationPeer>().First(peer => peer.GetName() == today.AddDays(-1).ToString("D", start.Calendar.Language.GetSpecificCulture())).GetPattern(PatternInterface.SelectionItem);
                selection.Select(); DrainDispatcher();
                WaitForUiTask(search.RefreshResultsAsync(false));
                Check(start.SelectedDate == today.AddDays(-1) && list.Items.Count == 2 && !start.CalendarPopup.IsOpen,
                    "actual calendar day UIA selection applies the date and closes the floating calendar");
                Capture(calendarPanel,output,$"date-calendar-selected-{theme}-{language}",306,350);
                var visibleDays=Descendants<CalendarDayButton>(calendarPanel).Where(d=>d.Visibility==Visibility.Visible).ToArray();
                Check(visibleDays.Length==42 && visibleDays.All(d=>d.IsEnabled && !d.IsBlackedOut),"all adjacent-month and conflicting dates remain visible and selectable");
                Check(visibleDays.All(d=>Math.Abs(d.ActualWidth-d.ActualHeight)<.1),"calendar day hit areas are square");
                var highlights=Descendants<System.Windows.Shapes.Rectangle>(calendarPanel).Where(e=>e.Name is "TodayBackground" or "SelectedBackground").ToArray();
                Check(highlights.Length>0 && highlights.All(e=>Math.Abs(e.ActualWidth-e.ActualHeight)<.1 && e.RadiusX>=e.ActualWidth/2 && e.RadiusY>=e.ActualHeight/2),"official selected and today highlights retain circular geometry");
                var marker = Descendants<System.Windows.Shapes.Ellipse>(day).Single(e=>e.Name=="RecordDateMarker");
                Check(marker.Visibility == Visibility.Visible && start.MatchingDates!.Count == 3,
                    "shared calendar shows record markers independently of active date boundaries");
                var editor = new FileFilterEditor([],[],dateEditor:true,title:Strings.Get("日期范围"),matchingDates:search.MatchingDates);
                editor.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                Capture(editor,output,$"date-file-filter-{theme}-{language}",(int)Math.Ceiling(editor.DesiredSize.Width),(int)Math.Ceiling(editor.DesiredSize.Height));
                Check(editor.ActualHeight <= 220 && editor.StartDate.Trigger.ActualWidth >= 280,
                    "date filter uses full-width rows and compact natural panel height");
                Check(!Descendants<TextBox>(editor).Any() && !Descendants<DatePicker>(editor).Any() && editor.StartDate.Calendar.Style == start.Calendar.Style &&
                    editor.StartDate.Calendar.CalendarDayButtonStyle == start.Calendar.CalendarDayButtonStyle,
                    "file and message date filters use exactly the same official calendar styles without text entry");
                editor.StartDate.SelectedDate = today.AddDays(-1);
                Capture(editor,output,$"date-file-filter-active-{theme}-{language}",(int)Math.Ceiling(editor.ActualWidth),(int)Math.Ceiling(editor.ActualHeight));
                Check(editor.ConfirmButton.IsEnabled && editor.EndDate.Calendar.DisplayDateStart is null,
                    "file date draft becomes confirmable after selecting a valid boundary");
                var draft=main.CreateFileColumnEditor("Date");
                var parentPopup=new Popup { StaysOpen=false, Child=draft };
                draft.ManageCalendarPopups(parentPopup);
                draft.StartDate.BeginCalendarInteraction();
                Check(parentPopup.StaysOpen,"parent filter suspends outside dismissal before child calendar takes capture");
                draft.StartDate.CalendarPopup.Child=null;
                draft.StartDate.Calendar.DisplayDate=today;
                Layout(draft.StartDate.CalendarPanel,306,350);
                var fileDay=(ISelectionItemProvider)new CalendarAutomationPeer(draft.StartDate.Calendar).GetChildren().OfType<DateTimeAutomationPeer>()
                    .Single(peer=>peer.GetName()==today.ToString("D",draft.StartDate.Calendar.Language.GetSpecificCulture())).GetPattern(PatternInterface.SelectionItem);
                fileDay.Select(); DrainDispatcher();
                Check(draft.StartDate.SelectedDate==today && draft.ConfirmButton.IsEnabled && !parentPopup.StaysOpen,
                    "file calendar selection survives child dismissal and restores parent outside-click policy");
                Check(!main.CreateFileColumnEditor("Date").Dates.IsActive,"calendar changes remain a draft until Confirm");
                ((IInvokeProvider)new ButtonAutomationPeer(draft.ConfirmButton).GetPattern(PatternInterface.Invoke)).Invoke(); DrainDispatcher();
                Check(main.CreateFileColumnEditor("Date").StartDate.SelectedDate==today,"confirm commits the chosen file date and reopening preserves it");
                draft.EndDate.SelectedDate=today.AddDays(-1);
                Check(draft.StartDate.SelectedDate is null && draft.EndDate.SelectedDate==today.AddDays(-1),"file range conflict clears the other endpoint");
                draft.StartDate.SelectedDate=today.AddDays(1);
                Check(draft.EndDate.SelectedDate is null && draft.StartDate.SelectedDate==today.AddDays(1),"file range conflict is symmetric");
                draft.StartDate.BeginCalendarInteraction(); draft.StartDate.ClosePopup(); draft.EndDate.BeginCalendarInteraction(); DrainDispatcher();
                Check(parentPopup.StaysOpen,"quickly switching calendars does not reenable parent capture between popups");
                draft.EndDate.ClosePopup(); DrainDispatcher(); parentPopup.Child=null;
                var summary=MainWindow.FileBatchSummary(100,92);
                Check(summary.Split(Environment.NewLine).Length==2 && summary.Contains("100") && summary.Contains("92") && summary.Contains("8"),
                    "batch confirmation stays at two count lines even for a hundred files");
                Check(new WindowInteropHelper(search).Handle==IntPtr.Zero && new WindowInteropHelper(main).Handle==IntPtr.Zero &&
                    PresentationSource.FromVisual(calendarPanel) is null,"date verification renders actual controls without native foreground windows");
            }
            finally { search?.Close(); WaitForUiTask(main.DisposeAsync().AsTask()); }
        }
    }
}
