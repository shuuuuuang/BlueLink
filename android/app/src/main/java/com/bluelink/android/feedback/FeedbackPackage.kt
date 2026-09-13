package com.bluelink.android.feedback

import com.bluelink.android.domain.DiagnosticEntry
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.UUID
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

enum class FeedbackType { CONNECTION, MESSAGES, FILES, OTHER }

data class FeedbackRequest(val type: FeedbackType, val description: String, val includeDiagnostics: Boolean = false) {
    val isValid: Boolean get() = description.isNotBlank() && description.length <= 1000
}

/** Local export only. The diagnostic supplier is never evaluated without consent. */
object FeedbackPackage {
    fun create(directory: File, request: FeedbackRequest, appVersion: String, androidApi: Int,
               diagnostics: () -> List<DiagnosticEntry>): File {
        require(request.isValid) { "Feedback description must contain 1–1000 characters" }
        check(directory.isDirectory || directory.mkdirs()) { "Cannot create feedback directory" }
        val stamp = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss").withZone(ZoneOffset.UTC).format(Instant.now())
        val target = File(directory, "BlueLink-feedback-$stamp-${UUID.randomUUID().toString().take(8)}.zip")
        val partial = File(directory, target.name + ".partial")
        try {
            ZipOutputStream(partial.outputStream()).use { zip ->
                fun entry(name: String, text: String) {
                    zip.putNextEntry(ZipEntry(name))
                    zip.write(text.toByteArray(Charsets.UTF_8)); zip.closeEntry()
                }
                entry("feedback.txt", "Type: ${request.type.name}\nDescription:\n${request.description.trim()}\n")
                entry("environment.txt", "BlueLink: $appVersion\nAndroid API: $androidApi\nDiagnostics included: ${request.includeDiagnostics}\n")
                if (request.includeDiagnostics) {
                    entry("diagnostics.txt", DiagnosticSummary.render(diagnostics()))
                }
            }
            Files.move(partial.toPath(), target.toPath(), StandardCopyOption.ATOMIC_MOVE)
            return target
        } catch (failure: Exception) {
            partial.delete() // Only this invocation's incomplete cache file.
            throw failure
        }
    }

}
