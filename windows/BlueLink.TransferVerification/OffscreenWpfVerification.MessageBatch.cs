using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Storage;
using BlueLink.Files;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyMessageBatch(string directory, string output)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "QA-preserved.pdf"); File.WriteAllText(file, "keep actual bytes");
        var attachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "QA-preserved.pdf", "application/pdf", 17, file, "Completed");
        ChatItem Item(int n, string text, params ChatAttachment[] files) => new(Guid.NewGuid(), text, false,
            DateTimeOffset.UtcNow.AddMinutes(n), MessageStatus.Received, files.Length > 0 ? ChatItemKind.File : ChatItemKind.Text, files);
        foreach (var status in Enum.GetValues<TransferStatus>())
        {
            var item = Item(1, "", attachment with { State = status.ToString() });
            Check(MessageBatch.CanDelete(item) == !item.Attachments![0].IsTransferActive, "active transfer retained: " + status);
            Check(!MessageBatch.CanDelete(item with { Attachments = [attachment with { State = status.ToString(), RecoveryPending = true }] }), "recovery retained: " + status);
        }
        var first = Item(0, "first\nline  "); var second = Item(1, "", attachment); var third = Item(2, "last 🌍");
        var ordered = MessageBatch.Ordered([third,second,first,second], [third.Id,first.Id,second.Id,Guid.NewGuid()]);
        Check(MessageBatch.CopyText(ordered) == "first\nline  " + Environment.NewLine + "[QA-preserved.pdf]" + Environment.NewLine + "last 🌍", "ordered copy preserves Unicode, whitespace and file names");
        var data = FileDragDropService.CreateCopyDataObject(new[] { file });
        Check(data.GetFileDropList().Count == 1 && data.GetFileDropList()[0] == file, "native file clipboard data without replacing user clipboard");
        Check(MessageClipboard.TryCreateDataObject(ordered, [], out var mixed), "mixed clipboard created");
        Check(mixed!.GetDataPresent(DataFormats.UnicodeText, false) && mixed.GetDataPresent(DataFormats.FileDrop, false), "mixed copy contains native text and files together");
        Check(mixed.GetText() == MessageBatch.CopyText(ordered), "mixed copy preserves chronological text fallback");
        Check(mixed.GetFileDropList().Count == 1 && mixed.GetFileDropList()[0] == file, "mixed copy references original file, not filename text");
        Check(mixed.GetData("Preferred DropEffect") is MemoryStream effect && BitConverter.ToInt32(effect.ToArray()) == (int)DragDropEffects.Copy, "mixed clipboard copies rather than cuts");
        Check(MessageClipboard.TryCreateDataObject([first, third], [], out var textOnly) &&
            !textOnly!.GetDataPresent(DataFormats.FileDrop) && textOnly.GetText() == MessageBatch.CopyText([first, third]), "text-only copy does not advertise files");
        Check(MessageClipboard.TryCreateDataObject([second], [], out var fileOnly) &&
            fileOnly!.GetFileDropList().Count == 1 && fileOnly.GetText() == "[QA-preserved.pdf]", "attachment-only general copy supplies file and text fallback");
        Check(MessageClipboard.TryCreateDataObject([Item(0, "中文 🌍", attachment, attachment with { AttachmentId = Guid.NewGuid() })], [], out var duplicate) &&
            duplicate!.GetFileDropList().Count == 1 && duplicate.GetText().StartsWith("中文 🌍"), "same-message text with repeated file deduplicates native paths");
        var anotherFile = Path.Combine(directory, "后一个.txt"); File.WriteAllText(anotherFile, "next");
        Check(MessageClipboard.TryCreateDataObject([first, second, Item(2, "after", attachment with { LocalPath = anotherFile })], [], out var multiple) &&
            multiple!.GetFileDropList().Cast<string>().SequenceEqual(new[] { file, anotherFile }), "multiple native files preserve message order");
        foreach (var unavailable in new[] { attachment with { LocalPath = null }, attachment with { LocalPath = file + ".missing" },
            attachment with { State = "Transferring" }, attachment with { State = "Failed" }, attachment with { RecoveryPending = true } })
        {
            Check(!MessageClipboard.TryCreateDataObject([first, Item(1, "", unavailable)], [], out var rejected) && rejected is null,
                "unavailable mixed copy cannot silently degrade to filename: " + unavailable.State + unavailable.LocalPath + unavailable.RecoveryPending);
        }
        var live = new TransferItem { Id = attachment.TransferId, Name = attachment.FileName, TotalBytes = 17, Outgoing = false, Status = TransferStatus.Transferring, LocalPath = file };
        Check(!MessageClipboard.TryCreateDataObject(ordered, [live], out _), "current transfer state overrides completed search snapshot");
        live.Status = TransferStatus.Completed;
        Check(MessageClipboard.TryCreateDataObject([first, Item(1, "", attachment with { State = "Transferring", LocalPath = null })], [live], out var refreshed) &&
            refreshed!.GetFileDropList()[0] == file, "current completion and path override stale search snapshot");
        Check(!MessageClipboard.TryCreateDataObject([], [], out var empty) && empty is null, "empty selection cannot replace clipboard");
        Check(File.ReadAllText(file) == "keep actual bytes", "clipboard construction does not modify original file");
        foreach (var theme in new[] {"light","dark"}) foreach (var language in new[] {"zh-CN","en-US","zh-TW"})
        {
            var path = Path.Combine(directory, theme+language); var main = new MainWindow(false,path);
            try
            {
                WaitForUiTask(DesktopAcceptance.InitializeAsync(main,path,"message-history"));
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with {Theme=theme,Language=language}));
                var peer = main.ViewModel.Conversations.First().PeerId;
                WaitForUiTask(main.ViewModel.SelectConversationAsync(peer));
                main.ViewModel.Messages.Clear();
                var active = Item(3,"",attachment with { AttachmentId=Guid.NewGuid(), State="Committing" });
                var recovery = Item(4,"",attachment with { AttachmentId=Guid.NewGuid(), RecoveryPending=true,State="Failed" });
                var sending = Item(5,"sending") with {Outgoing=true,Status=MessageStatus.Sending};
                var items = new[] {first,second,third,active,recovery,sending};
                var db = new BlueLinkDatabase(path,path);
                foreach(var message in items)
                {
                    WaitForUiTask(db.UpsertMessageAsync(new(message.Id.ToString("N"),"peer:"+peer,peer,
                        message.Outgoing ? StoredMessageDirection.Outgoing : StoredMessageDirection.Incoming,
                        message.Attachments?.Count>0 ? StoredMessageType.File : StoredMessageType.Text,
                        message.Text,message.Status.ToString(),message.CreatedAt.ToUnixTimeMilliseconds(),message.CreatedAt.ToUnixTimeMilliseconds())));
                    main.ViewModel.Messages.Add(message);
                }
                var root = DetachForRendering(main); Layout(root,1000,740);
                var list = (ListBox)main.FindName("MessageList");
                var menuHost = Descendants<Grid>(root).First(grid => grid.Name == "MessageContextHost" && grid.DataContext is ChatItem message && message.Id == first.Id);
                var textMenu = menuHost.ContextMenu; textMenu.PlacementTarget = menuHost;
                Check(textMenu.Items[1] is MenuItem { Icon: Wpf.Ui.Controls.SymbolIcon { Symbol: Wpf.Ui.Controls.SymbolRegular.TaskListLtr24 } }, "text message multiselect has aligned checklist icon");
                Check(textMenu.Items[2] is Separator && textMenu.Items[3] is MenuItem { CommandParameter: "danger" }, "text message menu separates delete from copy and selection");
                textMenu.Width = 320; textMenu.Height = 210;
                Capture(textMenu,output,$"message-text-menu-{theme}-{language}",320,210);
                main.SetMessageSelectionMode(true,second.Id); Layout(root,1000,740);
                Check(main.SelectedBatchMessages.Single().Id==second.Id,"menu initial message selected");
                Check(((FrameworkElement)main.FindName("MessageComposer")).Visibility==Visibility.Collapsed,"selection replaces composer");
                Check(main.FindName("MessageBatchFiles") is null && ((System.Windows.Controls.Primitives.ButtonBase)main.FindName("MessageBatchCopy")).IsEnabled,"pure files use unified copy without duplicate action");
                Check(Descendants<CheckBox>(root).Any(x=>x.Name=="MessageBatchCheckBox" && x.Visibility==Visibility.Visible),"real message checkboxes visible");
                var firstCheck = Descendants<CheckBox>(root).First(x=>x.Name=="MessageBatchCheckBox" && x.DataContext is ChatItem message && message.Id==first.Id);
                var toggle = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(firstCheck).GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
                var checkboxBefore = firstCheck.TranslatePoint(new Point(), list);
                var checkboxSize = firstCheck.RenderSize;
                toggle.Toggle(); Layout(root,1000,740);
                Check(firstCheck.TranslatePoint(new Point(), list) == checkboxBefore && firstCheck.RenderSize == checkboxSize,
                    "UIA selection does not move or resize the message checkbox");
                toggle.Toggle(); Layout(root,1000,740);
                Check(firstCheck.TranslatePoint(new Point(), list) == checkboxBefore, "deselecting preserves checkbox position");
                toggle.Toggle(); Layout(root,1000,740);
                Check(firstCheck.IsChecked==true,"real UI Automation checkbox toggle updates selection");
                var chatMark=Descendants<Wpf.Ui.Controls.SymbolIcon>((Grid)firstCheck.Parent).Single(icon=>icon.Name=="MessageBatchCheckMark");
                Check(chatMark.Visibility==Visibility.Visible && chatMark.Clip is null,"chat checkbox uses complete static mark after UIA toggle");
                Check(main.SelectedBatchMessages.Count==2 && ((System.Windows.Controls.Primitives.ButtonBase)main.FindName("MessageBatchCopy")).IsEnabled,"mixed selection uses same copy action");
                var selectedRow=Descendants<DockPanel>(list).First(row=>row.Name=="MessageSelectionRow" && row.DataContext is ChatItem item && item.Id==first.Id);
                Check(selectedRow.Background==root.FindResource("BatchSelectedRowBrush") && selectedRow.TranslatePoint(new Point(),list).X<=1,"chat selected background spans to leading edge");
                Check(firstCheck.TranslatePoint(new Point(),list).X <= 16,"message checkbox has compact leading inset");
                var batchPanel=(FrameworkElement)main.FindName("MessageBatchPanel");
                var workspace=(FrameworkElement)main.FindName("WorkspaceHeader");
                Check(Math.Abs(batchPanel.ActualWidth-workspace.ActualWidth)<1,"message action surface spans workspace width");
                Check(((FrameworkElement)main.FindName("MessageBatchControls")).HorizontalAlignment==HorizontalAlignment.Right,"message actions align right");
                Check(Descendants<Wpf.Ui.Controls.Button>((FrameworkElement)main.FindName("MessageSelectionHeader")).Count()==1,"message header exposes only cancel, no select-all");
                main.CopyMessageSelection(_ => throw new IOException("clipboard unavailable"));
                Check(main.ViewModel.MessageSelectionMode && main.SelectedBatchMessages.Count==2,"failed publication retains selection");
                System.Windows.DataObject? copied=null;
                main.CopyMessageSelection(value => copied=value); Layout(root,1000,740);
                Check(copied?.GetFileDropList().Count==1 && copied.GetText().Contains(first.Text),"chat unified copy publishes text and file together");
                Check(!main.ViewModel.MessageSelectionMode && main.SelectedBatchMessages.Count==0,"successful chat copy exits and clears selection");
                main.SetMessageSelectionMode(true,first.Id); main.ToggleMessageBatch(second.Id,false);
                main.ViewModel.Messages[1] = second with {Status=MessageStatus.Read}; Layout(root,1000,740);
                Check(main.SelectedBatchMessages.Count==2 && main.SelectedBatchMessages.Any(x=>x.Id==second.Id),"stable selected IDs survive status replacement");
                var incoming=Item(6,"new message"); main.ViewModel.Messages.Add(incoming); Layout(root,1000,740);
                Check(main.SelectedBatchMessages.Count==2,"new messages are not selected implicitly");
                foreach(var item in items.Where(item=>!main.SelectedBatchMessages.Any(selected=>selected.Id==item.Id))) main.ToggleMessageBatch(item.Id,false);
                Layout(root,1000,740); Check(main.SelectedBatchMessages.Count==6,"individual selections retain explicitly chosen messages only");
                Capture(root,output,$"messages-selected-{theme}-{language}",1000,740);
                var deletion=main.DeleteMessageSelectionAsync(); WaitForUiTask(deletion);
                Check(deletion.Result.Count==6 && deletion.Result.Count(x=>x.Deleted)==3,"partial delete retains active, recovery and sending messages");
                Check(main.ViewModel.Messages.Count==4 && File.ReadAllText(file)=="keep actual bytes","only message records removed; original file retained");
                var persisted=db.LoadMessagesAsync("peer:"+peer); WaitForUiTask(persisted);
                Check(!persisted.Result.Any(x=>new[]{first.Id,second.Id,third.Id}.Contains(Guid.Parse(x.MessageId))),"successful deletions persist in database");
                Check(persisted.Result.Any(x=>Guid.Parse(x.MessageId)==active.Id),"protected message remains persisted");
                Layout(root,1000,740); Check(!main.ViewModel.MessageSelectionMode && main.SelectedBatchMessages.Count==0,"completed deletion exits selection, including partial success");
                main.SetMessageSelectionMode(false); Layout(root,1000,740);
                Check(list.SelectedItems.Count==0 && list.SelectionMode==SelectionMode.Single,"leaving clears selection");
                Check(((FrameworkElement)main.FindName("MessageComposer")).Visibility==Visibility.Visible,"composer restored");
                main.SetMessageSelectionMode(true,active.Id);
                var rejectedDelete=main.DeleteMessageSelectionAsync(); WaitForUiTask(rejectedDelete);
                Check(rejectedDelete.Result.All(item=>!item.Deleted) && main.ViewModel.MessageSelectionMode,"no deletable messages retains selection");
                var publishedUnavailable=false; main.CopyMessageSelection(_=>publishedUnavailable=true);
                Check(!publishedUnavailable && main.ViewModel.MessageSelectionMode,"incomplete file neither publishes nor exits selection");
                WaitForUiTask(main.ViewModel.SelectConversationAsync(main.ViewModel.Conversations.First(x=>x.PeerId!=peer).PeerId));
                Check(!main.ViewModel.MessageSelectionMode && main.SelectedBatchMessages.Count==0,"switching peer clears selection");
                Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,"no foreground HWND created");
            }
            finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
            foreach (var confirm in new[] {true,false})
            {
                var dialog = confirm ? new ConfirmationWindow(MainWindow.MessageBatchDeleteDocument(5)) :
                    BlueLinkDialog.CreateWindow("批量操作结果", BlueLink.Localization.Strings.Format($"已删除 {4} 条，跳过或失败 {1} 条") + "\n" +
                        BlueLink.Localization.Strings.Get("正在发送或待恢复的消息不能删除"), BlueLinkDialogTone.Information);
                try
                {
                    var content=DetachForRendering(dialog); Capture(content,output,$"message-batch-prompt-{theme}-{language}-{confirm}",480,420);
                    Check(((FrameworkElement)dialog.FindName("ConfirmationSecondaryButton")).Visibility==Visibility.Collapsed,"no unused secondary action");
                    Check(((FrameworkElement)dialog.FindName("ConfirmationPrimaryButton")).Visibility==Visibility.Visible,"confirmation/result action visible");
                    Check(new System.Windows.Interop.WindowInteropHelper(dialog).Handle==IntPtr.Zero,"batch dialogs rendered without native window");
                }
                finally { dialog.Close(); }
            }
        }
    }
}
