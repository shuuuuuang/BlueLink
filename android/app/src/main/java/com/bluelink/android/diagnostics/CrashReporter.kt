package com.bluelink.android.diagnostics

import android.app.ActivityManager
import android.app.Application
import android.content.Context
import android.os.Process
import java.io.File
import java.time.Instant

object CrashReporter {
    private const val FILE_NAME = "pending-crashes.log"
    private const val MAX_REPORT_BYTES = 128 * 1024L
    private const val PREFS_NAME = "bluelink_crash_reporter"
    private const val LAST_EXIT_TIMESTAMP = "last_exit_timestamp"
    private val lock = Any()
    @Volatile private var installed = false

    fun install(application: Application) {
        if (installed) return
        synchronized(lock) {
            if (installed) return
            val previous = Thread.getDefaultUncaughtExceptionHandler()
            Thread.setDefaultUncaughtExceptionHandler { thread, failure ->
                runCatching { append(application, "Fatal/${thread.name}", failure) }
                previous?.uncaughtException(thread, failure)
                    ?: Process.killProcess(Process.myPid())
            }
            installed = true
        }
    }

    fun recordNonFatal(context: Context, component: String, failure: Throwable) {
        runCatching { append(context, component, failure) }
    }

    fun drain(context: Context): List<String> {
        val values = synchronized(lock) {
            val file = reportFile(context)
            if (!file.isFile) emptyList() else runCatching { file.readLines() }.getOrDefault(emptyList())
                .also { runCatching { file.delete() } }
        }.toMutableList()
        previousExitSummary(context)?.let(values::add)
        return values
    }

    private fun append(context: Context, component: String, failure: Throwable) {
        val file = reportFile(context)
        file.parentFile?.mkdirs()
        val cause = generateSequence(failure) { it.cause }.last()
        val trace = cause.stackTrace.take(10).joinToString(" <- ") {
            "${it.className.substringAfterLast('.')}.${it.methodName}:${it.lineNumber}"
        }
        val line = "${Instant.now()}\t${sanitize(component)}\t${cause.javaClass.simpleName}\tdetails-redacted\t$trace"
        synchronized(lock) {
            if (file.length() >= MAX_REPORT_BYTES) file.writeText("")
            file.appendText(line + System.lineSeparator())
        }
    }

    private fun previousExitSummary(context: Context): String? {
        val activityManager = context.getSystemService(ActivityManager::class.java) ?: return null
        val exit = runCatching {
            activityManager.getHistoricalProcessExitReasons(context.packageName, 0, 1).firstOrNull()
        }.getOrNull() ?: return null
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        if (exit.timestamp <= prefs.getLong(LAST_EXIT_TIMESTAMP, 0L)) return null
        prefs.edit().putLong(LAST_EXIT_TIMESTAMP, exit.timestamp).apply()
        return "${Instant.ofEpochMilli(exit.timestamp)}\tProcessExit\treason=${exit.reason}\timportance=${exit.importance}\tstatus=${exit.status}"
    }

    private fun reportFile(context: Context): File =
        context.filesDir.resolve("diagnostics").resolve(FILE_NAME)

    private fun sanitize(value: String): String = value
        .replace(Regex("content://\\S+", RegexOption.IGNORE_CASE), "content://<redacted>")
        .replace(Regex("(?:[A-Za-z]:)?[/\\\\][^\\s]+"), "<path>")
        .replace('\t', ' ')
        .replace('\n', ' ')
        .take(800)
}
