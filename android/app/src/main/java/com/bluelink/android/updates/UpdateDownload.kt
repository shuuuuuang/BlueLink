package com.bluelink.android.updates

import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.net.URL
import java.security.MessageDigest
import java.util.UUID
import javax.net.ssl.HttpsURLConnection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlin.coroutines.coroutineContext

internal enum class UpdateFailure { NETWORK, INTEGRITY, SIGNATURE, PACKAGE, VERSION, INSTALL }
internal class UpdateException(val reason: UpdateFailure) : Exception(reason.name)
internal data class DownloadedUpdate(val release: GitHubRelease, val file: File) {
    @Volatile var handedOff: Boolean = false
}

internal object UpdateDownload {
    private val hosts = setOf("github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com")
    internal fun allowed(url: URL): Boolean = url.protocol == "https" && url.host.lowercase() in hosts &&
        url.userInfo == null && url.port in setOf(-1, 443)
    suspend fun download(release: GitHubRelease, directory: File, progress: (Long, Long) -> Unit): DownloadedUpdate {
        var completed: File? = null
        try {
            return withContext(Dispatchers.IO) {
                val identity = ReleaseVersion.parse(release.tag) ?: throw UpdateException(UpdateFailure.INTEGRITY)
                val name = "BlueLink-${identity.base}-android-universal-release.apk"
                if (release.fileName != name || release.downloadUrl != "https://github.com/shuuuuuang/BlueLink/releases/download/${release.tag}/$name" ||
                    release.size !in 1..(512L * 1024 * 1024) || !Regex("[0-9a-fA-F]{64}").matches(release.sha256)) throw UpdateException(UpdateFailure.INTEGRITY)
                directory.mkdirs()
                val partial = File(directory, "bluelink-update-${UUID.randomUUID()}.part")
                val complete = File(directory, partial.name.removeSuffix(".part") + ".apk")
                var success = false
                try {
                    var url = URL(release.downloadUrl)
                    val deadline = System.nanoTime() + 600_000_000_000L
                    for (redirect in 0..5) {
                        ensureActive()
                        if (!allowed(url)) throw UpdateException(UpdateFailure.INTEGRITY)
                        val connection = url.openConnection() as HttpsURLConnection
                        try {
                            connection.instanceFollowRedirects = false
                            connection.connectTimeout = 15_000
                            connection.readTimeout = 15_000
                            connection.setRequestProperty("User-Agent", "BlueLink-Android-Updater")
                            val status = connection.responseCode
                            if (status in setOf(301, 302, 303, 307, 308)) {
                                url = URL(url, connection.getHeaderField("Location") ?: throw UpdateException(UpdateFailure.NETWORK))
                                continue
                            }
                            if (status != 200) throw UpdateException(UpdateFailure.NETWORK)
                            val length = connection.contentLengthLong
                            if (length >= 0 && length != release.size) throw UpdateException(UpdateFailure.INTEGRITY)
                            connection.inputStream.use { copy(it, partial, release.size, release.sha256, deadline, progress) }
                            ensureActive()
                            if (!partial.renameTo(complete)) throw UpdateException(UpdateFailure.INTEGRITY)
                            success = true
                            completed = complete
                            return@withContext DownloadedUpdate(release, complete)
                        } finally { connection.disconnect() }
                    }
                    throw UpdateException(UpdateFailure.NETWORK)
                } finally {
                    partial.delete()
                    if (!success) complete.delete()
                }
            }
        } catch (failure: Throwable) {
            // Cancellation may arrive while dispatching the completed result back to the caller.
            completed?.delete()
            throw failure
        }
    }

    internal suspend fun copy(input: InputStream, file: File, size: Long, sha256: String,
                              deadline: Long = Long.MAX_VALUE, progress: (Long, Long) -> Unit) {
        var received = 0L
        var reported = 0L
        val hash = MessageDigest.getInstance("SHA-256")
        try {
            FileOutputStream(file).use { output ->
                val buffer = ByteArray(65536)
                progress(0, size)
                while (true) {
                    coroutineContext.ensureActive()
                    if (System.nanoTime() > deadline) throw UpdateException(UpdateFailure.NETWORK)
                    val count = input.read(buffer)
                    if (count < 0) break
                    received += count
                    if (received > size) throw UpdateException(UpdateFailure.INTEGRITY)
                    hash.update(buffer, 0, count)
                    output.write(buffer, 0, count)
                    val now = System.nanoTime()
                    if (now - reported >= 150_000_000L) { progress(received, size); reported = now }
                }
                if (received != size || !hash.digest().joinToString("") { "%02x".format(it) }.equals(sha256, true))
                    throw UpdateException(UpdateFailure.INTEGRITY)
                output.fd.sync()
                progress(received, size)
            }
        } catch (failure: Throwable) { file.delete(); throw failure }
    }

    suspend fun verifyBytes(update: DownloadedUpdate) = withContext(Dispatchers.IO) {
        if (update.file.length() != update.release.size) throw UpdateException(UpdateFailure.INTEGRITY)
        val hash = MessageDigest.getInstance("SHA-256")
        update.file.inputStream().use { input ->
            val buffer = ByteArray(65536)
            while (true) {
                ensureActive()
                val count = input.read(buffer)
                if (count < 0) break
                hash.update(buffer, 0, count)
            }
        }
        if (!hash.digest().joinToString("") { "%02x".format(it) }.equals(update.release.sha256, true)) throw UpdateException(UpdateFailure.INTEGRITY)
    }
}
