package com.bluelink.android.data.local

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

@Database(
    entities = [
        PeerEntity::class,
        ConversationEntity::class,
        MessageEntity::class,
        AttachmentEntity::class,
        TransferEntity::class,
        TransferExtentEntity::class,
        SessionRecordEntity::class,
        TrustEntity::class,
        AppSettingEntity::class,
    ],
    version = 2,
    exportSchema = true,
)
abstract class BlueLinkDatabase : RoomDatabase() {
    abstract fun peers(): PeerDao
    abstract fun conversations(): ConversationDao
    abstract fun messages(): MessageDao
    abstract fun attachments(): AttachmentDao
    abstract fun transfers(): TransferDao
    abstract fun transferExtents(): TransferExtentDao
    abstract fun sessions(): SessionRecordDao
    abstract fun trust(): TrustDao
    abstract fun settings(): AppSettingDao

    companion object {
        @Volatile private var instance: BlueLinkDatabase? = null

        fun open(context: Context): BlueLinkDatabase = instance ?: synchronized(this) {
            instance ?: Room.databaseBuilder(
                context.applicationContext,
                BlueLinkDatabase::class.java,
                "bluelink.db",
            ).addMigrations(MIGRATION_1_2).build().also { instance = it }
        }

        private val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE peer ADD COLUMN transportAddress TEXT NOT NULL DEFAULT ''")
            }
        }
    }
}
