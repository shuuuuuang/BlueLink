package com.bluelink.android.updates

import com.bluelink.android.BuildConfig
import java.io.ByteArrayOutputStream
import java.net.URL
import javax.net.ssl.HttpsURLConnection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import org.json.JSONArray

internal data class GitHubRelease(val tag: String, val notes: String, val fileName: String, val downloadUrl: String, val size: Long, val sha256: String) {
    val pageUrl: String get() = "https://github.com/shuuuuuang/BlueLink/releases/tag/$tag"
}

/** User-triggered public metadata GET. Verified assets feed the in-app update workflow. */
internal object GitHubUpdateService {
    private const val REPOSITORY = "shuuuuuang/BlueLink"
    private const val MAX_FEED_BYTES = 2 * 1024 * 1024
    suspend fun check(current: ReleaseVersion = requireNotNull(ReleaseVersion.parse(BuildConfig.RELEASE_TAG))): GitHubRelease? = withContext(Dispatchers.IO) {
        val connection = URL("https://api.github.com/repos/$REPOSITORY/releases?per_page=100").openConnection() as HttpsURLConnection
        try {
            connection.instanceFollowRedirects = false
            connection.connectTimeout = 10_000
            connection.readTimeout = 10_000
            connection.setRequestProperty("Accept", "application/vnd.github+json")
            connection.setRequestProperty("X-GitHub-Api-Version", "2022-11-28")
            connection.setRequestProperty("User-Agent", "BlueLink-Android/" + BuildConfig.VERSION_NAME)
            val deadline = System.nanoTime() + 25_000_000_000L
            check(connection.responseCode == 200) { "GitHub HTTP ${connection.responseCode}" }
            val bytes = ByteArrayOutputStream()
            connection.inputStream.use { input ->
                val buffer = ByteArray(8192)
                while (true) {
                    ensureActive()
                    check(System.nanoTime() < deadline) { "GitHub request timed out" }
                    val count = input.read(buffer)
                    if (count < 0) break
                    check(bytes.size() + count <= MAX_FEED_BYTES) { "Release feed exceeds limit" }
                    bytes.write(buffer, 0, count)
                }
            }
            ensureActive()
            parse(bytes.toString(Charsets.UTF_8.name()), current)
        } finally {
            connection.disconnect()
        }
    }

    internal fun parse(json: String, current: ReleaseVersion): GitHubRelease? {
        val feed = JSONArray(json)
        require(feed.length() <= 100)
        val candidates = (0 until feed.length()).mapNotNull { index ->
            val release = feed.getJSONObject(index)
            if (release.getBoolean("draft")) return@mapNotNull null
            val identity = ReleaseVersion.parse(release.getString("tag_name")) ?: return@mapNotNull null
            if (release.getBoolean("prerelease") != (identity.preview != null)) return@mapNotNull null
            identity to release
        }
        val (identity, release) = candidates.maxByOrNull { it.first } ?: return null
        if (identity <= current) return null
        val tag = release.getString("tag_name")
        val expectedName = "BlueLink-${identity.base}-android-universal-release.apk"
        val assets = release.getJSONArray("assets")
        val matches = (0 until assets.length()).map { assets.getJSONObject(it) }.filter { it.getString("name") == expectedName }
        require(matches.size == 1) { "No unique universal release APK" }
        val asset = matches.single()
        require(asset.getLong("size") in 1..(512L * 1024 * 1024))
        require(Regex("sha256:[0-9a-fA-F]{64}").matches(asset.getString("digest")))
        require(asset.getString("browser_download_url") == "https://github.com/$REPOSITORY/releases/download/$tag/$expectedName")
        return GitHubRelease(tag, release.optString("body", "").replace("\u0000", "").take(4000), expectedName,
            asset.getString("browser_download_url"), asset.getLong("size"), asset.getString("digest").removePrefix("sha256:"))
    }
}
