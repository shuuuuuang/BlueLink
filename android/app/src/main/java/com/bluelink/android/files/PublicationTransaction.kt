package com.bluelink.android.files

import java.util.UUID

/** The original is retained under a backup name until the fully written replacement has its final name. */
internal object PublicationTransaction {
    fun <T> replace(original: T, name: String, create: (String) -> T, write: (T) -> Unit,
                    rename: (T, String) -> T, delete: (T) -> Unit, commit: (T) -> Unit = {}, checkpoint: () -> Unit = {}): T {
        val id = UUID.randomUUID().toString()
        var staged = create(".bluelink-new-$id-$name")
        check(staged != original) { "保存位置没有创建独立的临时文件" }
        var backup: T? = null
        try {
            checkpoint()
            write(staged)
            checkpoint()
            backup = rename(original, ".bluelink-backup-$id-$name")
            checkpoint()
            staged = rename(staged, name)
            checkpoint()
            commit(staged)
        } catch (failure: Exception) {
            try { delete(staged) } catch (cleanup: Exception) { failure.addSuppressed(cleanup) }
            backup?.let { saved ->
                try { rename(saved, name) }
                catch (restore: Exception) { failure.addSuppressed(restore) }
            }
            throw failure
        }
        // A provider can refuse backup cleanup. The new file is already complete;
        // retaining an extra backup is preferable to losing either copy.
        runCatching { backup?.let(delete) }
        return staged
    }
}
