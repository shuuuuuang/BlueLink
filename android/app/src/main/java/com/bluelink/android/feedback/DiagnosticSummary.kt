package com.bluelink.android.feedback

import com.bluelink.android.domain.DiagnosticEntry
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID

/** Never exports free-form logs, names, message content, addresses, or paths. */
object DiagnosticSummary {
    fun render(entries: List<DiagnosticEntry>) = buildString {
        appendLine("Structured event summary; message text and identifiers omitted.")
        entries.takeLast(300).forEach { appendLine("${it.timestamp} [${it.level}] ${safeComponent(it.component)}") }
    }

    fun create(directory: File, appVersion: String, androidApi: Int, entries: List<DiagnosticEntry>): File {
        check(directory.isDirectory || directory.mkdirs()) { "Cannot create diagnostic directory" }
        val target = File(directory, "BlueLink-diagnostics-${UUID.randomUUID()}.txt")
        val partial = File(directory, target.name + ".partial")
        try {
            partial.writeText("BlueLink: $appVersion\nAndroid API: $androidApi\n" + render(entries))
            Files.move(partial.toPath(), target.toPath(), StandardCopyOption.ATOMIC_MOVE)
            return target
        } catch (failure: Exception) { partial.delete(); throw failure }
    }

    private fun safeComponent(component: String): String = component.takeIf { it in setOf(
        "Application", "Runtime", "Bluetooth", "Discovery", "Connection", "Handshake", "Crypto",
        "Session", "Transfer", "Message", "Diagnostics", "Crash", "Storage") } ?: "Other"
}
