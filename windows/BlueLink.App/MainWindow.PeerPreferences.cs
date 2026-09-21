using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using BlueLink.Domain;
namespace BlueLink;
public partial class MainWindow
{
    private async void ConversationPinMenu_Click(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement { DataContext: ConversationSummary peer })
            await WithToastAsync(() => _model.SavePeerPreferenceAsync(peer,pinned:!peer.IsPinned),"设备偏好已保存","保存设备偏好失败");
    }
    private async void ConversationNoteMenu_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement { DataContext: ConversationSummary peer }) return;
        var panel=new StackPanel();
        panel.Children.Add(new TextBlock { Text=Localization.Strings.Get("仅在本机显示；留空恢复设备原名。"),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12) });
        var input=new Wpf.Ui.Controls.TextBox { Text=peer.LocalNote,MaxLength=64,MinHeight=40 };
        AutomationProperties.SetName(input,Localization.Strings.Get("设备备注"));
        input.Loaded+=(_,_)=> {input.Focus();input.SelectAll();}; panel.Children.Add(input);
        if(BlueLinkDialog.ConfirmContent(this,"设备备注",panel,"保存","取消",BlueLinkDialogTone.Information))
            await WithToastAsync(() => _model.SavePeerPreferenceAsync(peer,note:input.Text),"设备偏好已保存","保存设备偏好失败");
    }
}
