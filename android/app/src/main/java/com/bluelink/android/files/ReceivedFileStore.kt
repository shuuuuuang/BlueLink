package com.bluelink.android.files

import android.content.ContentValues
import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.DocumentsContract
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException
import java.nio.file.Path
import kotlin.io.path.inputStream
import kotlin.io.path.deleteIfExists

enum class DuplicateChoice { RENAME, REPLACE, CANCEL }

internal object ReceivedFileStore {
    private val publicationMutex = Mutex()
    suspend fun publish(context: Context, source: Path, name: String, mimeType: String,
                        destination: String = "downloads://BlueLink", policy: String = "rename",
                        conflict: suspend (String) -> DuplicateChoice, guard: PublicationGuard = PublicationGuard()): Uri = publicationMutex.withLock {
        val coroutine = kotlinx.coroutines.currentCoroutineContext()
        fun checkpoint() { coroutine.ensureActive(); guard.checkpoint() }
        checkpoint()
        val resolver = context.contentResolver
        val tree = destination.takeIf { it.startsWith("content://") }?.let(Uri::parse)
        val parent = tree?.let { DocumentsContract.buildDocumentUriUsingTree(it, DocumentsContract.getTreeDocumentId(it)) }
        val folder = "${Environment.DIRECTORY_DOWNLOADS}/BlueLink/"
        fun create(displayName: String): Uri {
            if (parent != null) return DocumentsContract.createDocument(resolver, parent, mimeType, displayName)
                ?: throw IOException("无法在自定义目录中创建文件")
            return resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, displayName)
                put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
                put(MediaStore.MediaColumns.RELATIVE_PATH, folder)
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }) ?: throw IOException("无法在 Download/BlueLink 中创建文件")
        }
        fun delete(uri: Uri) {
            if (tree != null) check(DocumentsContract.deleteDocument(resolver, uri)) { "无法清理临时文件" }
            else check(resolver.delete(uri, null, null) == 1) { "无法清理临时文件" }
        }
        fun write(uri: Uri) {
            resolver.openOutputStream(uri, "w").use { output ->
                requireNotNull(output) { "无法写入接收目录" }
                source.inputStream().buffered(128 * 1024).use { input ->
                    val bytes = ByteArray(128 * 1024)
                    while (true) {
                        checkpoint()
                        val count = input.read(bytes)
                        if (count < 0) break
                        if (count > 0) output.write(bytes, 0, count)
                    }
                }
            }
        }
        fun rename(uri: Uri, value: String): Uri {
            val result = if (tree != null) DocumentsContract.renameDocument(resolver, uri, value)
                ?: throw IOException("保存位置不支持重命名")
            else {
                check(resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.DISPLAY_NAME, value) }, null, null) == 1)
                uri
            }
            val actual = resolver.query(result, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME), null, null, null)?.use {
                if (it.moveToFirst()) it.getString(0) else null
            }
            check(actual == value) { "保存位置无法保留要求的文件名称" }
            return result
        }
        fun existing(): Uri? {
            val hits = mutableListOf<Uri>()
            if (tree != null) {
                val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
                resolver.query(children, arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_MIME_TYPE, DocumentsContract.Document.COLUMN_FLAGS), null, null, null)?.use { cursor ->
                    while (cursor.moveToNext()) if (cursor.getString(1) == name) {
                        check(cursor.getString(2) != DocumentsContract.Document.MIME_TYPE_DIR) { "接收目录存在同名文件夹" }
                        hits += DocumentsContract.buildDocumentUriUsingTree(tree, cursor.getString(0))
                    }
                }
            } else resolver.query(MediaStore.Downloads.EXTERNAL_CONTENT_URI, arrayOf(MediaStore.MediaColumns._ID),
                "${MediaStore.MediaColumns.RELATIVE_PATH} = ? AND ${MediaStore.MediaColumns.DISPLAY_NAME} = ? AND ${MediaStore.MediaColumns.OWNER_PACKAGE_NAME} = ?",
                arrayOf(folder, name, context.packageName), null)?.use { cursor ->
                while (cursor.moveToNext()) hits += ContentUris.withAppendedId(MediaStore.Downloads.EXTERNAL_CONTENT_URI, cursor.getLong(0))
            }
            check(hits.size <= 1) { "接收目录存在多个同名文件，请使用自动重命名" }
            return hits.singleOrNull()
        }
        fun commit(uri: Uri) = guard.commit {
            checkpoint()
            if (tree == null) check(resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null) == 1) {
                "接收文件未能公开，请重试保存"
            }
        }
        val original = if (policy == "rename") null else existing()
        val choice = if (original == null) DuplicateChoice.RENAME else when (policy) {
            "overwrite" -> DuplicateChoice.REPLACE
            "ask" -> conflict(name)
            else -> DuplicateChoice.RENAME
        }
        if (choice == DuplicateChoice.CANCEL) { guard.cancel(); throw kotlinx.coroutines.CancellationException("已取消保存同名文件") }
        checkpoint()
        val published = if (choice == DuplicateChoice.REPLACE && original != null) {
            if (tree != null) {
                val flags = resolver.query(original, arrayOf(DocumentsContract.Document.COLUMN_FLAGS), null, null, null)?.use {
                    if (it.moveToFirst()) it.getLong(0) else 0L
                } ?: 0L
                check(flags and DocumentsContract.Document.FLAG_SUPPORTS_RENAME.toLong() != 0L) { "保存位置不支持安全替换，请使用自动重命名" }
            }
            PublicationTransaction.replace(original, name, ::create, ::write, ::rename, ::delete, ::commit, ::checkpoint)
        } else {
            val uri = create(name)
            try { write(uri); commit(uri); uri } catch (failure: Exception) { runCatching { delete(uri) }; throw failure }
        }
        runCatching { source.deleteIfExists() } // Publication is complete even if private staging cleanup fails.
        published
    }
}
