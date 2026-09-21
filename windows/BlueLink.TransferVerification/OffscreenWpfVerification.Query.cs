using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Storage;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyQueryUi(string directory, string output)
    {
        VerifyHistoryWindow(directory);
        var metrics = new List<object>();
        foreach(var count in new[] { 1000,10000,100000 })
        {
            var now = DateTimeOffset.Now;
            var source = new ObservableCollection<ChatItem>(Enumerable.Range(1,count).Select(index => new ChatItem(
                Guid.Parse($"00000000-0000-0000-0000-{index:x12}"), $"资料中文 review {index}", false, now, MessageStatus.Received)));
            var search = new MessageSearchWindow("QA", source, false);
            try
            {
                var root = DetachForRendering(search);
                search.ApplyFilter("review", HistoryKind.Text);
                Layout(root,900,640);
                var list = (ListBox)search.FindName("Results");
                Check(list.Items.Count == 100, "large search only materializes first page");
                var more = (System.Windows.Controls.Button)search.FindName("MoreResultsButton");
                more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(list.Items.Count == 200, "load more keeps stable first page");
                var input = (TextBox)search.FindName("QueryInput");
                input.Text = "no old match";
                var old = search.RefreshResultsAsync();
                input.Text = "review";
                var current = search.RefreshResultsAsync();
                WaitForUiTask(Task.WhenAll(old,current)); Layout(root,900,640);
                Check(list.Items.Count == 100 && ((TextBlock)search.FindName("ResultCount")).Text.Contains(count.ToString()), "superseded query never replaces current result and total");
                var times = new List<double>();
                for(var i=0;i<20;i++)
                {
                    var clock = Stopwatch.StartNew();
                    WaitForUiTask(search.RefreshResultsAsync()); Layout(root,900,640);
                    times.Add(clock.Elapsed.TotalMilliseconds);
                }
                times.Sort(); metrics.Add(new { count, p95Ms=times[18], debounceMs=125, layoutIncluded=true, nativeHwnd=0 });
                File.WriteAllText(Path.Combine(output,"render-benchmark.json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions { WriteIndented=true }));
                Console.WriteLine($"WPF production query count={count} p95={times[18]:F1} ms, includes debounce and layout");
                if(count == 10000) Check(times[18] <= 300, "10k production search plus actual WPF layout p95 <= 300ms");
                Capture(root,output,$"query-page-{count}",900,640);
            }
            finally { search.Close(); }
        }
        foreach(var theme in new[] { "light","dark" }) foreach(var language in new[] { "zh-CN","en-US","zh-TW" })
        {
            var data = Path.Combine(directory,theme+language);
            var main = new MainWindow(false,data);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(main,data,"message-history"));
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme=theme,Language=language }));
                main.ViewModel.AllTransfers.Clear();
                for(var index=1;index<=250;index++) main.ViewModel.AllTransfers.Add(new TransferItem {
                    Id=Guid.Parse($"00000000-0000-0000-0000-{index:x12}"),Name=$"QA-{index}.pdf",TotalBytes=index,
                    CreatedAt=DateTimeOffset.Now.AddSeconds(-index),Outgoing=false,Status=TransferStatus.Completed });
                main.OpenFileWorkspace(true);
                var root = DetachForRendering(main); Layout(root,1000,700);
                var list = (ListBox)main.FindName("TransferList");
                Check(list.Items.Count == 100, "file first page bounded");
                ((System.Windows.Controls.Button)main.FindName("FileMoreButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(list.Items.Count == 200 && list.Items.Cast<TransferItem>().Select(item=>item.Id).Distinct().Count()==200,"file next page has no duplicates");
                SetFileColumn(main,"Kind","Images");
                Check(list.Items.Count == 0,"image filter excludes PDFs"); SetFileColumn(main,"Kind","Files");
                var sort=((FileTableHeader)main.FindName("FileSizeHeader")).SortButton;
                sort.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                sort.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(list.Items.Cast<TransferItem>().Select(item=>item.TotalBytes).SequenceEqual(Enumerable.Range(1,100).Select(value=>(long)value)),"actual sort controls update file order");
                SetFileDates(main,DateTime.Today.AddDays(1),null);
                Check(list.Items.Count==0,"file dates combine with type and sort");ResetFileColumn(main,"Date");
                Capture(root,output,$"query-files-{theme}-{language}",1000,700);
                var footer=(FrameworkElement)main.FindName("FileFooter");
                var more=(FrameworkElement)main.FindName("FileMoreButton");
                var path=(FrameworkElement)main.FindName("ChangeReceiveDirectoryButton");
                Check(path.TranslatePoint(new Point(path.ActualWidth,0),footer).X <= more.TranslatePoint(new Point(),footer).X,"more button does not cover receive folder action");
                Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,"query fixture leaves desktop untouched");
            }
            finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
        }
        File.WriteAllText(Path.Combine(output,"render-benchmark.json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions { WriteIndented=true }));
    }
    private void VerifyHistoryWindow(string directory)
    {
        var data = Path.Combine(directory,"history-window");
        var main = new MainWindow(false,data);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main,data,"message-history"));
            var model=main.ViewModel;
            var peer=model.Conversations.First(item=>item.IsOffline).PeerId;
            var other=model.Conversations.First(item=>item.PeerId!=peer).PeerId;
            var database=new BlueLinkDatabase(data);
            var conversation=$"peer:{peer.ToLowerInvariant()}";
            var otherConversation=$"peer:{other.ToLowerInvariant()}";
            WaitForUiTask(database.ClearMessagesAsync());
            var now=DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string Id(int number) => $"00000000000000000000{number:x12}";
            for(var index=1;index<=500;index++) WaitForUiTask(database.UpsertMessageAsync(new(Id(index),conversation,peer,StoredMessageDirection.Incoming,
                StoredMessageType.Text,$"needle {index}","Received",now+index,index)));
            WaitForUiTask(database.UpsertMessageAsync(new(Id(900),otherConversation,other,StoredMessageDirection.Incoming,StoredMessageType.Text,"other peer","Received",now,1)));
            WaitForUiTask(database.UpsertAttachmentAsync(new(Id(901),Id(1),null,"original.txt","text/plain",4,null,null,null,"Completed")));
            WaitForUiTask(database.UpsertAttachmentAsync(new(Id(902),Id(500),null,"latest.txt","text/plain",4,null,null,null,"Completed")));
            var latest=database.LoadHistoryPageAsync(conversation);WaitForUiTask(latest);
            Check(latest.Result.Messages.Count==200 && latest.Result.Messages[0].MessageId==Id(301),"database first page is latest 200");
            Check(latest.Result.Attachments.Count==1 && latest.Result.Attachments[0].MessageId==Id(500),"only page attachments are read");
            var older=database.LoadHistoryPageAsync(conversation,Id(301));WaitForUiTask(older);
            Check(older.Result.Messages.Count==200 && older.Result.Messages[0].MessageId==Id(101) && older.Result.Messages[^1].MessageId==Id(300),"cursor page has no boundary repeat");
            WaitForUiTask(database.UpsertMessageAsync(new(Id(501),conversation,peer,StoredMessageDirection.Incoming,StoredMessageType.Text,"new","Received",now+501,501)));
            var afterInsert=database.LoadHistoryPageAsync(conversation,Id(301));WaitForUiTask(afterInsert);
            Check(afterInsert.Result.Messages.Select(item=>item.MessageId).SequenceEqual(older.Result.Messages.Select(item=>item.MessageId)),"new arrival does not shift cursor page");
            var wrong=database.LoadHistoryPageAsync(conversation,Id(900),around:true);WaitForUiTask(wrong);
            Check(wrong.Result.Messages.Count==0,"context cannot cross peer boundary");
            var context=database.LoadHistoryPageAsync(conversation,Id(1),around:true);WaitForUiTask(context);
            Check(context.Result.Messages.Count<=200 && context.Result.Messages.Any(item=>item.MessageId==Id(1)) && context.Result.Attachments.Any(item=>item.MessageId==Id(1)),"old ID loads nearby context and its attachment only");
            WaitForUiTask(model.SelectConversationAsync(peer));
            Check(model.Messages.Count==200 && model.Messages.All(item=>item.Id!=Guid.ParseExact(Id(1),"N")),"production conversation starts bounded");
            var transient=new ChatItem(Guid.NewGuid(),"unpersisted",true,DateTimeOffset.Now.AddDays(1),MessageStatus.Sent);
            model.Messages.Add(transient);
            var locate=model.LoadMessageContextAsync(peer,Guid.ParseExact(Id(1),"N"));WaitForUiTask(locate);
            Check(locate.Result && model.Messages.Any(item=>item.Id==Guid.ParseExact(Id(1),"N")) && model.Messages.Contains(transient),"production locate retains unpersisted live message");
            var root=DetachForRendering(main);Layout(root,1000,700);
            var search=new MessageSearchWindow("QA",model.Messages,false,model);
            try
            {
                var content=DetachForRendering(search);
                ((TextBox)search.FindName("QueryInput")).Text="needle";
                WaitForUiTask(search.RefreshResultsAsync());Layout(content,900,640);
                Check(((TextBlock)search.FindName("ResultCount")).Text.Contains("500") && ((ListBox)search.FindName("Results")).Items.Count==100,"search includes unloaded history with bounded result page");
            }
            finally { search.Close(); }
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }

}
