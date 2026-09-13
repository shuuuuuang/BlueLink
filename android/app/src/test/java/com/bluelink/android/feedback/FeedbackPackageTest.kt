package com.bluelink.android.feedback

import com.bluelink.android.domain.DiagnosticEntry
import com.bluelink.android.domain.DiagnosticLevel
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.util.zip.ZipFile

class FeedbackPackageTest {
    @get:Rule val temporary = TemporaryFolder()

    @Test fun optOutNeverReadsOrWritesDiagnostics() {
        val file = FeedbackPackage.create(temporary.root, FeedbackRequest(FeedbackType.CONNECTION, "无法连接"), "0.2.17", 36) {
            error("Diagnostic supplier must not be read")
        }
        ZipFile(file).use { zip ->
            assertNull(zip.getEntry("diagnostics.txt"))
            assertTrue(zip.getInputStream(zip.getEntry("feedback.txt")).reader(Charsets.UTF_8).readText().contains("无法连接"))
        }
    }
    @Test fun optInOmitsPrivateMessageAndUnrecognizedComponent() {
        val file = FeedbackPackage.create(temporary.root, FeedbackRequest(FeedbackType.FILES, "传输中断", true), "0.2.17", 36) {
            listOf(DiagnosticEntry(1, level = DiagnosticLevel.ERROR, component = "device-secret-name", message = "secret-filename secret-code"))
        }
        ZipFile(file).use { zip ->
            val content = zip.getInputStream(zip.getEntry("diagnostics.txt")).reader().readText()
            assertTrue(content.contains("ERROR")); assertTrue(content.contains("Other"))
            assertFalse(content.contains("secret"))
        }
    }
    @Test fun invalidDescriptionCreatesNoFiles() {
        listOf("   ", "x".repeat(1001)).forEach { text ->
            assertThrows(IllegalArgumentException::class.java) {
                FeedbackPackage.create(temporary.root, FeedbackRequest(FeedbackType.OTHER, text), "v", 36) { emptyList() }
            }
        }
        assertTrue(temporary.root.listFiles().orEmpty().isEmpty())
    }
    @Test fun repeatedGenerationKeepsIndependentFiles() {
        val request = FeedbackRequest(FeedbackType.MESSAGES, "相同描述")
        val first = FeedbackPackage.create(temporary.root, request, "v", 36) { emptyList() }
        val second = FeedbackPackage.create(temporary.root, request, "v", 36) { emptyList() }
        assertNotEquals(first, second); assertTrue(first.isFile); assertTrue(second.isFile)
    }
}
