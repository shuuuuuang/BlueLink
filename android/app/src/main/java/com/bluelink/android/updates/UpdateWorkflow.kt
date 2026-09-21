package com.bluelink.android.updates

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

internal enum class UpdatePhase { IDLE, CHECKING, CURRENT, AVAILABLE, DOWNLOADING, READY, VERIFYING, PERMISSION, HANDED_OFF, CHECK_FAILED, DOWNLOAD_FAILED, INSTALL_FAILED }
internal data class UpdateState(val visible: Boolean = false, val phase: UpdatePhase = UpdatePhase.IDLE,
    val release: GitHubRelease? = null, val downloaded: DownloadedUpdate? = null, val received: Long = 0,
    val total: Long = 0, val failure: UpdateFailure? = null) {
    val busy: Boolean get() = phase in setOf(UpdatePhase.CHECKING, UpdatePhase.DOWNLOADING, UpdatePhase.VERIFYING)
}

internal class UpdateWorkflow(private val backend: UpdateBackend, private val scope: CoroutineScope) {
    private val mutableState = MutableStateFlow(UpdateState())
    val state = mutableState.asStateFlow()
    @Volatile private var generation = 0
    private var job: Job? = null
    fun check() {
        if (state.value.busy) return
        if (state.value.phase != UpdatePhase.HANDED_OFF) state.value.downloaded?.takeUnless { it.handedOff }?.file?.delete()
        mutableState.value = UpdateState(visible = true)
        run(UpdatePhase.CHECKING) { attempt ->
            val release = backend.check()
            if (attempt == generation) mutableState.value = UpdateState(true, if (release == null) UpdatePhase.CURRENT else UpdatePhase.AVAILABLE, release)
        }
    }
    fun download() {
        if (state.value.phase !in setOf(UpdatePhase.AVAILABLE, UpdatePhase.DOWNLOAD_FAILED)) return
        val release = state.value.release ?: return
        run(UpdatePhase.DOWNLOADING) { attempt ->
            val result = backend.download(release) { received, total ->
                mutableState.update { if (attempt == generation) it.copy(received = received, total = total) else it }
            }
            if (attempt == generation) mutableState.update { it.copy(phase = UpdatePhase.READY, downloaded = result, received = release.size, total = release.size) }
            else result.file.delete()
        }
    }
    fun install() {
        if (state.value.phase !in setOf(UpdatePhase.READY, UpdatePhase.INSTALL_FAILED)) return
        val update = state.value.downloaded ?: return
        run(UpdatePhase.VERIFYING) { attempt ->
            val handedOff = backend.install(update)
            if (handedOff) update.handedOff = true
            if (attempt == generation) mutableState.update { it.copy(phase = if (handedOff) UpdatePhase.HANDED_OFF else UpdatePhase.PERMISSION) }
        }
    }
    fun requestPermission() {
        if (state.value.phase != UpdatePhase.PERMISSION) return
        try {
            backend.requestInstallPermission()
            mutableState.update { it.copy(phase = UpdatePhase.READY) }
        } catch (_: Exception) { mutableState.update { it.copy(phase = UpdatePhase.INSTALL_FAILED, failure = UpdateFailure.INSTALL) } }
    }
    private fun run(phase: UpdatePhase, action: suspend (Int) -> Unit) {
        val attempt = ++generation
        mutableState.update { it.copy(visible = true, phase = phase, failure = null, received = if (phase == UpdatePhase.DOWNLOADING) 0 else it.received) }
        job = scope.launch {
            try { action(attempt) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (failure: Exception) {
                if (attempt == generation) mutableState.update { it.copy(phase = when (phase) {
                    UpdatePhase.CHECKING -> UpdatePhase.CHECK_FAILED
                    UpdatePhase.DOWNLOADING -> UpdatePhase.DOWNLOAD_FAILED
                    else -> UpdatePhase.INSTALL_FAILED
                }, failure = (failure as? UpdateException)?.reason ?: if (phase == UpdatePhase.VERIFYING) UpdateFailure.INSTALL else UpdateFailure.NETWORK) }
            }
        }
    }
    fun close() {
        ++generation
        val previous = job
        val update = state.value.downloaded
        val alreadyHandedOff = state.value.phase == UpdatePhase.HANDED_OFF
        previous?.cancel()
        mutableState.value = UpdateState()
        scope.launch(kotlinx.coroutines.NonCancellable + kotlinx.coroutines.Dispatchers.IO) {
            previous?.join()
            if (!alreadyHandedOff && update?.handedOff == false) update.file.delete()
        }
    }
}
