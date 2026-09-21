using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Storage;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifySearchBatch(string directory, string output)
    {
        var ids = Enumerable.Range(0, 110).Select(_ => Guid.NewGuid()).ToArray();
        var chosen = new HashSet<Guid> { ids[2], ids[8] };
        Check(BatchSelection.Toggle(chosen, ids, ids[2], ids[5], true) && chosen.SetEquals(new[] {ids[2],ids[3],ids[4],ids[5],ids[8]}), "range is inclusive and keeps unrelated picks");
        Check(BatchSelection.Toggle(chosen, ids.Reverse().ToArray(), ids[5], ids[2], true), "reverse display order supported");
        var before = chosen.ToHashSet();
        Check(!BatchSelection.Toggle(chosen, ids, ids[0], ids[105], true,100) && chosen.SetEquals(before), "over-limit range is rejected atomically");
        Check(BatchSelection.Toggle(chosen, ids, Guid.NewGuid(), ids[0], true) && chosen.Contains(ids[0]), "missing anchor falls back to individual choice");
        foreach (var theme in new[] {"light","dark"}) foreach (var language in new[] {"zh-CN","en-US","zh-TW"})
        {
            var path = Path.Combine(directory,"search-batch-"+theme+language);
            var main = new MainWindow(false,path);
            MessageSearchWindow? search = null;
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(main,path,"message-history"));
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with {Theme=theme,Language=language}));
                var peer = main.ViewModel.Conversations.First().PeerId;
                WaitForUiTask(main.ViewModel.SelectConversationAsync(peer));
                main.ViewModel.Messages.Clear();
                var db = new BlueLinkDatabase(path,path);
                var text = new ChatItem(Guid.NewGuid(),"selection needle first",false,DateTimeOffset.UtcNow.AddMinutes(-8),MessageStatus.Received);
                var history = text with { Id=Guid.NewGuid(),Text="selection needle older",CreatedAt=text.CreatedAt.AddMinutes(-1) };
                var active = text with { Id=Guid.NewGuid(),Text="selection needle sending",Outgoing=true,Status=MessageStatus.Sending,CreatedAt=text.CreatedAt.AddMinutes(1) };
                var filePath=Path.Combine(path,"search-copy.txt"); File.WriteAllText(filePath,"keep physical file");
                var file=text with { Id=Guid.NewGuid(),Text="",Kind=ChatItemKind.File,Attachments=[new ChatAttachment(Guid.NewGuid(),Guid.NewGuid(),"selection needle.txt","text/plain",18,filePath,"Completed")],CreatedAt=text.CreatedAt.AddMinutes(2) };
                foreach(var item in new[] {text,history,active,file})
                {
                    WaitForUiTask(db.UpsertMessageAsync(new(item.Id.ToString("N"),"peer:"+peer,peer,
                        item.Outgoing ? StoredMessageDirection.Outgoing : StoredMessageDirection.Incoming,
                        item.Attachments?.Count>0 ? StoredMessageType.File : StoredMessageType.Text,item.Text,item.Status.ToString(),item.CreatedAt.ToUnixTimeMilliseconds(),item.CreatedAt.ToUnixTimeMilliseconds())));
                    if(item.Id!=history.Id) main.ViewModel.Messages.Add(item);
                }
                var mainRoot=DetachForRendering(main);Layout(mainRoot,1000,740);
                main.SetMessageSelectionMode(true,text.Id); main.ToggleMessageBatch(file.Id,true);Layout(mainRoot,1000,740);
                Check(main.SelectedBatchMessages.Count==3,"chat shift selects inclusive range");
                var busy=typeof(MainWindow).GetField("_messageBatchRunning",BindingFlags.Instance|BindingFlags.NonPublic)!;
                var update=typeof(MainWindow).GetMethod("UpdateMessageBatch",BindingFlags.Instance|BindingFlags.NonPublic)!;
                busy.SetValue(main,true);update.Invoke(main,null);main.SetMessageSelectionMode(false);
                Check(main.ViewModel.MessageSelectionMode && !((ListBox)main.FindName("MessageList")).IsEnabled,"chat busy batch locks selection and exit");
                busy.SetValue(main,false);update.Invoke(main,null);
                var header=(FrameworkElement)main.FindName("MessageSelectionHeader");
                var footer=(FrameworkElement)main.FindName("MessageBatchPanel");
                Check(header.Visibility==Visibility.Visible && header.TranslatePoint(new Point(),mainRoot).Y<footer.TranslatePoint(new Point(),mainRoot).Y,"chat selection count and cancel above actions");
                Check(((TextBlock)main.FindName("MessageBatchCount")).Foreground == (System.Windows.Media.Brush)mainRoot.FindResource("InkBrush"),"chat selected count uses theme ink");
                Capture(mainRoot,output,$"selection-chat-{theme}-{language}",1000,740);
                main.SetMessageSelectionMode(false);
                search=new MessageSearchWindow("QA",main.ViewModel.Messages,true,main.ViewModel);
                search.ApplyFilter("selection needle",HistoryKind.All);
                WaitForUiTask(search.RefreshResultsAsync(false));
                var root=DetachForRendering(search);Layout(root,720,560);
                var results=(ListBox)search.FindName("Results");
                Check(results.Items.Count==3,"initial snapshot uses current search source");
                // Force real all-history reload, which includes the record absent from the chat window.
                typeof(MessageSearchWindow).GetField("_searchSourceLoaded",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(search,false);
                WaitForUiTask(search.RefreshResultsAsync(false)); Layout(root,720,560);
                Check(results.Items.Count==4,"search loads history-only records");
                var menu=search.BuildResultMenu(file);
                var tags=menu.Items.OfType<MenuItem>().Where(item=>item.Visibility==Visibility.Visible).Select(item=>item.Tag as string).ToArray();
                Check(tags.SequenceEqual(new[]{"message","open","copy","copy-name","select","locate","save","info","delete"}),"desktop search menu navigation first, multiselect before file operations");
                var select=menu.Items.OfType<MenuItem>().Single(item=>item.Tag as string=="select");
                Check(select.Icon is Wpf.Ui.Controls.SymbolIcon,"selection uses distinct checklist icon");
                menu.Width=260;menu.Height=440;Capture(menu,output,$"selection-search-menu-{theme}-{language}",260,440);
                search.SetSearchSelectionMode(true,file.Id);Layout(root,720,560);
                Check(search.SelectedSearchMessages.Single().Id==file.Id,"search selection starts on selected result");
                Check(search.FindName("SearchBatchFiles") is null && ((System.Windows.Controls.Primitives.ButtonBase)search.FindName("SearchBatchCopy")).IsEnabled,"search files use unified copy without duplicate action");
                search.ToggleSearchSelection(history.Id,true);Layout(root,720,560);
                Check(search.SelectedSearchMessages.Count==4,"search range uses displayed result order and reaches history-only record");
                var gesture=(MessageSelectionGesture)typeof(MessageSearchWindow).GetField("_searchSelectionGesture",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(search)!;
                foreach(var upwards in new[]{true,false})
                {
                    var visible=gesture.Rows().Where(row=>gesture.Viewport.Contains(row.Bounds)).ToArray();
                    var anchor=upwards ? visible[^1].Id : visible[0].Id;
                    search.SetSearchSelectionMode(true,anchor);Layout(root,720,560);
                    var endpoint=gesture.RangeTarget!.Value;var side=gesture.RangeButton.VerticalAlignment;
                    ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(gesture.RangeButton).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();Layout(root,720,560);
                    search.ToggleSearchSelection(endpoint,false);Layout(root,720,560);
                    Check(gesture.RangeTarget==endpoint && gesture.RangeButton.VerticalAlignment==side,"search range retains side after selecting range and deselecting endpoint");
                    Check(search.SelectedSearchMessages.Any(item=>item.Id==anchor),"search keeps original anchor selected");
                }
                search.SetSearchSelectionMode(true,file.Id);search.ToggleSearchSelection(history.Id,true);Layout(root,720,560);
                var row=gesture.Rows().First(item=>item.Bounds.Top>=gesture.Viewport.Top);
                var start=new Point(gesture.Viewport.Left+1,row.Bounds.Top+1);
                var end=new Point(start.X,row.Bounds.Bottom-1);
                gesture.Begin(start,null);gesture.Move(end);Layout(root,720,560);
                Check(search.SelectedSearchMessages.Count==4,"search vertical-only drag does not change selection before release");
                gesture.End(end);Layout(root,720,560);
                Check(search.SelectedSearchMessages.Count==3 && search.SelectedSearchMessages.All(item=>item.Id!=row.Id),"search release toggles row without horizontal card intersection");
                gesture.Begin(end,null);gesture.Move(start);gesture.End(start);Layout(root,720,560);
                Check(search.SelectedSearchMessages.Count==4,"search reverse release reselects same row");

                Check(((System.Windows.Controls.Primitives.ButtonBase)search.FindName("SearchBatchCopy")).IsEnabled,"mixed search selection uses same copy action");
                Check(Descendants<Wpf.Ui.Controls.Button>((FrameworkElement)search.FindName("SearchSelectionHeader")).Count()==1,"search selection removes select-all action");
                Check(((WrapPanel)((Border)search.FindName("SearchBatchPanel")).Child).HorizontalAlignment==HorizontalAlignment.Right,"search batch actions align right");
                search.CopySearchSelection(_=>throw new IOException("clipboard unavailable"));
                Check(search.IsBatchSelecting && search.SelectedSearchMessages.Count==4,"search clipboard failure retains selection");
                System.Windows.DataObject? copied=null; search.CopySearchSelection(value=>copied=value); Layout(root,720,560);
                Check(copied?.GetFileDropList().Count==1 && copied.GetText().Contains(history.Text),"search unified copy includes historical text and native file");
                Check(!search.IsBatchSelecting && search.SelectedSearchMessages.Count==0,"successful search copy exits selection");
                ((ToastHost)search.FindName("SearchToasts")).Expire(DateTimeOffset.MaxValue);
                search.SetSearchSelectionMode(true,file.Id);search.ToggleSearchSelection(history.Id,true);Layout(root,720,560);
                Check(((FrameworkElement)search.FindName("SearchSelectionHeader")).Visibility==Visibility.Visible && ((FrameworkElement)search.FindName("SearchFilters")).Visibility==Visibility.Collapsed,"selection replaces filter header");
                Check(((TextBlock)search.FindName("SearchSelectionCount")).Foreground == (System.Windows.Media.Brush)root.FindResource("InkBrush"),"search selected count uses theme ink");
                var selectedRow = Descendants<Border>(results).First(row=>row.Name=="SearchSelectionRow");
                Check(selectedRow.Background==root.FindResource("BatchSelectedRowBrush"),"search selection paints neutral full-row background");
                Check(selectedRow.TranslatePoint(new Point(),results).X<=1,"search selection has no leading group indentation");
                var messageHost=Descendants<Grid>(selectedRow).First(grid=>grid.Name=="SearchMessageHost");
                var selectionBox=Descendants<CheckBox>(selectedRow).Single();
                Check(messageHost.TranslatePoint(new Point(),selectedRow).X-selectionBox.TranslatePoint(new Point(selectionBox.ActualWidth,0),selectedRow).X<=8,"checkbox and message have compact spacing");
                var columns=Descendants<StackPanel>(results).Where(panel=>panel.Name=="SearchMessageColumn").ToArray();
                Check(columns.Any(panel=>panel.HorizontalAlignment==HorizontalAlignment.Left) && columns.Any(panel=>panel.HorizontalAlignment==HorizontalAlignment.Right),"search incoming and outgoing bubble alignment differs");
                Capture(root,output,$"selection-search-{theme}-{language}",720,560);
                Layout(root,620,480); Capture(root,output,$"selection-search-small-{theme}-{language}",620,480);
                var probeMenu=search.BuildResultMenu(file);
                MessageSearchWindow.ConfigureResultMenuPlacement(probeMenu,results,new Point(317,91));
                Check(probeMenu.Placement==PlacementMode.RelativePoint && probeMenu.HorizontalOffset==317 && probeMenu.VerticalOffset==91,"mouse menu anchored to exact click instead of card left edge");
                MessageSearchWindow.ConfigureResultMenuPlacement(probeMenu,results,null);
                Check(probeMenu.Placement==PlacementMode.Bottom && probeMenu.HorizontalOffset==0 && probeMenu.VerticalOffset==0,"keyboard menu remains anchored to result and clears mouse offsets");
                var check=Descendants<CheckBox>(root).First(x=>x.Visibility==Visibility.Visible);
                foreach (var box in Descendants<CheckBox>(results))
                {
                    var host=(Grid)box.Parent;
                    var mark=Descendants<Wpf.Ui.Controls.SymbolIcon>(host).Single(icon=>icon.Name=="SearchBatchCheckMark");
                    Check(mark.Visibility==(box.IsChecked==true ? Visibility.Visible : Visibility.Collapsed),"static search checkmark follows actual checkbox selection");
                    if(box.IsChecked==true)
                    {
                        var nativeBorder=Descendants<Border>(box).First(border=>border.Name=="ControlBorderIconPresenter");
                        var glyphCenter=mark.TranslatePoint(new Point(mark.ActualWidth/2,mark.ActualHeight/2),host);
                        var boxCenter=nativeBorder.TranslatePoint(new Point(nativeBorder.ActualWidth/2,nativeBorder.ActualHeight/2),host);
                        Check(Math.Abs(glyphCenter.X-boxCenter.X)<=1 && Math.Abs(glyphCenter.Y-boxCenter.Y)<=1,"checkmark centered in official checkbox bounds");
                        Check(mark.Clip is null && mark.ActualWidth>0 && mark.ActualHeight>0,"selected checkmark is fully visible without animated clipping");
                        Check(((System.Windows.Media.SolidColorBrush)mark.Foreground).Color.A==255,"selected checkmark keeps opaque themed foreground");
                    }
                }
                var toggle=(System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
                toggle.Toggle();Check(search.SelectedSearchMessages.Count==3,"real search checkbox UIA toggles selected IDs");
                Layout(root,620,480);
                var toggledMark=Descendants<Wpf.Ui.Controls.SymbolIcon>((Grid)check.Parent).Single(icon=>icon.Name=="SearchBatchCheckMark");
                Check(check.IsChecked==false && toggledMark.Visibility==Visibility.Collapsed,"UIA deselection immediately removes static checkmark");
                toggle.Toggle(); Layout(root,620,480);
                Check(check.IsChecked==true && toggledMark.Visibility==Visibility.Visible && toggledMark.Clip is null,"UIA reselection immediately shows complete checkmark");
                search.SetSearchSelectionMode(true,history.Id);search.ToggleSearchSelection(active.Id,false);
                var delete=search.DeleteSearchSelectionAsync();WaitForUiTask(delete);Layout(root,720,560);
                Check(delete.Result.Count==2 && delete.Result.Single(x=>x.Id==history.Id).Deleted && !delete.Result.Single(x=>x.Id==active.Id).Deleted,"search deletes history-only record and retains sending record");
                Check(!search.IsBatchSelecting && search.SelectedSearchMessages.Count==0,"completed search deletion exits selection and preserves skipped records");
                var stored=db.LoadMessagesAsync("peer:"+peer);WaitForUiTask(stored);
                Check(!stored.Result.Any(x=>Guid.Parse(x.MessageId)==history.Id) && File.ReadAllText(filePath)=="keep physical file","search deletion persists and keeps physical attachments");
                WaitForUiTask(search.RefreshResultsAsync(false));Check(results.Items.Count==3,"successful deleted result does not return after refresh");
                search.SetSearchSelectionMode(false);Layout(root,720,560);
                Check(((Wpf.Ui.Controls.TextBox)search.FindName("QueryInput")).Text=="selection needle" && ((FrameworkElement)search.FindName("SearchFilters")).Visibility==Visibility.Visible,"leaving selection preserves query and restores filters");
                Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero && new System.Windows.Interop.WindowInteropHelper(search).Handle==IntPtr.Zero,"selection QA creates no foreground window");
            }
            finally { search?.Close();WaitForUiTask(main.DisposeAsync().AsTask()); }
        }
    }
}
