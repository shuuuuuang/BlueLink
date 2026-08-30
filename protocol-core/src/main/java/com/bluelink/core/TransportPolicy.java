package com.bluelink.core;

public final class TransportPolicy {
    private TransportPolicy() {}

    public static TransportPlan select(TransportCapabilities local, TransportCapabilities remote,
                                       byte[] localPeer, byte[] remotePeer, byte[] localNonce, byte[] remoteNonce) {
        if (local.platform() == Platform.ANDROID && remote.platform() == Platform.ANDROID
                && supportsCoc(local, remote)) {
            return elected(TransportType.L2CAP_COC, localPeer, remotePeer, localNonce, remoteNonce, false);
        }
        if (local.platform() != remote.platform() && supportsRfcomm(local, remote)) {
            return new TransportPlan(TransportType.RFCOMM,
                    local.platform() == Platform.WINDOWS ? TransportPlan.Role.LISTENER : TransportPlan.Role.DIALER, false);
        }
        if (supportsRfcomm(local, remote)) {
            return elected(TransportType.RFCOMM, localPeer, remotePeer, localNonce, remoteNonce, false);
        }
        if (local.gattControl() && remote.gattControl()) {
            return elected(TransportType.GATT_FALLBACK, localPeer, remotePeer, localNonce, remoteNonce, true);
        }
        throw new IllegalStateException("No mutually supported Bluetooth transport");
    }

    private static boolean supportsCoc(TransportCapabilities a, TransportCapabilities b) {
        return (a.l2capListen() && b.l2capDial()) || (a.l2capDial() && b.l2capListen());
    }

    private static boolean supportsRfcomm(TransportCapabilities a, TransportCapabilities b) {
        return (a.rfcommListen() && b.rfcommDial()) || (a.rfcommDial() && b.rfcommListen());
    }

    private static TransportPlan elected(TransportType type, byte[] localPeer, byte[] remotePeer,
                                         byte[] localNonce, byte[] remoteNonce, boolean degraded) {
        boolean listener = RoleElection.localIsListener(localPeer, remotePeer, localNonce, remoteNonce);
        return new TransportPlan(type, listener ? TransportPlan.Role.LISTENER : TransportPlan.Role.DIALER, degraded);
    }
}
