package com.bluelink.android.files

import org.junit.Assert.*
import org.junit.Test
import java.io.IOException

class PublicationTransactionTest {
    private val names = mutableMapOf(1 to "file.txt")
    private val contents = mutableMapOf(1 to "original")
    private fun replace(writeFails: Boolean = false, renameFails: Boolean = false, commitFails: Boolean = false): Int = PublicationTransaction.replace(
        1, "file.txt", create = { names[2] = it; contents[2] = ""; 2 }, write = {
            contents[it] = "partial"
            if (writeFails) throw IOException("disk full")
            contents[it] = "new"
        }, rename = { id, name ->
            if (renameFails && id == 2 && name == "file.txt") throw IOException("rename failed")
            names[id] = name; id
        }, delete = { names.remove(it); contents.remove(it) }, commit = { if (commitFails) throw IOException("publication failed") })
    @Test fun successfulReplacementCommitsNewFileBeforeRemovingOldFile() {
        assertEquals(2, replace()); assertEquals(mapOf(2 to "file.txt"), names); assertEquals("new", contents[2])
    }
    @Test fun incompleteCopyLeavesOriginalIntact() {
        assertThrows(IOException::class.java) { replace(writeFails = true) }
        assertEquals(mapOf(1 to "file.txt"), names); assertEquals("original", contents[1])
    }
    @Test fun failedRenameRestoresOriginalNameAndBytes() {
        assertThrows(IOException::class.java) { replace(renameFails = true) }
        assertEquals(mapOf(1 to "file.txt"), names); assertEquals("original", contents[1])
    }
    @Test fun failedPublicationRetainsTheOriginal() {
        assertThrows(IOException::class.java) { replace(commitFails = true) }
        assertEquals(mapOf(1 to "file.txt"), names); assertEquals("original", contents[1])
    }

}
