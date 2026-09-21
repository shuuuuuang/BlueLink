using System.IO;
using BlueLink.Storage;

internal static class TemporaryStorageVerification
{
    public static void Run(string directory)
    {
        var root = Path.Combine(Path.GetFullPath(directory), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); checks++; }
        var outgoing = Path.Combine(root,"cache","outgoing"); Directory.CreateDirectory(outgoing);
        File.WriteAllBytes(Path.Combine(root,"db"),new byte[3]);
        File.WriteAllBytes(Path.Combine(outgoing,"legacy.snapshot"),new byte[5]);
        File.WriteAllBytes(Path.Combine(outgoing,"legacy.blm"),new byte[7]);
        var locations = new[] { new StorageLocation(root,StorageCategory.Other),new StorageLocation(outgoing,StorageCategory.Snapshots) };
        var inventory = StorageInventory.Scan(locations.Concat(locations));
        Check(inventory.TotalBytes == 15 && !inventory.Partial,"nested and repeated roots count every file once");
        Check(inventory.Bytes[StorageCategory.Other] == 3 && inventory.Bytes[StorageCategory.Snapshots] == 5 && inventory.Bytes[StorageCategory.UsbStaging] == 7,"semantic storage categories");
        Check(StorageInventory.Scan([new(Path.Combine(root,"missing"),StorageCategory.Received)]).Partial,"unavailable root is not known zero");
        var file = Path.Combine(outgoing,"owned.snapshot"); var id = Guid.NewGuid();
        OwnedTemporaryFiles.Register(file,id); File.WriteAllBytes(file,new byte[17]);
        Check(OwnedTemporaryFiles.Collect(outgoing,_=>false,false).Files == 0,"active writer blocks cleanup");
        OwnedTemporaryFiles.Release(file);
        Check(OwnedTemporaryFiles.Collect(outgoing,task=>task == id,false).Files == 0 && File.Exists(file),"pending recovery retains owned snapshot");
        var preview = OwnedTemporaryFiles.Collect(outgoing,_=>false,true);
        Check(preview.Bytes == 17 && preview.Files == 1 && File.Exists(file),"preview does not mutate files");
        int rechecks = 0;
        Check(OwnedTemporaryFiles.Collect(outgoing,_=>++rechecks > 1,false).Files == 0,"cleanup rechecks durable references");
        Check(OwnedTemporaryFiles.Collect(outgoing,_=>false,false) == preview && !File.Exists(file),"actual reclaimed bytes match preview");
        Check(File.Exists(Path.Combine(outgoing,"legacy.snapshot")) && File.Exists(Path.Combine(outgoing,"legacy.blm")),"unknown old files are untouched");
        OwnedTemporaryFiles.Register(file,id); File.WriteAllText(file,"keep"); OwnedTemporaryFiles.Release(file);
        File.WriteAllText(file+".owner.json","{broken");
        Check(OwnedTemporaryFiles.Collect(outgoing,_=>false,false).Errors == 1 && File.Exists(file),"corrupt ownership preserves file");
        try { OwnedTemporaryFiles.Register(file,id); throw new Exception("Claimed existing file"); } catch (IOException) { checks++; }
        var draftRoot = Path.Combine(root,"draft-case");
        var drafts = new BlueLink.Domain.ComposerDrafts(draftRoot);
        string MakeImage(Guid imageId) { var path = drafts.NewImagePath(imageId); OwnedTemporaryFiles.Register(path,imageId); File.WriteAllBytes(path,new byte[23]); OwnedTemporaryFiles.Release(path); return path; }
        var draftId = Guid.NewGuid(); var draftPath = MakeImage(draftId);
        var historyId = Guid.NewGuid(); var historyPath = MakeImage(historyId);
        var orphanId = Guid.NewGuid(); var orphanPath = MakeImage(orphanId);
        drafts.Edit("peer",[new(draftId,File:new(draftId,draftPath,"draft.png",23,true))]);
        var startup = new BlueLink.Domain.ComposerDrafts(draftRoot);
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { historyPath };
        Check(startup.CollectStartupOrphans(refs,TimeSpan.Zero).Files == 1 && !File.Exists(orphanPath),"startup collects only proven orphan");
        Check(File.Exists(draftPath) && File.Exists(historyPath),"draft and history/recovery references preserve attachments");
        startup.Edit("peer",[]);
        Check(startup.CollectStartupOrphans(new HashSet<string>(),TimeSpan.Zero).Files == 0 && File.Exists(draftPath),"native undo retains files for remainder of process");
        Console.WriteLine($"Temporary storage verification passed: {checks} checks");
    }
}
