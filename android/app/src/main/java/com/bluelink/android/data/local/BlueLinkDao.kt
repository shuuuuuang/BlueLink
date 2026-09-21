package com.bluelink.android.data.local

import androidx.room.Dao
import androidx.room.Query
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow

@Dao
interface PeerDao {
    @Query("SELECT * FROM peer WHERE trustState != 'RETIRED' ORDER BY lastSeenAt DESC")
    fun observeAll(): Flow<List<PeerEntity>>

    @Query("SELECT * FROM peer WHERE peerId=:peerId")
    suspend fun find(peerId: String): PeerEntity?

    @Query("SELECT * FROM peer WHERE trustState != 'RETIRED' ORDER BY lastSeenAt DESC")
    suspend fun loadAll(): List<PeerEntity>

    @Query("SELECT * FROM peer WHERE transportAddress=:address LIMIT 1")
    suspend fun findByTransportAddress(address: String): PeerEntity?

    @Upsert
    suspend fun upsert(value: PeerEntity)
}

@Dao
interface PeerHintDao {
    @Query("SELECT peerId FROM peer_hint WHERE hint=:hint")
    suspend fun find(hint: String): List<String>
    @Upsert
    suspend fun upsert(value: PeerHintEntity)
}

@Dao
interface ConversationDao {
    @Query("SELECT * FROM conversation ORDER BY lastActivityAt DESC")
    fun observeAll(): Flow<List<ConversationEntity>>

    @Query("SELECT * FROM conversation WHERE peerId=:peerId")
    suspend fun findForPeer(peerId: String): ConversationEntity?

    @Query("SELECT * FROM conversation ORDER BY lastActivityAt DESC")
    suspend fun loadAll(): List<ConversationEntity>

    @Upsert
    suspend fun upsert(value: ConversationEntity)

    @Query("UPDATE conversation SET draft=:text WHERE peerId=:peerId COLLATE NOCASE")
    suspend fun updateDraft(peerId: String, text: String): Int

    @Query("UPDATE conversation SET unreadCount=0")
    suspend fun markAllRead()

    @Query("UPDATE conversation SET unreadCount=0 WHERE peerId=:peerId")
    suspend fun markRead(peerId: String)
}

@Dao
interface MessageDao {
    /** Startup only: a previous process cannot still be sending. Reconnect never replays messages. */
    @Query("UPDATE message SET status='FAILED' WHERE direction='OUTGOING' AND status IN ('SENDING','LOCAL_QUEUED')")
    suspend fun recoverInterruptedOutgoing(): Int


    @Query("SELECT * FROM message WHERE conversationId=:conversationId AND (:beforeTime IS NULL OR createdAt < :beforeTime OR (createdAt=:beforeTime AND messageId < :beforeId)) ORDER BY createdAt DESC,messageId DESC LIMIT :limit")
    suspend fun loadPage(conversationId: String, beforeTime: Long?, beforeId: String?, limit: Int): List<MessageEntity>

    @Query("SELECT * FROM message WHERE conversationId=:conversationId AND (createdAt > :time OR (createdAt=:time AND messageId > :id)) ORDER BY createdAt,messageId LIMIT :limit")
    suspend fun loadAfter(conversationId: String, time: Long, id: String, limit: Int): List<MessageEntity>

    @Query("SELECT * FROM message WHERE conversationId=:conversationId ORDER BY monotonicOrder, createdAt")
    fun observeConversation(conversationId: String): Flow<List<MessageEntity>>

    @Query("SELECT * FROM message WHERE conversationId=:conversationId ORDER BY monotonicOrder, createdAt")
    suspend fun loadConversation(conversationId: String): List<MessageEntity>

    @Query("SELECT * FROM message WHERE messageId=:messageId")
    suspend fun find(messageId: String): MessageEntity?

    @Upsert
    suspend fun upsert(value: MessageEntity)

    @Query("DELETE FROM message")
    suspend fun deleteAll()

    @Query("DELETE FROM message WHERE messageId=:messageId")
    suspend fun delete(messageId: String)

    @Query("DELETE FROM message WHERE conversationId=:conversationId")
    suspend fun deleteConversation(conversationId: String)

