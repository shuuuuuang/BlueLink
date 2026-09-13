using System.IO;
using System.Threading.Channels;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Transport;

internal sealed partial class UsbVerification
{
    private async Task VerifyUnrestrictedPeerCount(string root)
    {
        await using var host = new SessionSupervisor(new IdentityStore(Path.Combine(root, "unrestricted-host")), r => r.Confirm());
        var remotes = Enumerable.Range(1, 12).Select(i => new SessionSupervisor(
            new IdentityStore(Path.Combine(root, "unrestricted-peer-" + i)), r => r.Confirm())).ToArray();
        var received = remotes.Select(_ => Channel.CreateUnbounded<(SessionSnapshot Session, ChatItem Message)>()).ToArray();
        try
        {
            for (var i = 0; i < remotes.Length; i++)
            {
                var queue = received[i];
                remotes[i].MessageReceived += (state, message) => queue.Writer.TryWrite((state, message));
            }
            // Simultaneous authenticated sessions exceed both the old default and selectable maximum.
            await Task.WhenAll(remotes.Select(remote => Connect(host, remote, TransportKind.Bluetooth)));
            await Task.WhenAll(remotes.Select(remote => WaitFor(remote, TransportKind.Bluetooth)));
            await Until(() => host.Snapshot().Count(s => s.Phase == ConnectionPhase.Connected) == remotes.Length);
            Check(host.ActiveCount == 12 && host.CanAccept, "twelve authenticated peers remain connected and admit more connections");
            for (var i = 0; i < remotes.Length; i++)
            {
                // The remote's identity is the peer seen by the host, independent of display names/addresses.
                var remoteId = Convert.ToHexString(new IdentityStore(Path.Combine(root, "unrestricted-peer-" + (i + 1))).Identity.PeerId);
                var route = host.Snapshot().Single(s => s.PeerId == remoteId);
                var text = "独立会话 " + i;
                await host.SendChatAsync(route.SessionId, text);
                Check((await ReadChat(received[i].Reader, text)).Message.Text == text, "encrypted chat reaches peer " + i);
            }
            var firstPeer = host.Snapshot().First(s => s.Phase == ConnectionPhase.Connected);
            var originalCount = host.ActiveCount;
            var duplicateClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(SessionSnapshot state)
            {
                if (state.Phase == ConnectionPhase.Disconnected && state.Detail == "同一设备已有活动会话")
                    duplicateClosed.TrySetResult();
            }
            host.SessionChanged += Observe;
            remotes[0].SessionChanged += Observe;
            try
            {
                await Connect(host, remotes[0], TransportKind.Bluetooth);
                await duplicateClosed.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await Until(() => host.ActiveCount == originalCount);
                Check(host.ActiveCount == 12, "lifting the count limit does not admit duplicate peer transports");
            }
            finally { host.SessionChanged -= Observe; remotes[0].SessionChanged -= Observe; }
            await host.DisconnectAsync(firstPeer.SessionId);
            Check(host.ActiveCount == 11 && host.CanAccept, "disconnecting one peer leaves the other eleven sessions active");
            await host.SuspendAsync();
            Check(!host.CanAccept && host.ActiveCount == 0, "identity-reset lifecycle suspension still blocks new sessions");
            var (left, right) = SecurityHandshakeVerification.MemoryDuplex.Create();
            await using (right)
                Check(await host.AddAsync(new StreamConnection(left, TransportKind.Bluetooth, false), false) is null,
                    "suspended supervisor rejects a new transport without a numeric connection limit");
        }
        finally { foreach (var remote in remotes) await remote.DisposeAsync(); }
    }
}
