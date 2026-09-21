package com.bluelink.android.files

import android.content.Context
import android.content.ContentUris
import android.net.Uri
import android.provider.MediaStore
import com.bluelink.android.BlueLinkApplication
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withContext
import kotlin.coroutines.coroutineContext
import java.io.File

internal data class StorageUsage(val bytes: Map<StorageCategory,Long>, val partial: Boolean, val unknown: Set<StorageCategory>) {
    val totalBytes: Long get() = bytes.values.sum()
}

internal suspend fun readStorageUsage(context: Context): StorageUsage = withContext(Dispatchers.IO) {
    val coroutine = coroutineContext
    val locations = mutableListOf(StorageLocation(File(context.applicationInfo.dataDir),StorageCategory.OTHER))
    for ((file,category) in listOf(File(context.filesDir,"attachments") to StorageCategory.RECEIVED,
        File(context.filesDir,"received") to StorageCategory.RECEIVED,
        File(context.filesDir,"composer") to StorageCategory.DRAFTS,
        File(context.cacheDir,"display-thumbnails") to StorageCategory.THUMBNAILS,
        File(context.cacheDir,"outgoing") to StorageCategory.SNAPSHOTS,
        File(context.cacheDir,"updates") to StorageCategory.UPDATES)) if (file.exists()) locations += StorageLocation(file,category)
    val local = LocalStorageInventory.scan(locations) { coroutine.ensureActive() }
    val bytes = local.bytes.toMutableMap(); var partial = local.partial
    val seen = mutableSetOf<String>(); val resolver = context.contentResolver
    fun addReceived(size: Long) { if (size < 0) partial = true else bytes[StorageCategory.RECEIVED] = Math.addExact(bytes.getValue(StorageCategory.RECEIVED),size) }
    try {
        val cursor = resolver.query(MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            arrayOf(MediaStore.MediaColumns._ID,MediaStore.MediaColumns.SIZE),
            "${MediaStore.MediaColumns.OWNER_PACKAGE_NAME} = ?",arrayOf(context.packageName),null)
        if (cursor == null) partial = true
        cursor?.use {
            while (it.moveToNext()) {
                coroutine.ensureActive()
                seen += ContentUris.withAppendedId(MediaStore.Downloads.EXTERNAL_CONTENT_URI,it.getLong(0)).toString()
                if (it.isNull(1)) partial = true else addReceived(it.getLong(1))
            }
        }
    } catch (_: SecurityException) { partial = true }
    catch (_: java.io.IOException) { partial = true }
    val records = (context.applicationContext as BlueLinkApplication).localRepository.transfers.first()
    for (item in records.filter { it.direction == "INCOMING" && it.status == "COMPLETED" }) {
        coroutine.ensureActive()
        val raw = item.localUri ?: continue; val uri = Uri.parse(raw)
        if (uri.scheme == "file" || !seen.add(raw)) continue
        try {
            val descriptor = resolver.openAssetFileDescriptor(uri,"r")
            if (descriptor == null) partial = true
            descriptor?.use { addReceived(it.length) }
        } catch (_: java.io.IOException) { partial = true }
        catch (_: SecurityException) { partial = true }
    }
    val usb=runCatching { com.bluelink.android.usb.MtpOwnedStorage.measure(context) }.getOrNull()
    if (usb!=null) bytes[StorageCategory.USB_STAGING]=Math.addExact(bytes.getValue(StorageCategory.USB_STAGING),usb.first)
    // Legacy/unindexed provider objects remain deliberately unclaimed.
    StorageUsage(bytes,partial || usb?.second == true || com.bluelink.android.usb.MtpSpool.hasGrant(context),if (usb==null) setOf(StorageCategory.USB_STAGING) else emptySet())
}
