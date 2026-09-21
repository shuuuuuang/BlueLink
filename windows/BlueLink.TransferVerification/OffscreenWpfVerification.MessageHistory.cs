using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Storage;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyAutomaticMessageHistory(string directory, string output)
    {
        var path = Path.Combine(directory, "automatic-message-history");
        var main = new MainWindow(false, path);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, path, "message-history"));
            var peer = main.ViewModel.Conversations.First(item => !item.IsConnected).PeerId;
            var database = new BlueLinkDatabase(path, path);
            var seed = Seed(); WaitForUiTask(seed);
            WaitForUiTask(main.ViewModel.SelectConversationAsync(peer));
            var root = DetachForRendering(main); Layout(root, 1000, 740);
            typeof(MainWindow).GetMethod("AttachMessageScrollViewer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            Layout(root, 1000, 740);
            var list = (ListBox)main.FindName("MessageList");
            var scroll = Descendants<ScrollViewer>(list).First();
            Check(main.ViewModel.Messages.Count == 200, "conversation initially loads one bounded history page");
            Check(!Descendants<Wpf.Ui.Controls.Button>(root).Any(button => Equals(button.Content, "加载更早消息")), "manual earlier-message button is absent");
            Check(Math.Abs(scroll.VerticalOffset-scroll.ScrollableHeight)<1, "opening conversation still starts at latest message");
            var count = main.ViewModel.Messages.Count;
            var deferred=main.ViewModel.LoadEarlierMessagesAsync(()=>false); WaitForUiTask(deferred);
            Check(!deferred.Result && main.ViewModel.Messages.Count==count && main.ViewModel.CanLoadEarlierMessages,"deferred insertion leaves page available for retry");
            var away = main.LoadEarlierHistoryPreservingViewportAsync(); WaitForUiTask(away);
            Check(!away.Result && main.ViewModel.Messages.Count==count, "being away from upper threshold does not query more history");

            var restoring = typeof(MainWindow).GetField("_restoringHistoryViewport", BindingFlags.Instance | BindingFlags.NonPublic)!;
            restoring.SetValue(main,true); scroll.ScrollToVerticalOffset(180); Layout(root,1000,740); restoring.SetValue(main,false);
            var selected = main.ViewModel.Messages[2].Id;
            main.SetMessageSelectionMode(true, selected); Layout(root,1000,740);
            var before = main.CaptureMessageViewportAnchor()!;
            Capture(root, output, "history-before-prepend", 1000, 740);
            MainWindow.MessageViewportAnchor? firstFrame=null;
            var frameScheduled=false;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler captureFirstFrame=(_,_) =>
            {
                if (!main.ViewModel.IsPrependingMessages || frameScheduled) return;
                frameScheduled=true;
                main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(()=>firstFrame=main.CaptureMessageViewportAnchor()));
            };
            main.ViewModel.Messages.CollectionChanged+=captureFirstFrame;
            var load = main.LoadEarlierHistoryPreservingViewportAsync();
            var duplicate = main.LoadEarlierHistoryPreservingViewportAsync(); WaitForUiTask(duplicate); WaitForUiTask(load); Layout(root,1000,740);
            Check(load.Result && !duplicate.Result && main.ViewModel.Messages.Count==400, "near-top request inserts exactly one earlier page and coalesces overlap");
            main.ViewModel.Messages.CollectionChanged-=captureFirstFrame;
            Check(firstFrame?.Id==before.Id && Math.Abs(firstFrame.Top-before.Top)<=1,"first render after prepend retains anchor without a transient jump");
            var after = main.CaptureMessageViewportAnchor()!;
            Check(after.Id==before.Id && Math.Abs(after.Top-before.Top)<=1, $"same visible message pixel anchor survives variable rows and date groups: before={before.Top}, after={after.Top}");
            Check(main.SelectedBatchMessages.Count==1 && main.SelectedBatchMessages[0].Id==selected,"history prepend preserves multiselect IDs");
            Check(((FrameworkElement)main.FindName("NewMessagesButton")).Visibility==Visibility.Collapsed,"historical outgoing messages do not show new-message indicator");
            Check(scroll.VerticalOffset<scroll.ScrollableHeight-2,"historical outgoing records do not jump to bottom");
            Check(main.ViewModel.Messages.Select(item=>item.Id).Distinct().Count()==400,"repeated paging never duplicates message IDs");
            Capture(root, output, "history-after-prepend", 1000, 740);

            main.SetMessageSelectionMode(false); Layout(root,1000,740);
            var provider = (IScrollProvider)new ScrollViewerAutomationPeer(scroll).GetPattern(PatternInterface.Scroll);
            provider.SetScrollPercent(System.Windows.Automation.ScrollPatternIdentifiers.NoScroll, 120/scroll.ScrollableHeight*100);
            Layout(root,1000,740); WaitForUiTask(main.EarlierHistoryLoad); Layout(root,1000,740);
            Check(main.ViewModel.Messages.Count>400,"actual scroll provider approaching top automatically loads next page");
            var total=main.ViewModel.Messages.Count;
            restoring.SetValue(main,true);scroll.ScrollToTop();Layout(root,1000,740);restoring.SetValue(main,false);
            var atBeginning = main.LoadEarlierHistoryPreservingViewportAsync();WaitForUiTask(atBeginning);Layout(root,1000,740);
            Check(!atBeginning.Result && !main.ViewModel.CanLoadEarlierMessages,"end of history is cached without repeated no-more toast");
            var noMore = main.LoadEarlierHistoryPreservingViewportAsync();WaitForUiTask(noMore);
            Check(!noMore.Result && main.ViewModel.Messages.Count==total,"repeated attempts at known beginning do not grow or reload history");
            var stable=main.CaptureMessageViewportAnchor()!;
            main.ViewModel.Messages[0]=main.ViewModel.Messages[0] with { Status=MessageStatus.Read };
            Layout(root,1000,740);
            Check(main.CaptureMessageViewportAnchor()?.Id==stable.Id,"message status updates do not scroll an older viewport to end");

            main.ViewModel.Messages.Add(new ChatItem(Guid.NewGuid(),"实时接收消息",false,DateTimeOffset.UtcNow.AddYears(10),MessageStatus.Received));
            Layout(root,1000,740);
            Check(main.CaptureMessageViewportAnchor()?.Id==stable.Id && ((FrameworkElement)main.FindName("NewMessagesButton")).Visibility==Visibility.Visible,
                "live incoming message preserves older viewport and offers new-message action");
            main.ViewModel.Messages.Add(new ChatItem(Guid.NewGuid(),"实时发送消息",true,DateTimeOffset.UtcNow.AddYears(10),MessageStatus.Delivered));
            Layout(root,1000,740);
            Check(Math.Abs(scroll.VerticalOffset-scroll.ScrollableHeight)<1,"new outgoing message still follows conversation bottom");

            // A-B-A may return to the same peer, but must not accept an earlier session's pending page.
            WaitForUiTask(main.ViewModel.SelectConversationAsync(peer));Layout(root,1000,740);
            Check(main.ViewModel.CanLoadEarlierMessages,"reopening a conversation resets exhausted page boundary");
            var oldPage=main.ViewModel.LoadEarlierMessagesAsync();
            var other=main.ViewModel.SelectConversationAsync(main.ViewModel.Conversations.First(item=>item.PeerId!=peer).PeerId);
            var back=main.ViewModel.SelectConversationAsync(peer);
            WaitForUiTask(Task.WhenAll(oldPage,other,back));Layout(root,1000,740);
            Check(!oldPage.Result && main.ViewModel.Messages.Count==200,"old A-B-A page is discarded by selection version");
            Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,"automatic history verification never creates native window");

            async Task Seed()
            {
                var local=Path.Combine(path,"history-file.txt");File.WriteAllText(local,"history fixture");
                var image=Path.Combine(path,"history-image.png");
                File.Copy(Path.GetFullPath("design/brand/final/bluelink-final-logo.png"),image,true);
                var start=new DateTimeOffset(2030,1,1,0,0,0,TimeSpan.Zero);
                for(var i=0;i<420;i++)
                {
                    var id=Guid.NewGuid().ToString("N");var timestamp=start.AddDays(i/80).AddMinutes(i%80).ToUnixTimeMilliseconds();
                    var attachment=i%13==0;var picture=i%26==0;
                    var content=attachment?"":$"历史消息 {i:D3}\n"+string.Join("\n",Enumerable.Repeat("不同高度的段落，用于核对补入历史后当前位置不偏移。",i%5+1));
                    await database.UpsertMessageAsync(new(id,"peer:"+peer,peer,i%2==0?StoredMessageDirection.Outgoing:StoredMessageDirection.Incoming,
                        attachment?(picture?StoredMessageType.Image:StoredMessageType.File):StoredMessageType.Text,content,"Received",timestamp,timestamp));
                    if(attachment) await database.UpsertAttachmentAsync(new(Guid.NewGuid().ToString("N"),id,null,picture?"history-image.png":"history-file.txt",
                        picture?"image/png":"text/plain",17,null,picture?image:local,null,"Completed"));
                }
            }
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }
}
