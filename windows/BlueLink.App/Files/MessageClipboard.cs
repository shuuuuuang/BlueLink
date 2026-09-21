using BlueLink.Domain;

namespace BlueLink.Files;

internal static class MessageClipboard
{
    internal const string OrderedFormat = "BlueLink.OrderedMessages.v1";
    internal sealed record Part(string? Text = null, string? Path = null);

    internal static bool TryReadOrdered(System.Windows.IDataObject data, out Part[] parts)
    {
        parts = [];
        if (!data.GetDataPresent(OrderedFormat) || data.GetData(OrderedFormat) is not string json || json.Length > 8_000_000) return false;
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Part[]>(json);
            if (parsed is null || parsed.Length > 10_000 || parsed.Any(part => part is null ||
                (part.Text is null) == (part.Path is null) || part.Path is { } path &&
                (!System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path)))) return false;
            parts = parsed;
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    internal static bool TryCreateDataObject(IReadOnlyList<ChatItem> messages,
        IEnumerable<TransferItem> transfers, out System.Windows.DataObject? data)
    {
        data = null;
        if (messages.Count == 0) return false;

        var currentTransfers = transfers.ToArray();
        var files = messages.SelectMany(message => message.Attachments ?? [])
            .Select(file => SearchResultActions.Current(file, currentTransfers)).ToArray();
        // Refuse the whole copy instead of silently replacing unavailable files with their names.
        if (files.Any(file => !file.CanOpen || file.IsTransferActive || file.RecoveryPending)) return false;

        data = files.Length == 0 ? new System.Windows.DataObject() :
            FileDragDropService.CreateCopyDataObject(files.Select(file => file.LocalPath!));
        // Keep a readable, ordered fallback for targets that only consume text.
        // Both formats must be published together; Clipboard.SetText would overwrite the file list.
        data.SetText(MessageBatch.CopyText(messages), System.Windows.TextDataFormat.UnicodeText);
        var parts = new List<Part>();
        foreach (var message in messages)
        {
            if (!string.IsNullOrEmpty(message.Text)) parts.Add(new(Text: message.Text));
            foreach (var file in message.Attachments ?? [])
                parts.Add(new(Path: System.IO.Path.GetFullPath(SearchResultActions.Current(file, currentTransfers).LocalPath!)));
        }
        data.SetData(OrderedFormat, System.Text.Json.JsonSerializer.Serialize(parts));
        return true;
    }
}
