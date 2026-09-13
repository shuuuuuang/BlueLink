package com.bluelink.android.feedback

import com.bluelink.android.domain.DiagnosticEntry
import com.bluelink.android.domain.DiagnosticLevel
import org.junit.Assert.*
import org.junit.Test
import org.junit.Rule
import org.junit.rules.TemporaryFolder

class DiagnosticSummaryTest {
    @get:Rule val temporary = TemporaryFolder()
    @Test fun exportedFileExcludesNamesMessagesAndUnknownComponentText() {
        val entries = listOf(DiagnosticEntry(sequence = 1, level = DiagnosticLevel.ERROR,
            component = "secret-device-name", message = "private-message /private/document.pdf AA:BB:CC"))
        val file = DiagnosticSummary.create(temporary.newFolder(), "test-version", 36, entries)
        val content = file.readText()
        assertTrue(content.contains("Android API: 36"))
        assertTrue(content.contains("[ERROR] Other"))
        listOf("secret-device-name", "private-message", "document.pdf", "AA:BB:CC").forEach { assertFalse(content.contains(it)) }
        assertEquals(listOf(file.name), requireNotNull(file.parentFile).list()!!.toList())
    }
}
