package com.bluelink.android.composer

import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files
import java.util.UUID

class ComposerAssociationTest {
    @Test fun bothDraftsAndAttachmentPositionsSurviveReplayAfterDatabaseRollback() {
        val root = Files.createTempDirectory("composer-association").toFile(); val store = ComposerDraftStore(root)
        val file = ComposerAttachment(UUID.randomUUID(), "original-path", "report.pdf", 12)
        store.edit("OLD", listOf(ComposerPart(text = "before"), ComposerPart(file.id, file = file), ComposerPart(text = "after")))
        val legacy = mapOf("old" to "beforeafter", "new" to "target draft")
        val first = store.associate(mapOf("OLD" to "NEW"), legacy)
        assertEquals("target draft\nbeforeafter", first["new"])
        assertEquals(listOf("target draft", "\n", "before", "report.pdf", "after"), store.get("new").map { it.text ?: it.file!!.name })
        val reopened = ComposerDraftStore(root)
        assertEquals(first, reopened.associate(mapOf("old" to "new"), legacy))
        assertEquals(1, reopened.get("new").count { it.file != null }); assertTrue(reopened.get("old").isEmpty())
        assertEquals("original-path", reopened.get("new").single { it.file != null }.file!!.path)
    }
    @Test fun chainsEmptyTombstonesAndUnrelatedDraftsArePreserved() {
        val store = ComposerDraftStore(Files.createTempDirectory("composer-chain").toFile())
        store.edit("a", listOf(ComposerPart(text = "A"))); store.edit("b", listOf(ComposerPart(text = "B")))
        store.edit("cleared", emptyList()); store.edit("other", listOf(ComposerPart(text = "OTHER")))
        val aliases = mapOf("a" to "b", "b" to "c", "cleared" to "c")
        assertEquals("C\nA\nB", store.associate(aliases, mapOf("c" to "C", "cleared" to "must not revive"))["c"])
        assertEquals("OTHER", store.get("other").single().text)
        assertEquals("C\nA\nB", store.associate(aliases, emptyMap())["c"])
    }
    @Test fun failedCyclesAndActiveWorkDoNotPartiallyMoveDrafts() {
        val root = Files.createTempDirectory("composer-blocked").toFile(); val store = ComposerDraftStore(root)
        store.edit("a", listOf(ComposerPart(text = "A"))); store.setHeight(220f)
        val before = java.io.File(root, "drafts.json").readText()
        assertThrows(IllegalStateException::class.java) { store.associate(mapOf("a" to "b", "b" to "a"), emptyMap()) }
        assertEquals(before, java.io.File(root, "drafts.json").readText())
        store.take("a")
        assertThrows(IllegalStateException::class.java) { store.associate(mapOf("a" to "b"), emptyMap()) }
        store.restore("a"); store.associate(mapOf("a" to "b"), emptyMap())
        assertEquals(220f, ComposerDraftStore(root).height())
    }
    @Test fun combiningFullDraftsNeverDropsFilesAndStillAllowsEditingOrRemoval() {
        val store = ComposerDraftStore(Files.createTempDirectory("composer-full").toFile())
        fun files() = (1..100).map { val f = ComposerAttachment(UUID.randomUUID(), "path-$it", "file-$it", 1); ComposerPart(f.id, file = f) }
        store.edit("a", files()); store.edit("b", files()); store.associate(mapOf("a" to "b"), emptyMap())
        val combined = store.get("b"); assertEquals(200, combined.count { it.file != null })
        store.edit("b", combined + ComposerPart(text = "continued")); store.edit("b", store.get("b").drop(1))
        assertEquals(199, store.get("b").count { it.file != null })
        assertThrows(IllegalArgumentException::class.java) { store.edit("b", store.get("b") + files().first()) }
    }
}