    @Query("DELETE FROM message WHERE createdAt < :cutoff")
    suspend fun deleteBefore(cutoff: Long)
}

@Dao
interface AttachmentDao {
    @Query("SELECT localUri FROM attachment WHERE localUri IS NOT NULL UNION SELECT previewUri FROM attachment WHERE previewUri IS NOT NULL UNION SELECT localUri FROM transfer WHERE localUri IS NOT NULL UNION SELECT snapshotPath FROM transfer WHERE snapshotPath IS NOT NULL")
    suspend fun referencedFiles(): List<String>

    @Query("SELECT * FROM attachment WHERE messageId IN (:ids) ORDER BY rowid")
    suspend fun loadForMessages(ids: List<String>): List<AttachmentEntity>

    @Query("SELECT attachment.* FROM attachment INNER JOIN message ON attachment.messageId=message.messageId WHERE message.conversationId=:conversationId ORDER BY attachment.rowid")
    suspend fun loadForConversation(conversationId: String): List<AttachmentEntity>

    @Query("SELECT * FROM attachment WHERE messageId=:messageId")
    fun observeForMessage(messageId: String): Flow<List<AttachmentEntity>>

    @Query("SELECT * FROM attachment WHERE attachmentId=:attachmentId")
    suspend fun find(attachmentId: String): AttachmentEntity?

    @Query("SELECT * FROM attachment WHERE messageId=:messageId ORDER BY rowid")
    suspend fun loadForMessage(messageId: String): List<AttachmentEntity>

    @Upsert
    suspend fun upsert(value: AttachmentEntity)
}

@Dao
interface TransferDao {
    @Query("SELECT * FROM transfer ORDER BY updatedAt DESC")
    fun observeAll(): Flow<List<TransferEntity>>

    @Query("SELECT * FROM transfer WHERE transferId=:transferId")
    suspend fun find(transferId: String): TransferEntity?

    @Query("SELECT * FROM transfer WHERE status NOT IN ('COMPLETED','FAILED','CANCELED') ORDER BY updatedAt")
    suspend fun loadIncomplete(): List<TransferEntity>

    @Query("SELECT * FROM transfer WHERE peerId=:peerId ORDER BY updatedAt DESC")
    suspend fun loadForPeer(peerId: String): List<TransferEntity>

    @Upsert
    suspend fun upsert(value: TransferEntity)

    @Query("DELETE FROM transfer")
    suspend fun deleteAll()

    @Query("DELETE FROM transfer WHERE transferId=:transferId")
    suspend fun delete(transferId: String)

    @Query("DELETE FROM transfer WHERE updatedAt < :cutoff")
    suspend fun deleteBefore(cutoff: Long)
}

@Dao
interface TransferExtentDao {
    @Query("SELECT * FROM transfer_extent WHERE transferId=:transferId ORDER BY extentIndex")
    suspend fun loadForTransfer(transferId: String): List<TransferExtentEntity>

    @Upsert
    suspend fun upsert(value: TransferExtentEntity)

    @Query("DELETE FROM transfer_extent WHERE transferId=:transferId")
    suspend fun deleteForTransfer(transferId: String)
}

@Dao
interface SessionRecordDao {
    @Upsert
    suspend fun upsert(value: SessionRecordEntity)
}

@Dao
interface TrustDao {
    @Query("SELECT * FROM trust ORDER BY trustedAt DESC")
    fun observeAll(): Flow<List<TrustEntity>>

    @Query("SELECT * FROM trust WHERE peerId=:peerId")
    suspend fun find(peerId: String): TrustEntity?

    @Query("SELECT * FROM trust")
    suspend fun loadAll(): List<TrustEntity>

    @Upsert
    suspend fun upsert(value: TrustEntity)

    @Query("DELETE FROM trust WHERE peerId=:peerId")
    suspend fun delete(peerId: String)
}

@Dao
interface AppSettingDao {
    @Query("SELECT * FROM app_setting")
    fun observeAll(): Flow<List<AppSettingEntity>>

    @Query("SELECT * FROM app_setting")
    suspend fun loadAll(): List<AppSettingEntity>

    @Upsert
    suspend fun upsert(value: AppSettingEntity)
}
