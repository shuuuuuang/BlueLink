using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Files;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyFileBatch(string directory, string output)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory,"QA-source.pdf"); File.WriteAllText(file,"owned QA source");
        foreach(var status in Enum.GetValues<TransferStatus>()) foreach(var outgoing in new[] {true,false}) foreach(var online in new[] {true,false})
        {
            var item = new TransferItem { Id=Guid.NewGuid(), Name="QA.pdf", TotalBytes=10, LocalPath=file, Status=status, Outgoing=outgoing };
            Check(FileBatchPolicy.Check(item.Id,item,FileBatchAction.Retry,online).Eligible == (outgoing && online && item.IsRetryableTerminal && item.CanRetry),"batch retry rights "+status);
            Check(FileBatchPolicy.Check(item.Id,item,FileBatchAction.Cancel,online).Eligible == (online && item.IsActive),"batch cancel rights "+status);
            Check(FileBatchPolicy.Check(item.Id,item,FileBatchAction.DeleteRecords,online).Eligible == item.CanDelete,"batch delete rights "+status);
            Check(FileBatchPolicy.Check(item.Id,item,FileBatchAction.Copy,online).Eligible == (item.IsCompleted && item.CanOpen),"batch copy rights "+status);
        }
        var ids=Enumerable.Range(0,4).Select(_=>Guid.NewGuid()).ToArray(); var executed=new List<Guid>();
        var task=FileBatchRunner.RunAsync(ids.Concat(ids), id=>Task.FromResult(new FileBatchCheck(id,"QA",id != ids[1],"changed")),id=> {
            executed.Add(id); if(id==ids[2]) throw new IOException("injected per-item error"); return Task.CompletedTask;
        }); WaitForUiTask(task);
        Check(task.Result.Select(item=>item.Outcome).SequenceEqual(new[] {FileBatchOutcome.Submitted,FileBatchOutcome.Skipped,FileBatchOutcome.Failed,FileBatchOutcome.Submitted}),"partial failure continues; duplicate selection dispatched once");
        Check(!executed.Contains(ids[1]) && executed.Count==3,"ineligible item never executes");
        var data=FileDragDropService.CreateCopyDataObject(new[] {file,file});
        Check(data.GetFileDropList().Count==1 && data.GetFileDropList()[0]==file,"native multi-file clipboard deduplicates exact paths without touching clipboard");
        var day=new DateTime(2026,9,14);
        var dated=new[] {day.AddSeconds(-1),day,day.AddDays(1).AddSeconds(-1),day.AddDays(1)}.Select((time,index)=>FileQueryRecord.Capture(new TransferItem {
            Id=Guid.NewGuid(),Name="date-"+index,TotalBytes=1,Outgoing=false,Status=TransferStatus.Completed,CreatedAt=new DateTimeOffset(time) })).ToArray();
        Check(HistorySearch.Files(dated,new FileQueryOptions(Dates:new(day,day))).Length==2,"date range includes both boundary days but excludes adjacent days");
        Check(HistorySearch.Files(dated,new FileQueryOptions(Dates:new(day,null))).Length==3,"start-only range supported");
        Check(HistorySearch.Files(dated,new FileQueryOptions(Dates:new(null,day))).Length==3,"end-only range supported");
        foreach(var theme in new[] {"light","dark"}) foreach(var language in new[] {"zh-CN","en-US","zh-TW"})
        {
            var rootPath=Path.Combine(directory,theme+language); var main=new MainWindow(false,rootPath);
            try {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(main,rootPath,"message-history"));
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme=theme,Language=language }));
                main.ViewModel.AllTransfers.Clear();
                var peerIds=main.ViewModel.Conversations.Take(3).Select(peer=>peer.PeerId).ToArray();
                for(var i=0;i<120;i++) main.ViewModel.AllTransfers.Add(new TransferItem { Id=Guid.NewGuid(),Name=$"QA-{i}.pdf",Status=TransferStatus.Completed,LocalPath=file,PeerId=peerIds[i%peerIds.Length],TotalBytes=i,CreatedAt=DateTimeOffset.UnixEpoch.AddMinutes(i),Outgoing=false });
                main.OpenFileWorkspace(true); var root=DetachForRendering(main); Layout(root,1000,740);
                var list=(ListBox)main.FindName("TransferList");
                var toolbar=(FrameworkElement)main.FindName("FullTransferContent");
                Check(toolbar.ActualHeight <= 60,"default file header is one compact search row");
                Check(main.FindName("FileFilterOverlay") is null && main.FindName("FileFilterSummaryRow") is null,"column filters need no independent panel or summary row");
                Capture(root,output,$"compact-files-{theme}-{language}",1000,740);
                var initialRowWidth = Descendants<Border>(list).First(row=>row.Name=="FileTableRow").ActualWidth;
                var initialHeaderWidth = ((Grid)main.FindName("FileColumnsHeader")).ActualWidth;
                SetFileColumn(main,"Status","Failed"); Layout(root,1000,740);
                Check(Math.Abs(((Grid)main.FindName("FileColumnsHeader")).ActualWidth-initialHeaderWidth)<.1,"empty filter preserves column widths and reserved scrollbar lane");
                ResetFileColumn(main,"Status"); Layout(root,1000,740);
                Check(Math.Abs(((Grid)main.FindName("FileColumnsHeader")).ActualWidth-initialHeaderWidth)<.1,"restoring many files does not change column widths");
                var dateEditor = main.CreateFileColumnEditor("Date");
                Check(dateEditor.MatchingDates.SetEquals(new[] { DateTimeOffset.UnixEpoch.LocalDateTime.Date }),"file calendar marks matching file days ignoring the date range");
                dateEditor = new FileFilterEditor([],[],dateEditor:true,matchingDates:new HashSet<DateTime> { DateTime.Today, DateTime.Today.AddDays(-2) });
                var calendar = dateEditor.StartDate.Calendar;
                ((Viewbox)calendar.Parent).Child=null; calendar.DisplayDate=DateTime.Today;
                var calendarRoot = new Grid { Width=320, Height=330 }; calendarRoot.Resources.MergedDictionaries.Add(root.Resources); calendarRoot.Children.Add(calendar);
                Capture(calendarRoot,output,$"file-calendar-{theme}-{language}",320,330);
                Check(calendar.IsTodayHighlighted && calendar.CalendarDayButtonStyle is not null,"file picker preserves today styling and record markers");
                Check(Descendants<CalendarDayButton>(calendar).Any(day=>day.IsToday),"file picker realizes the today cell");
                Check(Descendants<System.Windows.Shapes.Ellipse>(calendar).Count(dot=>dot.Name=="RecordDateMarker" && dot.Visibility==Visibility.Visible)==2,"file calendar draws only matching day dots including today");
                var listTop=list.TranslatePoint(new Point(),root).Y;
                void ClearColumn(string column) => ResetFileColumn(main,column);
                SetFileColumn(main,"Status","Failed"); Check(list.Items.Count==0,"status column filters results"); ClearColumn("Status");
                SetFileColumn(main,"Kind","Images"); Check(list.Items.Count==0,"name column filters file type"); ClearColumn("Kind");
                SetFileColumn(main,"Route","direction:Outgoing"); Check(list.Items.Count==0,"route column filters outgoing files"); ClearColumn("Route");
                SetFileDates(main,DateTime.Today.AddDays(1),null); Check(list.Items.Count==0,"time column filters dates"); ClearColumn("Date");
                SetFileColumn(main,"Route","peer:"+peerIds[0],"peer:"+peerIds[1]);
                Check(list.Items.Count==80,"multiple device choices combine with OR"); ClearColumn("Route");
                var routeDraft=main.CreateFileColumnEditor("Route");
                routeDraft.Choices["peer:@all"].IsChecked=true;
                routeDraft.Choices["peer:"+peerIds[0]].IsChecked=true;
                Check(routeDraft.Choices["peer:@all"].IsChecked==false,"specific device deselects all-devices choice");
                var changed=main.ViewModel.AllTransfers.First(); changed.Status=TransferStatus.Failed;
                SetFileColumn(main,"Status","Failed"); Check(list.Items.Count==1,"single status selects only failed record");
                Layout(root,1000,740);
                Check(Math.Abs(Descendants<Border>(list).First(row=>row.Name=="FileTableRow").ActualWidth-initialRowWidth)<.1,"single row without scrollbar preserves widths from overflowing list");
                SetFileColumn(main,"Status","Failed","Completed"); Check(list.Items.Count==100,"OR includes failed and completed records");
                changed.Status=TransferStatus.Completed; ClearColumn("Status");
                var draft=main.CreateFileColumnEditor("Status");
                draft.Choices["Failed"].IsChecked=true;
                Check(list.Items.Count==100,"unchecked confirmation leaves results unchanged");
                Check(!main.CreateFileColumnEditor("Status").Choices["Failed"].IsChecked!.Value,"dismissed draft never leaks into next editor");
                var dates=main.CreateFileColumnEditor("Date");
                dates.StartDate.SelectedDate=DateTime.Today; dates.EndDate.SelectedDate=DateTime.Today.AddDays(-1);
                Check(dates.StartDate.SelectedDate is null && dates.EndDate.SelectedDate==DateTime.Today.AddDays(-1) && dates.ConfirmButton.IsEnabled,"conflicting end date clears start and remains confirmable");
                dates.EndDate.SelectedDate=DateTime.Today.AddDays(1); Check(dates.ConfirmButton.IsEnabled,"valid bounded dates can be confirmed");
                SetFileColumn(main,"Status","Completed","Failed");
                Check(list.Items.Count==100,"multiple status choices use OR semantics");
                SetFileColumn(main,"Kind","Files"); ClearColumn("Kind");
                Check(main.CreateFileColumnEditor("Status").Selected.ToHashSet().SetEquals(new[] {"Completed","Failed"}),"resetting one column preserves another selection");
                Layout(root,1000,740);
                Check(Math.Abs(list.TranslatePoint(new Point(),root).Y-listTop)<1,"active filters consume no additional vertical space");
                Check(((FileTableHeader)main.FindName("FileStatusHeader")).FilterActive,"active status marked on its column");
                Capture(root,output,$"compact-column-filtered-{theme}-{language}",1000,740);
                CaptureFileColumnEditors(main,output,$"column-editor-{theme}-{language}");
                ClearColumn("Status");
                void SortHeader(string name) => ((FileTableHeader)main.FindName(name)).SortButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                SortHeader("FileNameHeader"); Layout(root,1000,740);
                Check(((TransferItem)list.Items[0]).Name=="QA-0.pdf" && ((FileTableHeader)main.FindName("FileNameHeader")).Descending==false,"name header sorts ascending with visible indicator");
                SortHeader("FileNameHeader");
                Check(((FileTableHeader)main.FindName("FileNameHeader")).Descending==true,"repeated header click reverses direction");
                SortHeader("FileSizeHeader");
                Check(((TransferItem)list.Items[0]).TotalBytes==119,"size header defaults to largest first");
                SortHeader("FileSizeHeader");
                Check(((TransferItem)list.Items[0]).TotalBytes==0,"size header toggles smallest first");
                SortHeader("FileTimeHeader");
                Check(((TransferItem)list.Items[0]).CreatedAt==DateTimeOffset.UnixEpoch.AddMinutes(119),"time header defaults to latest first");
                SortHeader("FileTimeHeader");
                Check(((TransferItem)list.Items[0]).CreatedAt==DateTimeOffset.UnixEpoch,"time header toggles oldest first");
                Check(new[] {"FileNameHeader","FileSizeHeader","FileTimeHeader"}.Count(name=>((FileTableHeader)main.FindName(name)).Descending is not null)==1,"only the active sort triangle is highlighted");
                Check(((FrameworkElement)main.FindName("FullTransferContent")).ActualHeight<=60,"sorting does not add a second toolbar row");
                SetFileColumn(main,"Kind","Files");
                typeof(MainWindow).GetMethod("FileClear_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,[main,new RoutedEventArgs()]);
                Check(((TransferItem)list.Items[0]).CreatedAt==DateTimeOffset.UnixEpoch,"clearing filters preserves column sort");
                Capture(root,output,$"compact-header-sort-{theme}-{language}",1000,740);
                main.SetFileSelectionMode(true);
                list.SelectedItems.Add(list.Items[0]); list.SelectedItems.Add(list.Items[1]); Layout(root,1000,740);
                Check(list.SelectedItems.Count==2,"two file rows selected");
                Check(((FrameworkElement)main.FindName("FileBatchPanel")).Visibility==Visibility.Visible,"batch toolbar visible");
                Check(list.SelectionMode==SelectionMode.Multiple,"explicit multi-selection mode");
                var batchPanel=(FrameworkElement)main.FindName("FileBatchPanel");
                var footer=(FrameworkElement)main.FindName("FileFooter");
                Check(batchPanel.ActualHeight<=52 && batchPanel.TranslatePoint(new Point(),footer).Y>=0,"batch controls occupy the existing fixed footer");
                Check(((FrameworkElement)main.FindName("FileToolbar")).Visibility==Visibility.Collapsed && ((FrameworkElement)main.FindName("FileFooterDefault")).Visibility==Visibility.Collapsed,"batch mode replaces normal tools instead of stacking them");
                var actions=((Panel)main.FindName("FileBatchActions")).Children.OfType<Wpf.Ui.Controls.Button>().ToArray();
                Check(actions.Length==4 && actions.Where(item=>item.Tag as string is "Copy" or "DeleteRecords").All(item=>item.IsEnabled),"completed files offer copy and record deletion");
                Check(actions.Where(item=>item.Tag as string is "Retry" or "Cancel").All(item=>!item.IsEnabled),"completed files cannot retry or cancel");
                Check(((FrameworkElement)main.FindName("FileSelectionHeader")).Visibility==Visibility.Visible,"selection count/cancel header visible");
                Check(batchPanel.HorizontalAlignment==HorizontalAlignment.Right,"file batch actions align right");
                Check(Descendants<Wpf.Ui.Controls.Button>((FrameworkElement)main.FindName("FileSelectionHeader")).Count()==1,"file batch header has no first-100 action");
                var fileGesture=(MessageSelectionGesture)typeof(MainWindow).GetField("_fileSelectionGesture",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main)!;
                Check(fileGesture.RangeTarget.HasValue && fileGesture.RangeButton.Visibility==Visibility.Visible,"file multiselect exposes selection-to-here for fully visible rows");
                var rangeEnd=fileGesture.RangeTarget!.Value; var rangeSide=fileGesture.RangeButton.VerticalAlignment;
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(fileGesture.RangeButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke(); Layout(root,1000,740);
                Check(list.SelectedItems.OfType<TransferItem>().Any(item=>item.Id==rangeEnd),"file range button selects endpoint through UIA");
                main.ToggleFileBatch(rangeEnd,false); Layout(root,1000,740);
                Check(fileGesture.RangeTarget==rangeEnd && fileGesture.RangeButton.VerticalAlignment==rangeSide,"file range direction survives endpoint deselection");
                var unselectedRow=Descendants<Border>(list).First(row=>row.Name=="FileTableRow" && row.DataContext is TransferItem entry && !list.SelectedItems.Contains(entry));
                var selectedIds=list.SelectedItems.OfType<TransferItem>().Select(item=>item.Id).ToHashSet();
                var rightClick=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Right) { RoutedEvent=UIElement.PreviewMouseRightButtonDownEvent };
                unselectedRow.RaiseEvent(rightClick);
                Check(rightClick.Handled && selectedIds.SetEquals(list.SelectedItems.OfType<TransferItem>().Select(item=>item.Id)),"right-clicking file never mutates multi-selection");
                var selectedRow=Descendants<Border>(list).First(row=>row.Name=="FileTableRow" && row.DataContext is TransferItem entry && list.SelectedItems.Contains(entry));
                Check(selectedRow.Background==root.FindResource("BatchSelectedRowBrush"),"file selection uses neutral row background");
                Check(selectedRow.TranslatePoint(new Point(),list).X<=1,"file selected background reaches leading edge");
                main.SetFileSelectionMode(true);list.SelectedItems.Add(list.Items[0]);list.SelectedItems.Add(list.Items[1]);
                var displayed=list.Items.OfType<TransferItem>().ToArray();
                main.ToggleFileBatch(displayed[4].Id,true); Layout(root,1000,740);
                // An initial keyboard/UIA selection has no pointer anchor; the next plain click establishes one.
                main.ToggleFileBatch(displayed[4].Id,false); main.ToggleFileBatch(displayed[1].Id,true);
                Check(list.SelectedItems.Count>=4,"file shift range uses current displayed order");
                main.SetFileSelectionMode(true); list.SelectedItems.Add(list.Items[0]); list.SelectedItems.Add(list.Items[1]);
                var firstSource=main.ViewModel.AllTransfers[0];
                main.ViewModel.AllTransfers[0]=new TransferItem { Id=firstSource.Id, Name=firstSource.Name, LocalPath=firstSource.LocalPath,
                    MimeType=firstSource.MimeType, TotalBytes=firstSource.TotalBytes, CompletedBytes=firstSource.CompletedBytes,
                    Status=firstSource.Status, Outgoing=firstSource.Outgoing, PeerId=firstSource.PeerId, CreatedAt=firstSource.CreatedAt };
                Layout(root,1000,740);
                Check(list.SelectedItems.Count==2,"replacement transfer snapshot keeps selected stable IDs");
                var busy=typeof(MainWindow).GetField("_fileBatchRunning",BindingFlags.Instance|BindingFlags.NonPublic)!;
                var update=typeof(MainWindow).GetMethod("UpdateFileSelection",BindingFlags.Instance|BindingFlags.NonPublic)!;
                busy.SetValue(main,true);update.Invoke(main,null);
                Check(!list.IsEnabled && actions.All(item=>!item.IsEnabled),"file batch locks selection and actions while running");
                busy.SetValue(main,false);update.Invoke(main,null);
                Check(((TextBlock)main.FindName("FileSelectionCount")).Foreground == (System.Windows.Media.Brush)root.FindResource("InkBrush"),"file selected count uses theme ink");
                Capture(root,output,$"batch-{theme}-{language}",1000,740);
                var deleteTask=main.ViewModel.RunFileBatchAsync(new[] {((TransferItem)list.Items[0]).Id,Guid.NewGuid()},FileBatchAction.DeleteRecords);
                WaitForUiTask(deleteTask);
                Check(deleteTask.Result[0].Outcome==FileBatchOutcome.Submitted && deleteTask.Result[1].Outcome==FileBatchOutcome.Skipped,"production batch reports deletion and vanished record separately");
                Check(File.Exists(file) && File.ReadAllText(file)=="owned QA source","deleting history never deletes the real file");
                main.SetFileSelectionMode(false); Check(list.SelectionMode==SelectionMode.Single && list.SelectedItems.Count==0,"leave batch clears selection");
                Check(actions.All(item=>!item.IsEnabled),"batch actions disabled when selection is empty");
                Check(((FrameworkElement)main.FindName("FileToolbar")).Visibility==Visibility.Visible,"leaving selection restores search");
                Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,"batch QA never activates desktop");
            } finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
            foreach(var action in Enum.GetValues<FileBatchAction>()) {
                var confirmation = new ConfirmationWindow(MainWindow.FileBatchDocument(18,16,action));
                try { var content=DetachForRendering(confirmation);
                    content.Measure(new Size(BlueLink.Presentation.WindowsDialogLayout.Width,double.PositiveInfinity));
                    var height=(int)Math.Ceiling(Math.Max(confirmation.MinHeight,content.DesiredSize.Height));
                    Capture(content,output,$"batch-prompt-{theme}-{language}-{action}",(int)confirmation.Width,height);
                    if(action==FileBatchAction.DeleteRecords)
                    {
                        var document=(ConfirmationDocument)confirmation.DataContext;
                        Check(document.Title==BlueLink.Localization.Strings.Get("删除本机记录") && document.PrimaryText==BlueLink.Localization.Strings.Get("删除"),"file delete reuses message delete title and action semantics");
                        Check(document.Question.Contains("18") && !document.Question.Contains("QA") && height<=300,"actual file confirmation uses count-only production text at its natural compact height");
                    }
                    Check(((FrameworkElement)confirmation.FindName("ConfirmationSecondaryButton")).Visibility==Visibility.Collapsed,"no blank secondary action");
                    Check(((FrameworkElement)confirmation.FindName("ConfirmationPrimaryButton")).Visibility==Visibility.Visible,"batch confirmation action reachable");
                } finally { confirmation.Close(); }
            }
        }
    }
    private static void SetFileColumn(MainWindow window,string column,params string[] selected)
    {
        var editor=window.CreateFileColumnEditor(column);
        foreach(var box in editor.Choices.Values) box.IsChecked=false;
        foreach(var key in selected) editor.Choices[key].IsChecked=true;
        if(editor.ConfirmButton.IsEnabled) editor.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static void ResetFileColumn(MainWindow window,string column)
    {
        var editor=window.CreateFileColumnEditor(column);
        editor.ResetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static void SetFileDates(MainWindow window,DateTime? start,DateTime? end)
    {
        var editor=window.CreateFileColumnEditor("Date"); editor.StartDate.SelectedDate=start; editor.EndDate.SelectedDate=end;
        if(editor.ConfirmButton.IsEnabled) editor.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private void CaptureFileColumnEditors(MainWindow window,string output,string prefix)
    {
        foreach(var column in new[] {"Kind","Route","Status","Date"})
        {
            var editor=window.CreateFileColumnEditor(column);
            editor.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
            var size=editor.DesiredSize;
            Capture(editor,output,prefix+"-"+column,(int)Math.Ceiling(size.Width),(int)Math.Ceiling(size.Height));
            Check(!Descendants<ComboBox>(editor).Any(),"column choices have no nested dropdowns");
        }
    }
}
