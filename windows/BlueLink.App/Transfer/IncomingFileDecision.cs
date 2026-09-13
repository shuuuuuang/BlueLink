namespace BlueLink.Transfer;

public sealed record IncomingFileDecision(string PeerName, FileOffer Offer, bool RequiresConfirmation,
    bool NameConflict, string DuplicatePolicy);

public static class DuplicateFilePolicy
{
    public static string Normalize(string? value) => value is "ask" or "overwrite" ? value : "rename";
}
