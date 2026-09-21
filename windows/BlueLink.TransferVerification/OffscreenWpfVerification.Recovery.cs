using System.IO;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Storage;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyRecovery(string directory, string output)
    {
        WaitForUiTask(TransferRecoveryVerification.SeedAsync(directory));
        var main=new MainWindow(initializeRuntime:false,dataRoot:directory);
        try
        {
            WaitForUiTask(main.ViewModel.InitializeLocalStateAsync());
            Check(!main.ViewModel.Settings.SaveTransferHistory && !main.ViewModel.Settings.SaveChatHistory,"recovery independent of both history switches");
            Check(main.ViewModel.AllTransfers.Count==2 && main.ViewModel.AllTransfers.All(x=>x.RecoveryPending && !x.IsActive),"real model loads pending recovery after restart");
            WaitForUiTask(main.ViewModel.SelectConversationAsync(main.ViewModel.Conversations.Single().PeerId));
            Check(main.ViewModel.Transfers.Count==2,"conversation includes recovery without SQL transfer rows");
            Check(main.ViewModel.AllTransfers.All(x=>x.ProgressLabel=="—" && !x.IsFailed),"recovery does not show a stale percentage or failure styling");
            main.OpenFileWorkspace(true);
            var root=DetachForRendering(main);
            foreach(var language in new[]{"zh-CN","en-US","zh-TW"})
            foreach(var theme in new[]{"light","dark"})
            {
                WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with {Language=language,Theme=theme}));
                Capture(root,output,$"recovery-{language}-{theme}",1100,740);
            }
            WaitForUiTask(main.ViewModel.ClearTransferHistoryAsync());
            Check(main.ViewModel.AllTransfers.Count==2,"clear history preserves operational recovery");
            WaitForUiTask(main.ViewModel.DeleteTransferAsync(main.ViewModel.AllTransfers.First(x=>x.Outgoing)));
            Check(main.ViewModel.AllTransfers.Count==1,"explicit task deletion dismisses recovery");
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
        var reopened=new MainWindow(initializeRuntime:false,dataRoot:directory);
        try
        {
            WaitForUiTask(reopened.ViewModel.InitializeLocalStateAsync());
            Check(reopened.ViewModel.AllTransfers.Count==1 && !reopened.ViewModel.AllTransfers[0].Outgoing,"dismissal survives model restart");
            Check(!reopened.ViewModel.AllTransfers[0].CanRetry,"receiver has no local resume/send action");
        }
        finally { WaitForUiTask(reopened.DisposeAsync().AsTask()); }
    }
}
