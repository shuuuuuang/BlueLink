using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Localization;

namespace BlueLink;

public partial class MainWindow
{
    private readonly HashSet<Guid> _selectedFileIds = [];
    private bool _syncingFileSelection;
    private bool _fileBatchRunning;
    private Guid? _fileSelectionAnchor;
    private MessageSelectionGesture? _fileSelectionGesture;
    private string? _fileSelectionPeer;

    internal void SetFileSelectionMode(bool enabled, Guid? initial = null)
    {
        if(_fileBatchRunning) return;
        _fileSelectionAnchor = initial;
        _fileSelectionPeer = enabled ? _model.ActivePeerId : null;
        _syncingFileSelection=true;
        try
        {
            _model.FileSelectionMode=enabled; _selectedFileIds.Clear(); TransferList.UnselectAll();
            TransferList.SelectionMode=enabled ? SelectionMode.Multiple : SelectionMode.Single;
            if(enabled && initial is { } id && TransferList.Items.OfType<TransferItem>().FirstOrDefault(item=>item.Id==id) is { } item)
            { _selectedFileIds.Add(id); TransferList.SelectedItems.Add(item); }
        }
        finally { _syncingFileSelection=false; }
        UpdateFileSelection();
    }
    internal void ToggleFileBatch(Guid id, bool extend)
    {
        if (_fileBatchRunning || !_model.FileSelectionMode) return;
        if (!BatchSelection.Toggle(_selectedFileIds, TransferList.Items.OfType<TransferItem>().Select(item => item.Id).ToArray(),
            _fileSelectionAnchor, id, extend, FileBatchPolicy.MaximumSelection))
        { ShowToast(Strings.Get("最多选择100项")); return; }
        if (_selectedFileIds.Contains(id) && (!extend || _fileSelectionAnchor is null)) _fileSelectionAnchor = id;
        _syncingFileSelection = true;
        try
        {
            TransferList.UnselectAll();
            foreach (var item in TransferList.Items.OfType<TransferItem>().Where(item => _selectedFileIds.Contains(item.Id)))
                TransferList.SelectedItems.Add(item);
        }
        finally { _syncingFileSelection = false; }
        UpdateFileSelection();
    }
    private void FileBatch_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_model.FileSelectionMode) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is not ListBoxItem { DataContext: TransferItem item }) continue;
            e.Handled = true; ToggleFileBatch(item.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); return;
        }
    }
    private void FileSelectMode_Click(object sender,RoutedEventArgs e) => SetFileSelectionMode(!_model.FileSelectionMode);
    private void FileSelectMenu_Click(object sender,RoutedEventArgs e)
    { if(sender is FrameworkElement { DataContext: TransferItem item }) SetFileSelectionMode(true,item.Id); }
    private void FileSelection_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_syncingFileSelection || !_model.FileSelectionMode) return;
        foreach(TransferItem item in e.RemovedItems) _selectedFileIds.Remove(item.Id);
        foreach(TransferItem item in e.AddedItems)
        {
            if(_selectedFileIds.Count < FileBatchPolicy.MaximumSelection) _selectedFileIds.Add(item.Id);
            else
            {
                _syncingFileSelection=true; TransferList.SelectedItems.Remove(item); _syncingFileSelection=false;
                ShowToast(Strings.Get("最多选择100项"));
            }
        }
        UpdateFileSelection();
    }
    private void UpdateFileSelection()
    {
        if(FileBatchPanel is null) return;
        _fileSelectionAnchor = BatchSelection.ResolveAnchor(_selectedFileIds, TransferList.Items.OfType<TransferItem>().Select(item => item.Id).ToArray(), _fileSelectionAnchor);
        _fileSelectionGesture?.SetEnabled(_model.FileSelectionMode && !_fileBatchRunning);
        FileBatchPanel.Visibility=_model.FileSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        FileFooterDefault.Visibility = _model.FileSelectionMode ? Visibility.Collapsed : Visibility.Visible;
        FileToolbar.Visibility = _model.FileSelectionMode ? Visibility.Collapsed : Visibility.Visible;
        FullTransferContent.Visibility = _model.FileSelectionMode ? Visibility.Collapsed : Visibility.Visible;
        FileSelectionHeader.Visibility = _model.FileSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        FileSelectionHeader.IsEnabled = !_fileBatchRunning;
        FileColumnsHeader.IsEnabled = !_model.FileSelectionMode;
        FileSelectModeButton.IsEnabled = !_fileBatchRunning;
        FileSelectionCount.Text=Strings.Format($"已选择 {_selectedFileIds.Count} 项");
        FileBatchActions.IsEnabled = TransferList.IsEnabled = !_fileBatchRunning;
        foreach (var button in FileBatchActions.Children.OfType<Wpf.Ui.Controls.Button>())
            button.IsEnabled = !_fileBatchRunning && Enum.TryParse<FileBatchAction>(button.Tag as string, out var action) &&
                _selectedFileIds.Any(id => _model.CheckFileBatch(id, action).Eligible);
    }
    internal static string FileBatchSummary(int total,int eligible) =>
        Strings.Format($"{total} 个文件")+Environment.NewLine+Strings.Format($"可处理 {eligible} 项，跳过 {total-eligible} 项");

    internal static ConfirmationDocument FileBatchDocument(int count, int eligible, FileBatchAction action)
    {
        if (action == FileBatchAction.DeleteRecords)
            return ConfirmationDocument.DeleteRecord(Strings.Format($"确定删除所选的 {count} 个文件记录吗？") +
                Environment.NewLine + Strings.Get("正在进行的文件传输会跳过。"));
        var title = Strings.Get(action switch { FileBatchAction.Retry => "批量重试", FileBatchAction.Cancel => "批量取消", _ => "复制所选文件" });
        var question = action switch
        {
            FileBatchAction.Retry => Strings.Format($"确定重试所选的 {count} 个文件吗？"),
            FileBatchAction.Cancel => Strings.Format($"确定取消所选的 {count} 个文件传输吗？"),
            _ => Strings.Format($"确定复制所选的 {count} 个文件吗？")
        };
        if (eligible < count) question += Environment.NewLine + Strings.Format($"可处理 {eligible} 项，跳过 {count - eligible} 项");
        return new ConfirmationDocument(title, question, Strings.Get("每项执行前会重新检查，传输结果请查看对应任务。"),
            Strings.Get(action switch { FileBatchAction.Retry => "重试", FileBatchAction.Cancel => "取消传输", _ => "复制" }),
            Destructive: action == FileBatchAction.Cancel);
    }

    private async void FileBatch_Click(object sender,RoutedEventArgs e)
    {
        if(_fileBatchRunning || _selectedFileIds.Count==0 || sender is not FrameworkElement { Tag:string tag } ||
            !Enum.TryParse<FileBatchAction>(tag,out var action)) return;
        var ids=_selectedFileIds.ToArray();
        var preview=ids.Select(id=>_model.CheckFileBatch(id,action)).ToArray();
        var eligible=preview.Count(item=>item.Eligible);
        var title=Strings.Get(action switch { FileBatchAction.Retry=>"批量重试",FileBatchAction.Cancel=>"批量取消",FileBatchAction.DeleteRecords=>"删除所选记录",_=>"复制所选文件" });
        var detail=FileBatchSummary(preview.Length,eligible);
        if(eligible==0) { BlueLinkDialog.Show(this,title,detail); return; }
        if(!ConfirmationWindow.Show(this,FileBatchDocument(preview.Length,eligible,action))) return;
        _fileBatchRunning=true; UpdateFileSelection();
        try
        {
            IReadOnlyList<FileBatchEntry> results;
            if(action==FileBatchAction.Copy)
            {
                var checks=ids.Select(id=>_model.CheckFileBatch(id,action)).ToArray();
                var paths=checks.Where(item=>item.Eligible).Select(check=>_model.AllTransfers.First(item=>item.Id==check.Id).LocalPath!).ToArray();
                if(paths.Length>0) Clipboard.SetDataObject(FileDragDropService.CreateCopyDataObject(paths),true);
                results=checks.Select(item=>new FileBatchEntry(item.Id,item.Name,item.Eligible ? FileBatchOutcome.Submitted : FileBatchOutcome.Skipped,item.Reason)).ToArray();
            }
            else results=await _model.RunFileBatchAsync(ids,action);
            foreach(var item in results.Where(item=>item.Outcome==FileBatchOutcome.Submitted)) _selectedFileIds.Remove(item.Id);
            var submitted=results.Count(item=>item.Outcome==FileBatchOutcome.Submitted);
            var skipped=results.Count(item=>item.Outcome==FileBatchOutcome.Skipped);
            var failed=results.Count(item=>item.Outcome==FileBatchOutcome.Failed);
            var summary=Strings.Format($"已处理 {submitted} 项，跳过 {skipped} 项，失败 {failed} 项");
            if(skipped+failed>0) BlueLinkDialog.Show(this,title,summary);
            else ShowToast(summary,ToastLevel.Success);
        }
        catch(Exception error) { ShowToast(Strings.Get("批量操作失败，请重试"),ToastLevel.Error); Session.SessionLog.Write("FileBatch","Batch failed",error); }
        finally
        {
            _fileBatchRunning=false;
            if (_fileSelectionPeer != _model.ActivePeerId || !_model.ShowFiles || _model.IsSettingsOpen) SetFileSelectionMode(false);
            RenderFileResults(); UpdateFileSelection();
        }
    }
}
