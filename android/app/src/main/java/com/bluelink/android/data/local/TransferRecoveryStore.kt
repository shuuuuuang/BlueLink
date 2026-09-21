package com.bluelink.android.data.local

import com.bluelink.android.domain.*
import com.bluelink.core.AttachmentRole
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID

/** Operational metadata. Loading it never queues network traffic or treats byte counts as checkpoints. */
class TransferRecoveryStore(private val root: File, private val write: (File, ByteArray) -> Unit = ::atomicWrite) {
    private val epoch = UUID.randomUUID().toString()
    private val records = linkedMapOf<UUID, JSONObject>()
    init {
        root.listFiles { f -> f.extension == "json" }?.forEach { file ->
            val row = JSONObject(file.readText()); val id = UUID.fromString(row.getString("id"))
            check(row.getInt("version") == 1 && file.nameWithoutExtension == id.toString()) { "Unsupported recovery record" }
            records[id] = row
        }
    }
    @Synchronized fun record(owner: UUID, ownerStarted: Long, transport: String, item: TransferItem): Boolean {
        if (item.role == AttachmentRole.IMAGE_PREVIEW || item.peerId.isNullOrBlank()) return true
        val old = records[item.id]
        if (old != null) {
            val incomingOffer = !item.outgoing && item.attemptId == null && item.status == TransferStatus.OFFERED
            val newAttempt = item.status in HistoryQuery.activeStatuses && (old.getString("epoch") != epoch || incomingOffer ||
                old.nullable("attempt") != item.attemptId?.toString() && item.attemptSequence > old.getLong("sequence"))
            if (old.optBoolean("dismissed") && !newAttempt || !old.getString("peer").equals(item.peerId, true) || old.getBoolean("outgoing") != item.outgoing) return false
            if (old.getString("epoch") == epoch) {
                if (old.getString("owner") != owner.toString() && ownerStarted <= old.getLong("ownerStarted")) return false
                if (old.nullable("attempt") == item.attemptId?.toString() && old.getString("owner") == owner.toString()) {
                    if (TransferStatus.valueOf(old.getString("status")) !in HistoryQuery.activeStatuses && !incomingOffer) return false
                } else if ((item.outgoing || item.attemptId != null || old.nullable("attempt") != null) && item.attemptSequence <= old.getLong("sequence")) return false
            } else if (item.status !in HistoryQuery.activeStatuses) return false
        }
        val now = System.currentTimeMillis()
        if (old != null && old.getString("epoch") == epoch && old.getString("owner") == owner.toString() &&
            old.nullable("attempt") == item.attemptId?.toString() && old.getString("status") == item.status.name && now - old.getLong("written") < 1000) return true
        val row = JSONObject().put("version",1).put("epoch",epoch).put("owner",owner.toString()).put("ownerStarted",ownerStarted).put("transport",transport)
            .put("id",item.id.toString()).put("name",item.name).put("total",item.totalBytes).put("completed",item.completedBytes).put("outgoing",item.outgoing)
            .put("status",item.status.name).put("peer",item.peerId).put("message",item.messageId?.toString()).put("attachment",item.attachmentId?.toString())
            .put("mime",item.mimeType).put("uri",item.localUri).put("sha256",item.sourceSha256).put("attempt",item.attemptId?.toString())
            .put("sequence",item.attemptSequence).put("role",item.role.name).put("started",item.startedAtEpochMs)
            .put("updated",item.updatedAtEpochMs).put("failure",item.failureDetail).put("written",now)
        save(item.id,row); return true
    }
    /** Only receives associations already confirmed by the identity safety-code flow. */
    @Synchronized fun associate(associations: Map<String, String>) {
        val aliases = associations.mapKeys { it.key.lowercase(java.util.Locale.ROOT) }
        val resolved = aliases.keys.associateWith { source ->
            var target = source
            val visited = mutableSetOf<String>()
            while (aliases.containsKey(target.lowercase(java.util.Locale.ROOT))) {
                check(visited.add(target.lowercase(java.util.Locale.ROOT))) { "Invalid recovery identity association" }
                target = aliases.getValue(target.lowercase(java.util.Locale.ROOT))
                check(target.isNotBlank()) { "Invalid recovery identity association" }
            }
            target
        }
        val affected = records.filterValues { it.getString("peer").lowercase(java.util.Locale.ROOT) in resolved }
        check(affected.values.none { it.getString("epoch") == epoch && TransferStatus.valueOf(it.getString("status")) in HistoryQuery.activeStatuses }) {
            "设备仍有活动传输，请等待传输结束后关联身份。"
        }
        for ((id, old) in affected) {
            save(id, JSONObject(old.toString()).put("peer", resolved.getValue(old.getString("peer").lowercase(java.util.Locale.ROOT))))
        }
    }
    @Synchronized fun referencesTemporary(id: UUID): Boolean {
        val row = records[id] ?: return false
        return !row.optBoolean("dismissed") && (TransferStatus.valueOf(row.getString("status")) in HistoryQuery.activeStatuses || row.getString("status") == "FAILED")
    }
    @Synchronized fun dismiss(id: UUID): Boolean {
        val old = records[id] ?: return true
        if (old.getString("epoch") == epoch && TransferStatus.valueOf(old.getString("status")) in HistoryQuery.activeStatuses) return false
        save(id,JSONObject(old.toString()).put("dismissed",true)); return true
    }
    @Synchronized fun mergeHistory(history: List<TransferItem>): List<TransferItem> {
        val result = history.associateBy { it.id }.toMutableMap()
        for ((id,row) in records) {
            if (row.optBoolean("dismissed")) { result.remove(id); continue }
            val status = TransferStatus.valueOf(row.getString("status"))
            val unfinished = status in HistoryQuery.activeStatuses || status == TransferStatus.FAILED
            if (!unfinished && id !in result) continue
            val pending = unfinished && row.getString("epoch") != epoch
            result[id] = TransferItem(id=id,name=row.getString("name"),totalBytes=row.getLong("total"),completedBytes=row.getLong("completed"),
                outgoing=row.getBoolean("outgoing"),status=if (pending) TransferStatus.FAILED else status,
                messageId=row.nullable("message")?.let(UUID::fromString),attachmentId=row.nullable("attachment")?.let(UUID::fromString),
                mimeType=row.getString("mime"),localUri=row.nullable("uri"),peerId=row.getString("peer"),role=AttachmentRole.valueOf(row.getString("role")),
                startedAtEpochMs=row.getLong("started"),updatedAtEpochMs=row.getLong("updated"),sourceSha256=row.nullable("sha256"),
                attemptId=if(pending) null else row.nullable("attempt")?.let(UUID::fromString),attemptSequence=if(pending) 0 else row.getLong("sequence"),
                failureDetail=if(pending) if(row.getBoolean("outgoing")) "应用中断后保留的任务，请确认设备连接后手动重试" else "应用中断后保留的接收任务，等待发送方重试" else row.nullable("failure"),
                recoveryPending=pending)
        }
        return result.values.sortedByDescending { it.startedAtEpochMs }
    }
    private fun save(id: UUID, row: JSONObject) { write(File(root,"$id.json"),row.toString().toByteArray(Charsets.UTF_8)); records[id]=row }
    private fun JSONObject.nullable(key: String): String? = if (isNull(key) || !has(key)) null else getString(key)
    companion object {
        private fun atomicWrite(file: File, bytes: ByteArray) {
            check(file.parentFile!!.isDirectory || file.parentFile!!.mkdirs()) { "Recovery directory unavailable" }
            val temporary=File(file.path+".new")
            FileOutputStream(temporary).use { it.write(bytes); it.fd.sync() }
            Files.move(temporary.toPath(),file.toPath(),StandardCopyOption.REPLACE_EXISTING,StandardCopyOption.ATOMIC_MOVE)
        }
    }
}
