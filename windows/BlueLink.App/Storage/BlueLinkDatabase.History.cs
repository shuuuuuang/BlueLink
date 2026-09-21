namespace BlueLink.Storage;

public sealed partial class BlueLinkDatabase
{
    public sealed record HistoryPage(IReadOnlyList<StoredMessage> Messages, IReadOnlyList<StoredAttachment> Attachments);

    public Task<HistoryPage> LoadHistoryPageAsync(string conversationId, string? anchorId = null,
        bool around = false, int pageSize = 200, CancellationToken token = default) => Run(() =>
    {
        if (pageSize is < 1 or > 400) throw new ArgumentOutOfRangeException(nameof(pageSize));
        using var connection = Open();
        connection.Execute("BEGIN");
        var messages = new List<StoredMessage>();
        long? time = null;
        if (anchorId is not null)
        {
            using var anchor = connection.Prepare("SELECT created_at FROM message WHERE conversation_id=? AND message_id=?");
            anchor.Bind(1,conversationId).Bind(2,anchorId);
            if (!anchor.Read()) { connection.Execute("COMMIT"); return new HistoryPage([],[]); }
            time = anchor.GetInt64(0);
        }
        void Read(bool after, int count)
        {
            var condition = time is null ? "" : after
                ? " AND (created_at>? OR (created_at=? AND message_id>?))"
                : around ? " AND (created_at<? OR (created_at=? AND message_id<=?))"
                : " AND (created_at<? OR (created_at=? AND message_id<?))";
            var order = after ? "ASC" : "DESC";
            using var query = connection.Prepare("SELECT message_id,conversation_id,peer_id,direction,type,content,status,created_at,monotonic_order FROM message WHERE conversation_id=?" + condition +
                $" ORDER BY created_at {order},message_id {order} LIMIT ?");
            query.Bind(1,conversationId); var next=2;
            if(time is { } value) { query.Bind(next++,value).Bind(next++,value).Bind(next++,anchorId); }
            query.Bind(next,count);
            while(query.Read()) messages.Add(new(query.GetString(0),query.GetString(1),query.GetString(2),
                Enum.TryParse<StoredMessageDirection>(query.GetString(3),true,out var direction) ? direction : StoredMessageDirection.Incoming,
                Enum.TryParse<StoredMessageType>(query.GetString(4),true,out var type) ? type : StoredMessageType.System,
                query.GetString(5),query.GetString(6),query.GetInt64(7),query.GetInt64(8)));
        }
        Read(false,around ? (pageSize+1)/2 : pageSize);
        if(around && time is not null) Read(true,pageSize/2);
        messages.Sort((a,b) => a.CreatedAt != b.CreatedAt ? a.CreatedAt.CompareTo(b.CreatedAt) : string.CompareOrdinal(a.MessageId,b.MessageId));
        var attachments = new List<StoredAttachment>();
        if(messages.Count > 0)
        {
            using var query = connection.Prepare("SELECT attachment_id,message_id,transfer_id,file_name,mime_type,size_bytes,sha256,local_path,preview_path,state FROM attachment WHERE message_id IN (" +
                string.Join(",",messages.Select(_=>"?")) + ") ORDER BY rowid");
            for(var i=0;i<messages.Count;i++) query.Bind(i+1,messages[i].MessageId);
            while(query.Read()) attachments.Add(new(query.GetString(0),query.GetString(1),NullableString(query,2),query.GetString(3),query.GetString(4),query.GetInt64(5),
                query.IsNull(6)?null:query.GetBlob(6),NullableString(query,7),NullableString(query,8),query.GetString(9)));
        }
        connection.Execute("COMMIT"); return new HistoryPage(messages,attachments);
    },token);
}
