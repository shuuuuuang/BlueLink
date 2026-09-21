package com.bluelink.android.updates

import java.io.ByteArrayInputStream
import java.io.File
import java.net.URL
import java.nio.file.Files
import java.security.MessageDigest
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class UpdateDownloadTest {
    private val bytes = ByteArray(100000) { it.toByte() }
    private val digest = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
    @Test fun restrictsRedirectHostsAndTransport() {
        listOf("https://github.com/test", "https://release-assets.githubusercontent.com/test", "https://objects.githubusercontent.com/test").forEach { assertTrue(UpdateDownload.allowed(URL(it))) }
        listOf("http://github.com/test", "https://github.com.evil.example/test", "https://user@github.com/test", "https://github.com:444/test").forEach { assertFalse(UpdateDownload.allowed(URL(it))) }
    }
    @Test fun copiesExactBytesAndReportsProgress() = runBlocking {
        val file = Files.createTempFile("bluelink-download", ".part").toFile()
        try {
            var final = 0L
            UpdateDownload.copy(ByteArrayInputStream(bytes), file, bytes.size.toLong(), digest) { received, _ -> final = received }
            assertArrayEquals(bytes, file.readBytes())
            assertEquals(bytes.size.toLong(), final)
        } finally { file.delete() }
    }
    @Test fun refusesTruncationOverrunAndTamperingAndRemovesPartial() = runBlocking {
        for ((size, hash) in listOf(bytes.size + 1 to digest, bytes.size - 1 to digest, bytes.size to "0".repeat(64))) {
            val file = Files.createTempFile("bluelink-bad", ".part").toFile()
            try { UpdateDownload.copy(ByteArrayInputStream(bytes), file, size.toLong(), hash) { _, _ -> }; fail("invalid package accepted") }
            catch (failure: UpdateException) { assertEquals(UpdateFailure.INTEGRITY, failure.reason) }
            assertFalse(file.exists())
        }
    }
    @Test fun cancellationCleansPartial() = runBlocking {
        val file = Files.createTempFile("bluelink-cancel", ".part").toFile()
        val task = launch(start = CoroutineStart.LAZY) {
            UpdateDownload.copy(ByteArrayInputStream(bytes), file, bytes.size.toLong(), digest) { received, _ -> if (received > 0) cancel() }
        }
        task.start(); task.join()
        assertTrue(task.isCancelled)
        assertFalse(file.exists())
    }
    @Test fun verificationRejectsChangedPackageBeforeInstall() = runBlocking {
        val file = Files.createTempFile("bluelink-verify", ".apk").toFile()
        try {
            file.writeBytes(bytes)
            val release = GitHubRelease("v0.2.17", "", "", "", bytes.size.toLong(), digest)
            val update = DownloadedUpdate(release, file)
            UpdateDownload.verifyBytes(update)
            file.writeBytes(ByteArray(bytes.size))
            try { UpdateDownload.verifyBytes(update); fail("changed package accepted") }
            catch (failure: UpdateException) { assertEquals(UpdateFailure.INTEGRITY, failure.reason) }
        } finally { file.delete() }
    }
}
