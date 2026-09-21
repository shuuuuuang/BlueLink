using BlueLink.Protocol;
internal static class DeviceNameVerification
{
    public static void Run()
    {
        var golden = Convert.FromHexString("010100000000007f424c444e0011e9aa8ce694b6e6898be69cba20f09f93b1");
        if (!DeviceNameGreeting.Encode("验收手机 📱").SequenceEqual(golden) || DeviceNameGreeting.Decode(golden) != "验收手机 📱") throw new Exception("Cross-platform name vector mismatch");
        if (ProtocolGreeting.Decode(golden) != ProtocolGreeting.Current) throw new Exception("Legacy greeting decoder compatibility broken");
        foreach (var payload in new byte[][] { [1,0,0,0], ProtocolGreeting.Current.Encode(), golden[..^1], [1,1,0,0,0,0,0,31,66,76,68,78,0,1,255] })
            if (DeviceNameGreeting.Decode(payload) != "") throw new Exception("Invalid or absent name must use existing peer fallback");
        foreach (var invalid in new[] { " ", new string('x', 385), "a\nb" })
            if (!DeviceNameGreeting.Encode(invalid).SequenceEqual(ProtocolGreeting.Current.Encode())) throw new Exception("Invalid outgoing display metadata");
        Console.WriteLine("Device name greeting: Unicode vector, old decoder, old peer, malformed and invalid metadata passed.");
    }
}
