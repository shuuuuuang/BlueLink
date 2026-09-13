namespace BlueLink.Domain;

public static class AutoConnectionPolicy
{
    public static bool ShouldConnect(bool trusted, bool connectedThisRun, bool manuallyDisconnected,
        bool autoConnect, bool reconnect) => trusted && !manuallyDisconnected && (connectedThisRun ? reconnect : autoConnect);
}
