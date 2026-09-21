package com.bluelink.android.updates

import java.nio.file.Files
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class UpdateWorkflowTest {
    private val release = GitHubRelease("v0.2.17", "", "", "", 100, "a".repeat(64))
    @Test fun closeDiscardsLateCheckAndKeepsNewResult() = runBlocking {
        val late = CompletableDeferred<Unit>()
        var attempts = 0
        val backend = object : Stub() {
            override suspend fun check(): GitHubRelease? = if (++attempts == 1) withContext(NonCancellable) { late.await(); release } else null
        }
        val workflow = UpdateWorkflow(backend, this)
        workflow.check(); yield(); workflow.close(); workflow.check(); yield()
        assertEquals(UpdatePhase.CURRENT, workflow.state.value.phase)
        late.complete(Unit); yield(); yield()
        assertEquals(UpdatePhase.CURRENT, workflow.state.value.phase)
        workflow.close()
    }
    @Test fun retryDownloadPermissionAndInstallRemainExplicit() = runBlocking {
        val file = Files.createTempFile("bluelink-workflow", ".apk").toFile()
        var attempts = 0
        var permitted = false
        var installs = 0
        val backend = object : Stub() {
            override suspend fun check() = release
            override suspend fun download(release: GitHubRelease, progress: (Long, Long) -> Unit): DownloadedUpdate {
                if (++attempts == 1) throw UpdateException(UpdateFailure.INTEGRITY)
                progress(100,100); return DownloadedUpdate(release,file)
            }
            override suspend fun install(update: DownloadedUpdate): Boolean { installs++; return permitted }
            override fun requestInstallPermission() { permitted = true }
        }
        val workflow = UpdateWorkflow(backend, this)
        try {
            workflow.check(); yield(); workflow.download(); yield()
            assertEquals(UpdatePhase.DOWNLOAD_FAILED,workflow.state.value.phase)
            workflow.download(); yield()
            assertEquals(UpdatePhase.READY,workflow.state.value.phase)
            assertEquals(0,installs)
            workflow.install(); yield()
            assertEquals(UpdatePhase.PERMISSION,workflow.state.value.phase)
            workflow.requestPermission()
            assertEquals(1,installs)
            workflow.install(); yield()
            assertEquals(UpdatePhase.HANDED_OFF,workflow.state.value.phase)
            workflow.install(); yield()
            assertEquals(2, installs)
            workflow.close()
            assertTrue(file.exists())
        } finally { file.delete() }
    }
    @Test fun closeDuringInstallWaitsForReaderAndKeepsHandedOffPackage() = runBlocking {
        val file = Files.createTempFile("bluelink-install-close", ".apk").toFile()
        val entered = CompletableDeferred<Unit>(); val finish = CompletableDeferred<Unit>()
        val backend = object : Stub() {
            override suspend fun check() = release
            override suspend fun download(release: GitHubRelease, progress: (Long, Long) -> Unit) = DownloadedUpdate(release,file)
            override suspend fun install(update: DownloadedUpdate): Boolean = withContext(NonCancellable) {
                entered.complete(Unit); finish.await(); update.handedOff = true; true
            }
        }
        val workflow = UpdateWorkflow(backend,this)
        try {
            workflow.check(); yield(); workflow.download(); yield(); workflow.install(); entered.await()
            workflow.close(); yield()
            assertTrue(file.exists())
            finish.complete(Unit); yield(); yield()
            assertTrue(file.exists()); assertEquals(UpdatePhase.IDLE,workflow.state.value.phase)
        } finally { finish.complete(Unit); file.delete() }
    }
    private open class Stub : UpdateBackend {
        override suspend fun check(): GitHubRelease? = null
        override suspend fun download(release: GitHubRelease, progress: (Long, Long) -> Unit): DownloadedUpdate = error("not expected")
        override suspend fun install(update: DownloadedUpdate) = false
        override fun requestInstallPermission() {}
    }
}
