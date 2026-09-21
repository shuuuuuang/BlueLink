using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using BlueLink;
using BlueLink.Domain;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifySelectionGestures(string directory, string output)
    {
        foreach (var height in new[] { 36d, 360d, 2000d })
        {
            var message = new Rect(100, 40, 240, height);
            Check(MessageSelectionGesture.Hits(new Rect(0, 40, 20, height), message, 36), "vertical row selection ignores horizontal bubble position");
            Check(MessageSelectionGesture.Hits(new Rect(0, 40, 0, 18), message, 36), "pure vertical drag reaches half-line threshold");
            Check(!MessageSelectionGesture.Hits(new Rect(101, 40, 20, 17.999), message, 36), "less than half a single-line bubble is not selected");
            Check(MessageSelectionGesture.Hits(new Rect(101, 40, 20, 18), message, 36), "exactly half single-line height selects even very tall messages");
            Check(!MessageSelectionGesture.Hits(new Rect(101, 0, 20, 40), message, 36), "vertical boundary contact is not a hit");
        }
        var order = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        Check(BatchSelection.ResolveAnchor(new HashSet<Guid>{order[3]},order,order[3])==order[3],"valid selected anchor is retained");
        Check(BatchSelection.ResolveAnchor(new HashSet<Guid>{order[0],order[3]},order,order[2])==order[3],"deselected anchor resolves to nearest selected row");
        Check(BatchSelection.ResolveAnchor(new HashSet<Guid>(),order,order[2]) is null,"empty selection has no range anchor");
        Check(BatchSelection.ResolveAnchor(new HashSet<Guid>{order[1]},order,Guid.NewGuid())==order[1],"removed anchor resolves in current displayed order");
        var viewport = new Rect(0, 0, 300, 200);
        var rows = new[] { new MessageSelectionGesture.Row(order[0], new Rect(20, -1, 200, 40)),
            new MessageSelectionGesture.Row(order[1], new Rect(20, 39, 200, 40)),
            new MessageSelectionGesture.Row(order[2], new Rect(20, 79, 200, 40)),
            new MessageSelectionGesture.Row(order[3], new Rect(20, 159, 200, 40)),
            new MessageSelectionGesture.Row(order[4], new Rect(20, 199, 200, 40)) };
        Check(MessageSelectionGesture.FindRangeTarget(order, new HashSet<Guid> { order[2] }, order[2], rows, viewport) == order[3], "range target excludes clipped bottom row");
        Check(MessageSelectionGesture.FindRangeTarget(order, new HashSet<Guid> { order[3] }, order[3], rows, viewport) == order[1], "range target excludes clipped top row");
        Check(MessageSelectionGesture.FindRangeTarget(order, order.ToHashSet(), order[2], rows, viewport) is null, "no redundant range action when range already selected");
        Check(MessageSelectionGesture.FindRangeTarget(order, new HashSet<Guid>(), Guid.NewGuid(), rows, viewport) is null, "missing anchor offers no invalid range");

        var main = new MainWindow(false, Path.Combine(directory, "selection-gestures"));
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, Path.Combine(directory, "selection-gestures"), "message-history"));
            WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with { Theme = "light", Language = "zh-CN" }));
            WaitForUiTask(main.ViewModel.SelectConversationAsync(main.ViewModel.Conversations.First().PeerId));
            main.ViewModel.Messages.Clear();
            var messages = Enumerable.Range(0, 18).Select(i => new ChatItem(Guid.NewGuid(), i == 1 ? "较长的消息\n第二行\n第三行\n第四行\n第五行" : $"框选验证 {i + 1}", i % 2 == 1,
                DateTimeOffset.UtcNow.AddMinutes(i), MessageStatus.Received)).ToArray();
            foreach (var message in messages) main.ViewModel.Messages.Add(message);
            var root = DetachForRendering(main); Layout(root, 1000, 740);
            var list = (ListBox)main.FindName("MessageList");
            var scroll = Descendants<ScrollViewer>(list).First(); scroll.ScrollToTop(); Layout(root, 1000, 740);
            main.SetMessageSelectionMode(true, messages[0].Id); Layout(root, 1000, 740);
            var gesture = (MessageSelectionGesture)typeof(MainWindow).GetField("_messageSelectionGesture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            foreach (var upwards in new[] { true, false })
            {
                var visible=gesture.Rows().Where(row=>gesture.Viewport.Contains(row.Bounds)).ToArray();
                var anchor=upwards ? visible[^1].Id : visible[0].Id;
                main.SetMessageSelectionMode(true,anchor);Layout(root,1000,740);
                var endpoint=gesture.RangeTarget!.Value;
                var placement=gesture.RangeButton.VerticalAlignment;
                Check(placement==(upwards ? VerticalAlignment.Top : VerticalAlignment.Bottom),"range initially points to expected visible edge");
                for(var repeat=0;repeat<2;repeat++)
                {
                    ((IInvokeProvider)new ButtonAutomationPeer(gesture.RangeButton).GetPattern(PatternInterface.Invoke)).Invoke();Layout(root,1000,740);
                    Check(main.SelectedBatchMessages.Any(item=>item.Id==endpoint),"range selects endpoint before deselection regression");
                    var endpointRow=gesture.Rows().Single(row=>row.Id==endpoint);
                    var click=new Point(gesture.Viewport.Left+2,endpointRow.Bounds.Top+endpointRow.Bounds.Height/2);
                    gesture.Begin(click,endpoint);gesture.End(click);Layout(root,1000,740);
                    Check(main.SelectedBatchMessages.Any(item=>item.Id==anchor) && main.SelectedBatchMessages.All(item=>item.Id!=endpoint),"deselecting endpoint preserves original selected anchor");
                    Check(gesture.RangeTarget==endpoint && gesture.RangeButton.VerticalAlignment==placement && gesture.RangeButton.Visibility==Visibility.Visible,"range button returns to same side after endpoint deselection");
                }
                Capture(root,output,upwards ? "range-reselect-top" : "range-reselect-bottom",1000,740);
            }
            main.SetMessageSelectionMode(true,messages[0].Id);Layout(root,1000,740);
            Check(gesture.RangeTarget is not null && gesture.RangeButton.Visibility == Visibility.Visible, "real chat offers select-to-here for fully visible rows");
            Capture(root, output, "selection-range-chat", 1000, 740);
            var target = gesture.RangeTarget!.Value;
            ((IInvokeProvider)new ButtonAutomationPeer(gesture.RangeButton).GetPattern(PatternInterface.Invoke)).Invoke(); Layout(root, 1000, 740);
            var targetIndex = Array.FindIndex(messages, item => item.Id == target);
            Check(main.SelectedBatchMessages.Count == targetIndex + 1, "range button UIA selects inclusive loaded range");
            var row = gesture.Rows().First(item => item.Id == messages[0].Id);
            var start = new Point(gesture.Viewport.Left + 2, row.Bounds.Top + 1);
            var end = new Point(start.X, row.Bounds.Bottom - 1);
            var before = main.SelectedBatchMessages.Select(item => item.Id).ToHashSet();
            gesture.Begin(start, null); gesture.Move(end); Layout(root, 1000, 740);
            Check(gesture.IsDragging && main.SelectedBatchMessages.Select(item => item.Id).ToHashSet().SetEquals(before), "drag preview leaves selection unchanged until release");
            gesture.Move(end); Check(main.SelectedBatchMessages.Select(item => item.Id).ToHashSet().SetEquals(before), "repeated moves do not publish selection");
            Capture(root, output, "selection-rectangle-chat", 1000, 740);
            gesture.Move(new Point(start.X + 1, start.Y + 1));
            Check(main.SelectedBatchMessages.Select(item => item.Id).ToHashSet().SetEquals(before), "shrinking box restores original selection outside hit area");
            gesture.Move(end); gesture.End(end);
            Check(!gesture.IsDragging && !main.SelectedBatchMessages.Any(item => item.Id == row.Id), "release commits rectangle deselection");
            gesture.Begin(end, null); gesture.Move(start); gesture.End(start);
            Check(main.SelectedBatchMessages.Any(item => item.Id == row.Id), "reverse drag can reselect the same message");
            gesture.Begin(start, null); gesture.Move(end); gesture.Cancel(true); gesture.Refresh();
            Check(main.SelectedBatchMessages.Select(item => item.Id).ToHashSet().SetEquals(before), "capture cancellation restores snapshot");
            gesture.Begin(start, row.Id); gesture.End(start);
            Check(!main.SelectedBatchMessages.Any(item => item.Id == row.Id), "short click remains individual toggle");

            main.SetMessageSelectionMode(true, messages[0].Id); Layout(root, 1000, 740);
            gesture.Begin(start, null); gesture.Move(new Point(gesture.Viewport.Right - 4, gesture.Viewport.Bottom + 10));
            var oldOffset = scroll.VerticalOffset;
            typeof(MessageSelectionGesture).GetMethod("AutoScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gesture, [null, EventArgs.Empty]);
            Layout(root, 1000, 740);
            Check(scroll.VerticalOffset > oldOffset, "dragging at viewport edge scrolls actual list");
            Check(main.SelectedBatchMessages.Any(item => item.Id == messages[0].Id), "edge scrolling still leaves selection unchanged before release");
            gesture.End(new Point(gesture.Viewport.Right - 4, gesture.Viewport.Bottom));
            Check(!main.SelectedBatchMessages.Any(item => item.Id == messages[0].Id), "release commits scrolled-out row hits");
            main.SetMessageSelectionMode(false); Layout(root, 1000, 740);
            Check(!gesture.IsDragging && gesture.RangeButton.Visibility == Visibility.Collapsed && !main.ViewModel.MessageSelectionMode, "leaving selection removes gesture overlay");
            Check(new System.Windows.Interop.WindowInteropHelper(main).Handle == IntPtr.Zero, "gesture tests create no foreground window or mouse input");
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }
}
