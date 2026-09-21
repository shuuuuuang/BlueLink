package com.bluelink.android.sharing

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class ShareCompletionTest {
    private val file = SharedFile(UUID.randomUUID(), "qa.txt", "text/plain", 12, "qa")
    private fun request(text: String = "", files: List<SharedFile> = listOf(file)) =
        SharedRequest(UUID.randomUUID(), "qa", text, files, "chosen-peer")

    @Test fun filesOnlyNavigateWhenEveryFileWasSubmitted() {
        val pending = request(files = listOf(file.copy(submitted = true), file.copy(id = UUID.randomUUID())))
        assertNull(pending.completedPeerId)
        assertEquals("chosen-peer", pending.copy(files = pending.files.map { it.copy(submitted = true) }).completedPeerId)
    }
    @Test fun mixedShareWaitsForTextAndFiles() {
        val pending = request("message")
        assertNull(pending.copy(textSubmitted = true).completedPeerId)
        assertNull(pending.copy(files = listOf(file.copy(submitted = true))).completedPeerId)
        assertEquals("chosen-peer", pending.copy(textSubmitted = true, files = listOf(file.copy(submitted = true))).completedPeerId)
    }
    @Test fun successfulTextShareUsesTheSavedTarget() {
        val pending = request("message", emptyList())
        assertNull(pending.completedPeerId)
        assertEquals("chosen-peer", pending.copy(textSubmitted = true).completedPeerId)
    }
    @Test fun emptyOrUntargetedRequestCannotNavigate() {
        assertNull(request(files = emptyList()).completedPeerId)
        val complete = request(files = listOf(file.copy(submitted = true)))
        assertNull(complete.copy(peerId = null).completedPeerId)
        assertNull(complete.copy(peerId = " ").completedPeerId)
    }
}
