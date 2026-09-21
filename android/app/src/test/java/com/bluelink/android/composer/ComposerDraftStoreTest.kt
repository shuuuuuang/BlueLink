package com.bluelink.android.composer

import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files
import java.util.UUID

class ComposerDraftStoreTest {
    @Test fun startupCleanupPreservesDraftHistoryAndNativeUndo() {
        val root = Files.createTempDirectory("composer-owned").toFile()
        val store = ComposerDraftStore(root)
        fun owned(): ComposerAttachment {
            val id = UUID.randomUUID(); val file = store.newFile(id)
            com.bluelink.android.files.OwnedTemporaryFiles.register(file,id)
            file.writeBytes(ByteArray(23))
            com.bluelink.android.files.OwnedTemporaryFiles.release(file)
            return ComposerAttachment(id,file.absolutePath,"image.png",23)
        }
        val draft = owned(); val history = owned(); val orphan = owned()
        store.edit("peer",listOf(ComposerPart(draft.id,file=draft)))
        val startup = ComposerDraftStore(root)
        assertEquals(1,startup.collectStartupOrphans(setOf(history.path),0).files)
        assertFalse(java.io.File(orphan.path).exists())
        assertTrue(java.io.File(draft.path).exists()); assertTrue(java.io.File(history.path).exists())
        startup.edit("peer",emptyList())
        assertEquals(0,startup.collectStartupOrphans(emptySet(),0).files)
        assertTrue(java.io.File(draft.path).exists())
    }

    @Test fun heightIsBoundedAndPersisted() {
        val root = Files.createTempDirectory("composer-height").toFile(); val store = ComposerDraftStore(root)
        assertEquals(68f, store.height())
        store.setHeight(1f); assertEquals(68f, ComposerDraftStore(root).height())
        store.setHeight(230f); assertEquals(230f, ComposerDraftStore(root).height())
        assertEquals(68f, ComposerDraftStore.clampHeight(Float.NaN)); assertEquals(280f, ComposerDraftStore.clampHeight(900f))
        assertEquals(160f, ComposerDraftStore.clampHeight(280f, 340f))
    }
    @Test fun mixedDocumentPreservesOrderAndReopeningNeverAutoSends() {
        val root = Files.createTempDirectory("composer-order").toFile(); val store = ComposerDraftStore(root)
        val file = ComposerAttachment(UUID.randomUUID(), "qa-path", "report.pdf", 123)
        val parts = listOf(ComposerPart(text = "text1"), ComposerPart(file.id, file = file), ComposerPart(text = "text2"))
        store.edit("PEER", parts)
        assertEquals(listOf("text1", "report.pdf", "text2"), store.take("peer").map { it.text ?: it.file!!.name })
        store.acknowledge("peer", parts[0].id); store.edit("peer", listOf(ComposerPart(text = "new text")))
        val reopened = ComposerDraftStore(root)
        assertEquals(listOf("report.pdf", "text2", "new text"), reopened.get("peer").map { it.text ?: it.file!!.name })
        assertEquals("text2new text", ComposerDraftStore.messages(reopened.get("peer")).last().text)
        assertTrue(reopened.get("other").isEmpty())
    }
    @Test fun emptyTextIsOmittedAndAdjacentFilesStaySeparate() {
        val a = ComposerAttachment(UUID.randomUUID(), "a", "a.png", 1)
        val b = ComposerAttachment(UUID.randomUUID(), "b", "b.pdf", 2)
        val result = ComposerDraftStore.messages(listOf(ComposerPart(text = "  "), ComposerPart(a.id, file = a), ComposerPart(b.id, file = b), ComposerPart(text = " 🌍\n尾部  ")))
        assertEquals(3, result.size); assertEquals(" 🌍\n尾部  ", result.last().text)
    }
    @Test fun outOfOrderAcknowledgementDoesNotLoseDraft() {
        val root = Files.createTempDirectory("composer-ack").toFile(); val store = ComposerDraftStore(root)
        val part = ComposerPart(text = "unsent"); store.edit("peer", listOf(part)); store.take("peer")
        try { store.acknowledge("peer", UUID.randomUUID()); fail() } catch (_: IllegalStateException) { }
        store.restore("peer"); assertEquals("unsent", store.get("peer").single().text)
    }
}
