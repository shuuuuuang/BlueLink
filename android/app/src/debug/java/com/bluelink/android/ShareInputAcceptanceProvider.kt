package com.bluelink.android
import android.content.ContentProvider
import android.content.ContentValues
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File
import java.io.FileNotFoundException

/** Debug-owned generated input only. No path supplied by the caller is used as a filesystem path. */
class ShareInputAcceptanceProvider: ContentProvider() {
    override fun onCreate()=true
    override fun getType(uri:Uri)="application/pdf"
    override fun query(uri:Uri,projection:Array<out String>?,selection:String?,args:Array<out String>?,sort:String?)=
        MatrixCursor(arrayOf(OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE)).apply {
            addRow(arrayOf(if(uri.lastPathSegment=="bad-name") "../bad.pdf" else "QA-unknown-size.pdf",
                if(uri.lastPathSegment=="too-large") 3L*1024*1024*1024 else null))
        }
    override fun openFile(uri:Uri,mode:String):ParcelFileDescriptor {
        require(mode=="r")
        if(uri.lastPathSegment=="denied") throw FileNotFoundException("QA revoked source")
        val file=File(requireNotNull(context).cacheDir,"qa-share-input.bin")
        if(!file.exists()) file.writeBytes(ByteArray(256*1024){(it%251).toByte()})
        return ParcelFileDescriptor.open(file,ParcelFileDescriptor.MODE_READ_ONLY)
    }
    override fun insert(uri:Uri,values:ContentValues?):Uri?=null
    override fun update(uri:Uri,values:ContentValues?,selection:String?,args:Array<out String>?)=0
    override fun delete(uri:Uri,selection:String?,args:Array<out String>?)=0
}
