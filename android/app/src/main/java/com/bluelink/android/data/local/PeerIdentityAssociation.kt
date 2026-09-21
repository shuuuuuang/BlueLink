package com.bluelink.android.data.local

import androidx.room.withTransaction
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.domain.IdentityCandidate
import com.bluelink.android.domain.PeerIdentityHint
import java.util.Locale

suspend fun BlueLinkRepository.findIdentityCandidate(peerId: String, hint: String?): IdentityCandidate? {
    if (hint.isNullOrBlank()) return null
    val peers = database.peers().loadAll()
    if (peers.any { it.peerId.equals(peerId, true) }) return null
    val matchingIds = database.peerHints().find(hint).toSet()
    val candidates = peers.filter { it.trustState != "BLOCKED" &&
        (it.peerId in matchingIds || PeerIdentityHint.fromAddress(it.transportAddress) == hint) }
    return candidates.singleOrNull()?.let { IdentityCandidate(it.peerId, it.displayName, it.identityPublicKey, hint) }
}

/** Replay after process interruption. The identity journal was written only after fresh safety-code confirmation. */
suspend fun BlueLinkRepository.applyIdentityAssociations(identity: IdentityStore) = database.withTransaction {
    val associations = identity.identityAssociations()
    val trust = identity.trustedEntries()
    val merged = if (associations.isNotEmpty()) associateComposerDrafts?.invoke(loadDrafts()) else null
    associations.forEach { (oldId, initialTarget) ->
        var target = initialTarget
        val visited = mutableSetOf(oldId)
        while (associations.containsKey(target)) {
            check(visited.add(target)) { "设备身份关联存在循环" }
            target = associations.getValue(target)
        }
        movePeerHistory(oldId, target, trust[target])
    }
    merged?.forEach { (peer, text) -> database.conversations().updateDraft(peer, text) }
}

internal suspend fun BlueLinkRepository.movePeerHistory(oldId: String, newId: String, newKey: ByteArray?) = database.withTransaction {
    require(oldId != newId)
    val previous = database.peers().find(oldId) ?: return@withTransaction
    if (previous.trustState == "RETIRED") return@withTransaction
    val target = database.peers().find(newId)
    database.peers().upsert(previous.copy(peerId = newId, identityPublicKey = newKey,
        trustState = if (newKey == null) "UNKNOWN" else "TRUSTED",
        lastSeenAt = maxOf(previous.lastSeenAt, target?.lastSeenAt ?: 0),
        transportAddress = target?.transportAddress?.takeIf { it.isNotBlank() } ?: previous.transportAddress))
    val conversationId = "peer:${newId.lowercase(Locale.ROOT)}"
    val previousConversation = database.conversations().findForPeer(oldId)
    val targetConversation = database.conversations().findForPeer(newId)
    if (previousConversation != null) database.conversations().upsert(previousConversation.copy(
        conversationId = conversationId, peerId = newId,
        lastActivityAt = maxOf(previousConversation.lastActivityAt, targetConversation?.lastActivityAt ?: 0),
        unreadCount = previousConversation.unreadCount + (targetConversation?.unreadCount ?: 0),
        draft = previousConversation.draft.ifBlank { targetConversation?.draft.orEmpty() }))
    val db = database.openHelper.writableDatabase
    db.execSQL("""UPDATE attachment SET state='FAILED' WHERE transferId IN
        (SELECT transferId FROM transfer WHERE peerId=? AND status NOT IN ('COMPLETED','FAILED','CANCELED'))""", arrayOf(oldId))
    db.execSQL("""UPDATE message SET peerId=?,conversationId=?,
        status=CASE WHEN direction='OUTGOING' AND status IN ('LOCAL_QUEUED','QUEUED','PENDING','SENDING') THEN 'FAILED' ELSE status END WHERE peerId=?""",
        arrayOf(newId, conversationId, oldId))
    db.execSQL("""UPDATE transfer SET peerId=?,
        status=CASE WHEN status NOT IN ('COMPLETED','FAILED','CANCELED') THEN 'FAILED' ELSE status END,
        failureDetail=CASE WHEN status NOT IN ('COMPLETED','FAILED','CANCELED') THEN '设备身份已变化，请确认后手动重试。' ELSE failureDetail END WHERE peerId=?""",
        arrayOf(newId, oldId))
    db.execSQL("UPDATE session_record SET peerId=? WHERE peerId=?", arrayOf(newId, oldId))
    db.execSQL("INSERT OR IGNORE INTO peer_hint(peerId,hint) SELECT ?,hint FROM peer_hint WHERE peerId=?", arrayOf(newId, oldId))
    for (table in listOf("peer_hint", "conversation", "trust")) db.execSQL("DELETE FROM $table WHERE peerId=?", arrayOf(oldId))
    database.peers().upsert(previous.copy(trustState = "RETIRED"))
    if (newKey != null) database.trust().upsert(TrustEntity(newId, newKey, System.currentTimeMillis()))
}
