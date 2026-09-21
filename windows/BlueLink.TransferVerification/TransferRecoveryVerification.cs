using System.IO;
using BlueLink.Domain;
using BlueLink.Protocol;
using BlueLink.Storage;

internal static class TransferRecoveryVerification
{
    public static async Task SeedAsync(string directory)
    {
        if (File.Exists(Path.Combine(directory, "bluelink.db"))) throw new IOException("Use a fresh QA directory.");
        var database = new BlueLinkDatabase(directory, directory);
        await database.InitializeAsync(new BlueLink.Security.IdentityStore(directory));
        await database.SaveSettingsAsync(BlueLinkSettings.Defaults(Path.Combine(directory,"Received")) with {
            SaveChatHistory=false, SaveTransferHistory=false, ScanOnStartup=false, AutoConnectTrustedDevices=false, Theme="light" });
        var now=DateTimeOffset.UtcNow;
        const string peer="00000000000000000000000000000001";
        await database.UpsertPeerAsync(new(peer,"QA Recovery Android","Android",StoredTrustState.Unknown,null,
            now.ToUnixTimeMilliseconds(),now.ToUnixTimeMilliseconds(),now.ToUnixTimeMilliseconds()));
        await database.UpsertConversationAsync(new("peer:"+peer,peer,now.ToUnixTimeMilliseconds(),0));
        var source=Path.Combine(directory,"QA-recovery.bin"); await File.WriteAllBytesAsync(source,new byte[1024]);
        var journal=new TransferRecoveryStore(Path.Combine(directory,"Recovery"));
        foreach(var outgoing in new[]{true,false}) journal.RecordTransfer(Guid.NewGuid(),now,"Bluetooth",new() {
            Id=Guid.NewGuid(),Name=outgoing?"QA-待恢复的发送文件.bin":"QA-等待发送方恢复.pdf",TotalBytes=1024,CompletedBytes=1024,
            Outgoing=outgoing,Status=TransferStatus.Committing,PeerId=peer,PeerName="QA Recovery Android",LocalPath=outgoing?source:null,
            AttemptId=outgoing?Guid.NewGuid():null,AttemptSequence=outgoing?1:0,SourceSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[1024])) });
    }

    public static void Run(string directory)
    {
        var root = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); checks++; }
        var owner = Guid.NewGuid(); var started = DateTimeOffset.UtcNow;
        TransferItem Task(bool outgoing = true) => new() { Id = Guid.NewGuid(), Name = "报告.bin", TotalBytes = 200, Outgoing = outgoing,
            PeerId = "peer-a", PeerName = "Device A", Status = TransferStatus.Queued, AttemptId = outgoing ? Guid.NewGuid() : null,
            AttemptSequence = outgoing ? 50 : 0, SourceSha256 = new string('A',64), LocalPath = Path.Combine(root,"source.bin"),
            MessageId = Guid.NewGuid(), AttachmentId = Guid.NewGuid() };
        var item = Task(); var store = new TransferRecoveryStore(root);
        Check(store.RecordTransfer(owner,started,"Bluetooth",item),"queue committed");
        Check(!store.Dismiss(item.Id),"active task cannot be dismissed");
        item.Status=TransferStatus.Committing; item.CompletedBytes=200;
        Check(store.RecordTransfer(owner,started,"Bluetooth",item),"commit phase");
        var restart = new TransferRecoveryStore(root);
        var recovered = restart.MergeHistory([]).Single();
        Check(recovered.RecoveryPending && !recovered.IsActive && !recovered.CanOpen,"restart is explicit recovery, not completion");
        Check(recovered.AttemptId is null && recovered.AttemptSequence==0,"new process projection has no live attempt");
        Check(recovered.SourceSha256==item.SourceSha256 && recovered.MessageId==item.MessageId && recovered.AttachmentId==item.AttachmentId,"identity preserved");
        Check(restart.MergeHistory([item]).Count==1,"legacy history merges once");
        Check(restart.MergeHistory([],"other").Count==0,"peer isolation");
        item.AttemptSequence=1;item.AttemptId=Guid.NewGuid();item.Status=TransferStatus.Queued;
        Check(restart.RecordTransfer(Guid.NewGuid(),started.AddSeconds(1),"Bluetooth",item),"new process can explicitly retry with sequence one");
        var nextOwner=Guid.NewGuid(); var newer=started.AddSeconds(2);
        item.AttemptSequence=2; item.AttemptId=Guid.NewGuid();
        Check(restart.RecordTransfer(nextOwner,newer,"Bluetooth",item),"next attempt");
        var stale=item.Snapshot();stale.AttemptSequence=1;stale.AttemptId=Guid.NewGuid();stale.Status=TransferStatus.Completed;
        Check(!restart.RecordTransfer(owner,started,"Bluetooth",stale),"stale owner cannot complete new attempt");
        item.Status=TransferStatus.Completed;
        Check(restart.RecordTransfer(nextOwner,newer,"Bluetooth",item),"completed durable");
        var late=item.Snapshot();late.Status=TransferStatus.Transferring;
        Check(!restart.RecordTransfer(nextOwner,newer,"Bluetooth",late),"terminal cannot roll back");
        Check(new TransferRecoveryStore(root).MergeHistory([]).Count==0,"completed task not resurrected when history disabled");
        Check(new TransferRecoveryStore(root).MergeHistory([late]).Single().Status==TransferStatus.Completed,"durable completion overrides stale history");
        var incoming=Task(false);store=new TransferRecoveryStore(root);store.RecordTransfer(owner,started,"USB",incoming);
        var receiveRestart=new TransferRecoveryStore(root);var receive=receiveRestart.MergeHistory([]).Single();
        Check(receive.RecoveryPending && !receive.CanRetry && receive.StatusText.Length>0,"receiver waits for sender");
        var wrongPeer=incoming.Snapshot();wrongPeer.PeerId="peer-b";
        Check(!receiveRestart.RecordTransfer(nextOwner,newer,"USB",wrongPeer),"do not replay to changed identity");
        Check(receiveRestart.Dismiss(incoming.Id),"explicit removal allowed after restart");
        Check(new TransferRecoveryStore(root).MergeHistory([incoming]).Count==0,"tombstone prevents SQL rollback resurrection");
        incoming.Status=TransferStatus.Offered;
        Check(receiveRestart.RecordTransfer(owner,started,"USB",incoming),"fresh incoming offer after deletion remains visible");
        incoming.Status=TransferStatus.Failed;
        Check(receiveRestart.RecordTransfer(owner,started,"USB",incoming),"receiver failure recorded");
        incoming.Status=TransferStatus.Offered;
        Check(receiveRestart.RecordTransfer(owner,started,"USB",incoming),"same-session legacy sender retry is not suppressed");
        var receiveAttempt=Task(false); receiveAttempt.AttemptId=Guid.NewGuid();receiveAttempt.AttemptSequence=1;
        Check(store.RecordTransfer(owner,started,"USB",receiveAttempt),"receiver attempt registered");
        receiveAttempt.Status=TransferStatus.Canceled;store.RecordTransfer(owner,started,"USB",receiveAttempt);
        var freshReceive=receiveAttempt.Snapshot();freshReceive.Status=TransferStatus.Queued;freshReceive.AttemptId=Guid.NewGuid();freshReceive.AttemptSequence=2;
        Check(store.RecordTransfer(owner,started,"USB",freshReceive),"new receiver attempt registered");
        receiveAttempt.Status=TransferStatus.Completed;
        Check(!store.RecordTransfer(owner,started,"USB",receiveAttempt),"late receiver completion cannot overwrite new attempt");
        var faultRoot=Path.Combine(root,"fault");var failed=Task();
        var faultStore=new TransferRecoveryStore(faultRoot,(_,_)=>throw new IOException("Injected full disk"));
        try { faultStore.RecordTransfer(owner,started,"Bluetooth",failed);throw new Exception("write must fail"); } catch(IOException) { checks++; }
        Check(faultStore.MergeHistory([]).Count==0,"failed write doesn't advance memory");
        var healthy=new TransferRecoveryStore(faultRoot);healthy.RecordTransfer(owner,started,"Bluetooth",failed);
        var failTerminal=new TransferRecoveryStore(faultRoot,(_,_)=>throw new IOException("Injected full disk"));
        var active=failTerminal.MergeHistory([]).Single();
        try { failTerminal.Dismiss(active.Id);throw new Exception("dismiss must fail"); } catch(IOException) { checks++; }
        Check(new TransferRecoveryStore(faultRoot).MergeHistory([]).Single().RecoveryPending,"failed dismissal retains task");
        File.WriteAllText(Path.Combine(faultRoot,"ignored.json.new"),"broken interrupted replacement");
        Check(new TransferRecoveryStore(faultRoot).MergeHistory([]).Count==1,"uncommitted temporary write ignored");
        var attachment=new ChatAttachment(recovered.AttachmentId!.Value,recovered.Id,recovered.Name,recovered.MimeType,recovered.TotalBytes);
        var projected=attachment.WithTransfer(recovered);
        Check(projected.RecoveryPending && !projected.HasFailure && projected.ProgressLabel=="—","message attachment uses neutral recovery state");
        Check(projected.StateText==recovered.StatusText,"file and message recovery labels agree");
        Check(!projected.WithTransfer(item).RecoveryPending,"live retry removes old message recovery flag");
        Check(ReferenceEquals(projected,projected.WithTransfer(incoming)),"unrelated task cannot change attachment");
        var migrationRoot=Path.Combine(root,"association");
        var migrationItem=Task();
        var beforeMigration=new TransferRecoveryStore(migrationRoot);
        beforeMigration.RecordTransfer(owner,started,"USB",migrationItem);
        var aliases=new Dictionary<string,string> { ["peer-a"]="peer-b", ["peer-b"]="peer-c" };
        try { beforeMigration.Associate(aliases); throw new Exception("active migration must fail"); }
        catch (InvalidOperationException) { checks++; }
        Check(beforeMigration.MergeHistory([]).Single().PeerId=="peer-a","active migration leaves original identity");
        var migration=new TransferRecoveryStore(migrationRoot);
        migration.Associate(aliases); migration.Associate(aliases);
        var migrated=new TransferRecoveryStore(migrationRoot).MergeHistory([]).Single();
        Check(migrated.PeerId=="peer-c" && migrated.RecoveryPending,"confirmed association remains manual recovery");
        Check(migrated.Id==migrationItem.Id && migrated.SourceSha256==migrationItem.SourceSha256 && migrated.LocalPath==migrationItem.LocalPath,
            "migration preserves task and immutable source identity");
        Check(!migration.RecordTransfer(owner,started,"USB",migrationItem),"old peer callback rejected after association");
        try { migration.Associate(new Dictionary<string,string> { ["peer-c"]="peer-d",["peer-d"]="peer-c" }); throw new Exception("cycle must fail"); }
        catch(InvalidDataException) { checks++; }
        Check(new TransferRecoveryStore(migrationRoot).MergeHistory([]).Single().PeerId=="peer-c","cycle never mutates records");
        var failureMigration=new TransferRecoveryStore(migrationRoot,(_,_)=>throw new IOException("Injected migration failure"));
        try { failureMigration.Associate(new Dictionary<string,string> { ["peer-c"]="peer-d" }); throw new Exception("write must fail"); }
        catch(IOException) { checks++; }
        Check(failureMigration.MergeHistory([]).Single().PeerId=="peer-c","failed migration does not advance memory");
        var receiverRoot=Path.Combine(root,"receiver-ownership");
        var bytes=new byte[128]; var hash=System.Security.Cryptography.SHA256.HashData(bytes);
        var offer=new BlueLink.Transfer.FileOffer(Guid.NewGuid(),"owned.bin",bytes.Length,128,hash);
        var held=new BlueLink.Transfer.TransferReceiver(receiverRoot,offer);
        try
        {
            held.AcceptAsync(new(offer.Id,0,hash,bytes),CancellationToken.None).GetAwaiter().GetResult();
            try { new BlueLink.Transfer.TransferReceiver(receiverRoot,offer with { Hash=new byte[32] }); throw new Exception("receiver must remain owned"); }
            catch(IOException) { checks++; }
            Check(held.ContiguousBytes==128,"competing receiver preserves original checkpoint");
        }
        finally { held.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var resumedReceiver=new BlueLink.Transfer.TransferReceiver(receiverRoot,offer);
        try { Check(resumedReceiver.ContiguousBytes==128,"receiver can resume after prior owner closes"); }
        finally { resumedReceiver.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var streams=new BlueLink.Session.TransferStreamFence(); var streamTask=Task(false);
        using (streams.Receive(streamTask.Id,19,true,false,true,true) ?? throw new Exception("first incoming stream"))
        {
            var first=streams.Stamp(streamTask);
            Check(first.AttemptId is not null && first.AttemptSequence>0,"incoming task receives attempt identity");
            Check(streams.StreamFor(streamTask.Id,2,true)==19,"reply echoes authenticated attempt stream");
            var terminal=streamTask.Snapshot(); terminal.Status=TransferStatus.Failed; streams.Stamp(terminal);
        }
        Check(streams.Receive(streamTask.Id,19,true,false,true,true) is null,"terminal offer cannot reopen same stream");
        Check(streams.Receive(streamTask.Id,21,true,true,true,true) is null,"new stream cannot overtake a draining worker");
        using (streams.Receive(streamTask.Id,21,true,false,true,true) ?? throw new Exception("retry stream"))
            Check(streams.StreamFor(streamTask.Id,2,true)==21,"new attempt gets new reply stream");
        Check(streams.Receive(streamTask.Id,19,false,false,true,true) is null,"late control/completion from previous attempt ignored");
        Check(streams.Receive(Guid.NewGuid(),21,true,false,true,true) is null,"stream cannot be reassigned to another task");
        Check(streams.Receive(Guid.NewGuid(),20,true,false,true,true) is null,"peer cannot use local stream parity");
        var legacy=new BlueLink.Session.TransferStreamFence();
        using(legacy.StartOutgoing(streamTask.Id,false,true)) Check(legacy.StreamFor(streamTask.Id,2,false)==2,"legacy framing unchanged");
        try { using var again=legacy.StartOutgoing(streamTask.Id,false,true); throw new Exception("legacy same-session retry must fail"); }
        catch(IOException) { checks++; }
        Console.WriteLine($"Transfer recovery verification passed: {checks} checks");
    }
}
