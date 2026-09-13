package com.bluelink.android.files

import kotlinx.coroutines.CancellationException
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class PublicationGuardTest {
    @Test fun cancellationBeforeCommitRetainsOriginalAfterBackupRename() {
        val guard = PublicationGuard()
        val names = mutableMapOf(1 to "file.txt")
        val contents = mutableMapOf(1 to "original")
        assertThrows(CancellationException::class.java) {
            PublicationTransaction.replace(1, "file.txt", create = { names[2] = it; 2 },
                write = { contents[it] = "complete new bytes" },
                rename = { id, name -> names[id] = name
                    if (id == 1 && name.startsWith(".bluelink-backup")) guard.cancel()
                    id }, delete = { names.remove(it); contents.remove(it); Unit },
                commit = { guard.commit { fail("Canceled file became visible") } }, checkpoint = guard::checkpoint)
        }
        assertEquals(mapOf(1 to "file.txt"), names)
        assertEquals(mapOf(1 to "original"), contents)
    }
    @Test fun cancellationDuringCopyPreventsRenameAndCommit() {
        val guard = PublicationGuard(); val names = mutableMapOf(1 to "file.txt")
        assertThrows(CancellationException::class.java) {
            PublicationTransaction.replace(1, "file.txt", create = { names[2] = it; 2 },
                write = { guard.cancel(); guard.checkpoint() },
                rename = { _, _ -> fail("Original renamed after cancellation"); 1 },
                delete = { names.remove(it); Unit }, checkpoint = guard::checkpoint)
        }
        assertEquals(mapOf(1 to "file.txt"), names)
    }
    @Test fun alreadyCommittedFileCannotLaterBecomeCanceled() {
        val guard = PublicationGuard()
        assertEquals("saved", guard.commit { "saved" })
        assertFalse(guard.cancel())
        var invoked = false
        assertThrows(IllegalStateException::class.java) { guard.commit { invoked = true } }
        assertFalse(invoked)
    }
    @Test fun lateProgressCannotOverwriteCancellation() {
        val guard = PublicationGuard()
        var status = "CANCELED"
        guard.cancel()
        assertThrows(CancellationException::class.java) { guard.active { status = "COMMITTING" } }
        assertEquals("CANCELED", status)
    }
    @Test fun concurrentCancelWaitsForCommitAndCannotReportFalseCancellation() {
        val guard = PublicationGuard(); val entered = CountDownLatch(1); val finish = CountDownLatch(1)
        val executor = Executors.newFixedThreadPool(2)
        try {
            val commit = executor.submit<String> { guard.commit { entered.countDown(); check(finish.await(2, TimeUnit.SECONDS)); "saved" } }
            assertTrue(entered.await(2, TimeUnit.SECONDS))
            val cancel = executor.submit<Boolean> { guard.cancel() }
            finish.countDown()
            assertEquals("saved", commit.get(2, TimeUnit.SECONDS)); assertFalse(cancel.get(2, TimeUnit.SECONDS))
        } finally { finish.countDown(); executor.shutdownNow() }
    }
}
