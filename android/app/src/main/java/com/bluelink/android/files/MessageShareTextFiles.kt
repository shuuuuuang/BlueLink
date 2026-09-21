package com.bluelink.android.files

import android.content.Context
import android.net.Uri
import com.bluelink.android.domain.ChatAttachment
import java.io.File
import java.util.UUID

/** Only our marked share snapshots expire; never remove originals or fresh receiver grants. */
internal object MessageShareTextFiles {
    private const val RETAIN_MS = 7L * 24 * 60 * 60 * 1000
    fun create(context: Context, text: String, displayName: String, created: MutableList<File>): ChatAttachment {
        val root = File(context.cacheDir, "shared/message-text")
        check(root.isDirectory || root.mkdirs())
        OwnedTemporaryFiles.collect(root, { false }, preview = false, minimumAgeMs = RETAIN_MS)
        val id = UUID.randomUUID()
        val file = File(root, "$id.txt")
        OwnedTemporaryFiles.register(file, id)
        created += file
        try { file.writeText(text, Charsets.UTF_8) }
        finally { OwnedTemporaryFiles.release(file) }
        return ChatAttachment(id, id, displayName, "text/plain", file.length(), Uri.fromFile(file).toString(), "COMPLETED")
    }
}
