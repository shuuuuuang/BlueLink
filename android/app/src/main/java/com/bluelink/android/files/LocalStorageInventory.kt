package com.bluelink.android.files

import java.io.File
import java.nio.file.Files

internal enum class StorageCategory { RECEIVED, THUMBNAILS, UPDATES, SNAPSHOTS, USB_STAGING, DRAFTS, OTHER }
internal data class StorageLocation(val directory: File, val category: StorageCategory)
internal data class LocalStorageInventory(val bytes: Map<StorageCategory, Long>, val partial: Boolean) {
    companion object {
        fun scan(locations: List<StorageLocation>, checkCanceled: () -> Unit = {}): LocalStorageInventory {
            val bytes = StorageCategory.entries.associateWith { 0L }.toMutableMap()
            val seen = mutableSetOf<String>()
            var partial = false; var count = 0
            val deadline = System.nanoTime() + 5_000_000_000L
            for (root in locations.sortedByDescending { it.directory.absolutePath.length }) {
                val pending = java.util.ArrayDeque<File>(); pending.push(root.directory.absoluteFile.normalize())
                while (pending.isNotEmpty()) {
                    checkCanceled()
                    if (++count > 100000 || System.nanoTime() > deadline) return LocalStorageInventory(bytes, true)
                    val directory = pending.pop()
                    if (!seen.add(directory.path)) continue
                    if (Files.isSymbolicLink(directory.toPath())) { partial = true; continue }
                    val children = directory.listFiles()
                    if (children == null) { partial = true; continue }
                    for (file in children) {
                        checkCanceled()
                        if (++count > 100000 || System.nanoTime() > deadline) return LocalStorageInventory(bytes, true)
                        if (Files.isSymbolicLink(file.toPath())) { partial = true; continue }
                        if (file.isDirectory) { pending.push(file); continue }
                        if (!seen.add(file.path)) continue
                        try {
                            val category = if (root.category == StorageCategory.SNAPSHOTS && file.extension == "blm") StorageCategory.USB_STAGING else root.category
                            bytes[category] = Math.addExact(bytes.getValue(category), Files.size(file.toPath()))
                        } catch (_: java.io.IOException) { partial = true }
                        catch (_: SecurityException) { partial = true }
                    }
                }
            }
            return LocalStorageInventory(bytes, partial)
        }
    }
}
