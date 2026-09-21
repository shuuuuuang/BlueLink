using System.IO;
using BlueLink.Storage;
using BlueLink.Domain;
internal static class PeerPreferencesVerification
{
    public static void Run(string directory)
    {
        var root=Path.Combine(Path.GetFullPath(directory),Guid.NewGuid().ToString("N"));
        var store=new PeerPreferences(root); int checks=0;
        void Check(bool valid,string reason) { if(!valid) throw new Exception(reason); checks++; }
        store.Update("PEER",note:"工作电脑 🌍",pinned:true);
        var reopened=new PeerPreferences(root);
        Check(reopened.Get("peer")==new PeerPreference("工作电脑 🌍",true),"local metadata survives restart and ID case normalization");
        Check(reopened.Get("different")==new PeerPreference(),"different verified identity never inherits notes or pins");
        reopened.Update("peer",note:""); Check(reopened.Get("peer")==new PeerPreference("",true),"clearing note preserves pin");
        var peer=new ConversationSummary("peer","Original",PeerPlatform.Android,DeviceAvailability.Offline,null,"",0,DateTimeOffset.UtcNow) { LocalNote="Alias" };
        Check(peer.DisplayName=="Alias" && peer.PeerName=="Original","display note does not replace protocol name");
        try { reopened.Update("peer",note:new string('x',65)); throw new Exception("Accepted oversized note"); } catch(ArgumentException) { checks++; }
        File.WriteAllText(Path.Combine(root,"peer-preferences.json"),"broken");
        Check(reopened.Get("peer")==new PeerPreference(),"bad optional metadata does not block device list");
        try { reopened.Update("peer",pinned:false); throw new Exception("Overwrote corrupt metadata"); } catch(System.Text.Json.JsonException) { checks++; }
        Check(File.ReadAllText(Path.Combine(root,"peer-preferences.json"))=="broken","failed update preserves original metadata");
        Console.WriteLine($"Peer preferences verification passed: {checks} checks");
    }
}
