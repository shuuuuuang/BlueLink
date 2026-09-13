package com.bluelink.android.usb

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract as Documents
import java.io.IOException
import java.security.SecureRandom
import java.util.UUID

/** Only an explicitly granted local tree is accessible. Public files contain ciphertext, never file keys. */
internal class MtpSpool private constructor(private val context: Context, val tree: Uri, val directory: Uri,
    val epoch: String, val path: List<String>, val proof: ByteArray) : AutoCloseable {
    private val resolver get() = context.contentResolver
    private val owned = java.util.concurrent.ConcurrentHashMap.newKeySet<Uri>()
    fun createBlob(name: String): Uri {
        validateName(name)
        check(find(name) == null) { "USB 中转文件已存在" }
        return (Documents.createDocument(resolver, directory, "application/octet-stream", name)
            ?: throw IOException("无法创建 USB 中转文件")).also { owned.add(it) }
    }
    fun find(name: String): Uri? {
        validateName(name)
        val children = Documents.buildChildDocumentsUriUsingTree(tree, Documents.getDocumentId(directory))
        val hits = mutableListOf<Uri>()
        resolver.query(children, arrayOf(Documents.Document.COLUMN_DOCUMENT_ID, Documents.Document.COLUMN_DISPLAY_NAME), null, null, null)?.use { cursor ->
            while (cursor.moveToNext()) if (cursor.getString(1) == name)
                hits.add(Documents.buildDocumentUriUsingTree(tree, cursor.getString(0)))
        }
        if (hits.size > 1) throw IOException("USB 中转文件名称冲突")
        return hits.singleOrNull()
    }
    fun deleteBlob(name: String) {
        find(name)?.let { uri -> Documents.deleteDocument(resolver, uri); owned.remove(uri) }
    }
    override fun close() {
        // Delete only exact objects created by this session. Never recursively delete the user's tree.
        owned.toList().forEach { uri -> runCatching { Documents.deleteDocument(resolver, uri) }; owned.remove(uri) }
        runCatching {
            val children = Documents.buildChildDocumentsUriUsingTree(tree, Documents.getDocumentId(directory))
            val empty = resolver.query(children, arrayOf(Documents.Document.COLUMN_DOCUMENT_ID), null, null, null)?.use { !it.moveToFirst() } == true
            if (empty) Documents.deleteDocument(resolver, directory)
        }
    }
    companion object {
        private const val AUTHORITY = "com.android.externalstorage.documents"
        private fun preferences(context: Context) = context.getSharedPreferences("bluelink_mtp", Context.MODE_PRIVATE)
        fun hasGrant(context: Context): Boolean {
            val raw = preferences(context).getString("tree", null) ?: return false
            return context.contentResolver.persistedUriPermissions.any { it.uri.toString() == raw && it.isReadPermission && it.isWritePermission }
        }
        fun grant(context: Context, uri: Uri) {
            require(uri.authority == AUTHORITY) { "请选择手机本地存储中的蓝联专用文件夹" }
            val segments = Documents.getTreeDocumentId(uri).substringAfter(':', "").split('/').filter { it.isNotBlank() }
            require(segments.isNotEmpty()) { "请选择一个专用子文件夹" }
            context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            check(preferences(context).edit().putString("tree", uri.toString()).commit())
        }
        fun open(context: Context): MtpSpool {
            check(hasGrant(context)) { "需要授权蓝联 USB 中转文件夹" }
            val tree = Uri.parse(preferences(context).getString("tree", null))
            val parent = Documents.buildDocumentUriUsingTree(tree, Documents.getTreeDocumentId(tree))
            val epoch = UUID.randomUUID().toString().replace("-", "")
            val name = "bluelink-usb-$epoch"
            val directory = Documents.createDocument(context.contentResolver, parent, Documents.Document.MIME_TYPE_DIR, name)
                ?: throw IOException("无法创建 USB 会话文件夹")
            val path = Documents.getDocumentId(directory).substringAfter(':').split('/')
            val proof = ByteArray(32).also { SecureRandom().nextBytes(it) }
            val spool = MtpSpool(context, tree, directory, epoch, path, proof)
            try {
                val marker = Documents.createDocument(context.contentResolver, directory, "application/octet-stream", "peer-proof")
                    ?: throw IOException("无法写入 USB 认证标记")
                spool.owned.add(marker)
                context.contentResolver.openOutputStream(marker, "wt")!!.use { it.write(proof) }
                return spool
            } catch (error: Throwable) { spool.close(); throw error }
        }
        private fun validateName(name: String) {
            require(Regex("[a-f0-9]{32}\\.blm").matches(name)) { "Invalid USB spool name" }
        }
    }
}
