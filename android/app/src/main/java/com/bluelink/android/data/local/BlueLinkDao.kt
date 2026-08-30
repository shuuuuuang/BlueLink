package com.bluelink.android.data.local

import androidx.room.Dao
import androidx.room.Query
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow

@Dao
interface PeerDao {
    @Query("SELECT * FROM peer ORDER BY lastSeenAt DESC")
    fun observeAll(): Flow<List<PeerEntity>>

    @Query("SELECT * FROM peer WHERE peerId=:peerId")
    suspend fun find(peerId: String): PeerEntity?

    @Query("SELECT * FROM peer ORDER BY lastSeenAt DESC")
    suspend fun loadAll(): List<PeerEntity>

    @Query("SELECT * FROM peer WHERE transportAddress=:address LIMIT 1")
    suspend fun findByTransportAddress(address: String): PeerEntity?

    @Upsert
    suspend fun upsert(value: PeerEntity)
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

    @Query("UPDATE conversation SET unreadCount=0 WHERE peerId=:peerId")
    suspend fun markRead(peerId: String)
}

@Dao
interface MessageDao {
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
