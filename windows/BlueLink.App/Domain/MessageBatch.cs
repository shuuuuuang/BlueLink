namespace BlueLink.Domain;

public sealed record MessageBatchResult(Guid Id, bool Deleted, string? Reason = null);

public static class MessageBatch
{
    public static IReadOnlyList<ChatItem> Ordered(IEnumerable<ChatItem> messages, IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        return messages.Where(message => selected.Contains(message.Id)).DistinctBy(message => message.Id)
            .OrderBy(message => message.CreatedAt).ThenBy(message => message.Id).ToArray();
    }

    public static string CopyText(IEnumerable<ChatItem> messages) => string.Join(Environment.NewLine,
        messages.SelectMany(message => (message.HasText ? new[] { message.Text } : [])
            .Concat(message.Attachments?.Select(file => $"[{file.FileName}]") ?? [])));

    public static bool CanDelete(ChatItem message) =>
        !(message.Outgoing && message.Status is MessageStatus.LocalQueued or MessageStatus.Sending) &&
        message.Attachments?.Any(file => file.IsTransferActive || file.RecoveryPending) != true;

}
