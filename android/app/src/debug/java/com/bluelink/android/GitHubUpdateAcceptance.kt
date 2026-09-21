package com.bluelink.android

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.android.ui.settings.GitHubUpdateRow
import com.bluelink.android.updates.GitHubRelease
import com.bluelink.android.updates.GitHubUpdateService
import com.bluelink.android.updates.ReleaseVersion
import com.bluelink.android.updates.*
import kotlinx.coroutines.Dispatchers
import java.security.MessageDigest
import java.io.FilterInputStream
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.io.IOException

/** Debug-only scenarios render the production row and dialog, with no user-history/settings writes. */
@Composable
internal fun GitHubUpdateAcceptance(scene: String, close: () -> Unit) {
    val context = LocalContext.current
    val check: suspend () -> GitHubRelease? = remember(scene) {
        var attempts = 0
        val fixture = GitHubRelease("v0.2.17-preview.3", List(60) { "Release notes / 更新說明 / 更新说明：第 ${it + 1} 项。" }.joinToString("\n"),
            "BlueLink-0.2.17-android-universal-release.apk", "https://github.com/shuuuuuang/BlueLink/releases/download/v0.2.17-preview.3/BlueLink-0.2.17-android-universal-release.apk", 1024, "a".repeat(64))
        val operation: suspend () -> GitHubRelease? = {
            val attempt = ++attempts
            android.util.Log.i("BlueLinkUpdateQA", "START $scene attempt=$attempt")
            val result = when (scene) {
                "github-update-notes" -> fixture
                "github-update-install", "github-update-download-cancel" -> withContext(Dispatchers.IO) {
                    val payload = File(context.cacheDir, "acceptance/install-candidate.apk")
                    val digest = MessageDigest.getInstance("SHA-256")
                    payload.inputStream().use { input -> val bytes = ByteArray(65536); while (true) { val n = input.read(bytes); if (n < 0) break; digest.update(bytes, 0, n) } }
                    fixture.copy(tag = BuildConfig.RELEASE_TAG, notes = "Same-signature local QA package. Tests the production verifier and system installer.",
                        size = payload.length(), sha256 = digest.digest().joinToString("") { "%02x".format(it) })
                }
                "github-update-cancel-late" -> if (attempt == 1) withContext(NonCancellable) { delay(20_000); fixture }
                    else { delay(300); null }
                "github-update-fail-retry" -> if (attempt == 1) { delay(300); throw IOException("Controlled QA failure") }
                    else GitHubUpdateService.check(ReleaseVersion(0, 0, 0, null))
                "github-update-live-old" -> GitHubUpdateService.check(ReleaseVersion(0, 0, 0, null))
                else -> GitHubUpdateService.check()
            }
            android.util.Log.i("BlueLinkUpdateQA", "FINISH $scene attempt=$attempt release=${result?.tag ?: "CURRENT"}")
            if (scene in setOf("github-update-live-old", "github-update-live-current")) {
                File(context.cacheDir, "acceptance/$scene.json").apply { parentFile?.mkdirs() }.writeText(
                    JSONObject().put("scenario", scene).put("installedTag", BuildConfig.RELEASE_TAG)
                        .put("release", result?.tag ?: "CURRENT").put("page", result?.pageUrl ?: "")
                        .put("realNetwork", true).toString())
            }
            result
        }
        operation
    }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().padding(16.dp)) {
        Text("BlueLink update QA: $scene")
        val backend = remember(check, context) {
            val production = AndroidUpdateBackend(context)
            object : UpdateBackend by production {
                override suspend fun check() = check.invoke()
                override suspend fun download(release: GitHubRelease, progress: (Long, Long) -> Unit): DownloadedUpdate {
                    if (scene !in setOf("github-update-install", "github-update-download-cancel")) return production.download(release, progress)
                    return withContext(Dispatchers.IO) {
                        val file = File(context.cacheDir, "updates/bluelink-update-local-qa.apk").apply { parentFile?.mkdirs() }
                        try {
                            val payload = File(context.cacheDir, "acceptance/install-candidate.apk")
                            val input = object : FilterInputStream(payload.inputStream()) {
                                override fun read(bytes: ByteArray, offset: Int, length: Int): Int {
                                    if (scene == "github-update-download-cancel") Thread.sleep(15)
                                    return super.read(bytes, offset, length)
                                }
                            }
                            input.use { UpdateDownload.copy(it, file, release.size, release.sha256, progress = progress) }
                            DownloadedUpdate(release, file).also { production.verify(it) }
                        } catch (failure: Throwable) { file.delete(); throw failure }
                    }
                }
            }
        }
        GitHubUpdateRow(backend)
        TextButton(onClick = close) { Text("QA Close") }
    }
}
