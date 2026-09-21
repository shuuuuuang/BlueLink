package com.bluelink.android.updates

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import androidx.core.content.FileProvider
import java.io.File
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlin.coroutines.coroutineContext

internal interface UpdateBackend {
    suspend fun check(): GitHubRelease?
    suspend fun download(release: GitHubRelease, progress: (Long, Long) -> Unit): DownloadedUpdate
    suspend fun install(update: DownloadedUpdate): Boolean
    fun requestInstallPermission()
}

internal class AndroidUpdateBackend(private val context: Context) : UpdateBackend {
    override suspend fun check() = GitHubUpdateService.check()
    override suspend fun download(release: GitHubRelease, progress: (Long, Long) -> Unit): DownloadedUpdate {
        val update = UpdateDownload.download(release, File(context.cacheDir, "updates"), progress)
        try { verify(update); return update }
        catch (failure: Throwable) { update.file.delete(); throw failure }
    }
    internal suspend fun verify(update: DownloadedUpdate) = withContext(Dispatchers.IO) {
        val root = File(context.cacheDir, "updates").canonicalFile
        if (update.file.canonicalFile.parentFile != root || update.file.extension != "apk") throw UpdateException(UpdateFailure.PACKAGE)
        UpdateDownload.verifyBytes(update)
        val pm = context.packageManager
        val flags = PackageManager.PackageInfoFlags.of(PackageManager.GET_SIGNING_CERTIFICATES.toLong())
        val candidate = pm.getPackageArchiveInfo(update.file.path, flags) ?: throw UpdateException(UpdateFailure.PACKAGE)
        val installed = pm.getPackageInfo(context.packageName, flags)
        if (candidate.packageName != context.packageName) throw UpdateException(UpdateFailure.PACKAGE)
        val base = ReleaseVersion.parse(update.release.tag)?.base ?: throw UpdateException(UpdateFailure.VERSION)
        if (candidate.versionName != base || candidate.longVersionCode < installed.longVersionCode) throw UpdateException(UpdateFailure.VERSION)
        val expected = installed.signingInfo?.apkContentsSigners?.map { it.toCharsString() }?.toSet()
        val actual = candidate.signingInfo?.apkContentsSigners?.map { it.toCharsString() }?.toSet()
        if (expected.isNullOrEmpty() || actual != expected) throw UpdateException(UpdateFailure.SIGNATURE)
    }
    override suspend fun install(update: DownloadedUpdate): Boolean {
        verify(update)
        coroutineContext.ensureActive()
        if (!context.packageManager.canRequestPackageInstalls()) return false
        val uri = FileProvider.getUriForFile(context, context.packageName + ".files", update.file)
        update.handedOff = true
        try {
            context.startActivity(Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "application/vnd.android.package-archive")
                clipData = ClipData.newRawUri("BlueLink update", uri)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            })
        } catch (failure: Throwable) { update.handedOff = false; throw failure }
        return true
    }
    override fun requestInstallPermission() {
        context.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + context.packageName)))
    }
}
