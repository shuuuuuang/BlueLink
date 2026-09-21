package com.bluelink.android.usb

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract as Documents
import com.bluelink.android.files.OwnedDocumentLedger
import java.io.File
import java.io.IOException
import java.util.UUID

internal object MtpOwnedStorage {
    private var instance: OwnedDocumentLedger? = null
    @Synchronized fun ledger(context: Context) = instance ?: OwnedDocumentLedger(File(context.filesDir,"mtp-owned.json")).also { instance=it }
    private fun size(context: Context, raw: String): Long? {
        val uri=Uri.parse(raw); require(uri.authority=="com.android.externalstorage.documents")
        return (context.contentResolver.query(uri,arrayOf(Documents.Document.COLUMN_SIZE),null,null,null)
            ?: throw IOException("USB storage unavailable")).use { cursor ->
            if (!cursor.moveToFirst()) null else if (cursor.isNull(0)) -1 else cursor.getLong(0)
        }
    }
    fun measure(context: Context) = ledger(context).measure { size(context,it) }
    fun collect(context: Context, preview: Boolean, referenced: (UUID)->Boolean) = ledger(context).collect(preview,referenced,
        { size(context,it) }, { raw -> Documents.deleteDocument(context.contentResolver,Uri.parse(raw)) },
        { rawTree,rawDirectory ->
            val directory=Uri.parse(rawDirectory); val tree=Uri.parse(rawTree)
            require(tree.authority=="com.android.externalstorage.documents" && directory.authority==tree.authority)
            val children=Documents.buildChildDocumentsUriUsingTree(tree,Documents.getDocumentId(directory))
            val empty=(context.contentResolver.query(children,arrayOf(Documents.Document.COLUMN_DOCUMENT_ID),null,null,null)
                ?: throw IOException("USB storage unavailable")).use { !it.moveToFirst() }
            empty && Documents.deleteDocument(context.contentResolver,directory)
        })
}
