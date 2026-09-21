using BlueLink.Domain;
internal static class TransferPresentationVerification
{
    internal static void Run()
    {
        var count=0;
        foreach(var status in Enum.GetValues<TransferStatus>())
        {
            var item=new TransferItem { Id=Guid.NewGuid(),Name="QA",TotalBytes=100,CompletedBytes=100,Outgoing=true,Status=status };
            var attachment=new ChatAttachment(Guid.NewGuid(),item.Id,"QA","application/pdf",100,State:status.ToString(),CompletedBytes:100);
            var unknown=status is TransferStatus.Offered or TransferStatus.Queued or TransferStatus.Verifying or TransferStatus.Committing;
            Check(item.IsProgressIndeterminate==unknown && attachment.IsProgressIndeterminate==unknown,"phase progress");
            Check(!unknown || (!item.DeviceStatusText.Contains('%') && item.ProgressLabel=="—" && attachment.ProgressLabel==""),"unknown progress has no false percentage");
            if(status==TransferStatus.Committing) Check(!attachment.CanOpen,"commit is not completed");
        }
        var peer=new ConversationSummary("qa","QA",PeerPlatform.Android,DeviceAvailability.Connected,Guid.NewGuid(),"",0,DateTimeOffset.Now);
        var active=new TransferItem {Id=Guid.NewGuid(),Name="QA",TotalBytes=100,CompletedBytes=25,Outgoing=true,Status=TransferStatus.Transferring};
        peer.ActiveTransfer=active;peer.ActiveTransferCount=3;
        Check(peer.ActiveTransferSummary.Contains("25%") && peer.ActiveTransferSummary.Contains("2"),"current plus remaining count");
        var notified=false;peer.PropertyChanged+=(_,e)=>notified|=e.PropertyName==nameof(peer.ActiveTransferSummary);
        active.CompletedBytes=50;
        Check(notified && peer.ActiveTransferSummary.Contains("50%"),"summary tracks bytes");
        active.Status=TransferStatus.Committing;
        Check(!peer.ActiveTransferSummary.Contains('%'),"summary switches to saving without bytes");
        Console.WriteLine($"Transfer presentation: {count} checks passed.");
        void Check(bool valid,string name) {if(!valid)throw new Exception(name);count++;}
    }
}
