package com.bluelink.android.composer

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import android.view.ContentInfo
import android.widget.Toast
import com.bluelink.android.R
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import java.io.File
import java.util.UUID

class ComposerController(private val context: Context, root: File = File(context.filesDir, "composer"),
    private val submit: suspend (String, ComposerPart) -> Boolean = { _, _ -> false }) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val store = ComposerDraftStore(root)
    init {
        store.snapshot().forEach { (peer, parts) ->
            if (parts.any { (it.file?.size ?: 0) < 0 }) store.edit(peer, parts.map { part ->
                if (part.file != null && part.file.size < 0) part.copy(file = part.file.copy(name = context.getString(R.string.composer_incomplete), size = -2)) else part
            })
        }
    }
    private val unsaved = mutableMapOf<String, List<ComposerPart>>()
    private val mutableHeight = MutableStateFlow(store.height()); val height = mutableHeight.asStateFlow()
    private val mutableDrafts = MutableStateFlow(store.snapshot()); val drafts = mutableDrafts.asStateFlow()
    private val mutableSending = MutableStateFlow<Set<String>>(emptySet()); val sending = mutableSending.asStateFlow()
    private val mutablePreparing = MutableStateFlow<Map<String, Int>>(emptyMap()); val preparing = mutablePreparing.asStateFlow()
    private val insertions = mutableMapOf<String, (List<ComposerPart>) -> Unit>()
    fun associate(aliases: Map<String, String>, legacy: Map<String, String>): Map<String, String> {
        check(mutableSending.value.isEmpty() && mutablePreparing.value.values.none { it > 0 }) { context.getString(R.string.composer_identity_busy) }
        for ((peer, parts) in unsaved.toMap()) { store.edit(peer, parts); unsaved.remove(peer) }
        return store.associate(aliases, legacy).also { insertions.clear(); refresh() }
    }
    internal fun collectStartupOrphans(referenced: Set<String>) = store.collectStartupOrphans(referenced)
    fun clear(peer: String? = null) { store.clear(peer); if (peer == null) unsaved.clear() else unsaved.remove(peer.lowercase(java.util.Locale.ROOT)); refresh() }
    fun bind(peer: String, insert: (List<ComposerPart>) -> Unit) { insertions[peer] = insert }
    fun unbind(peer: String) { insertions.remove(peer) }
    fun initialize(peer: String, legacy: String) { if (!store.contains(peer)) edit(peer, if (legacy.isEmpty()) emptyList() else listOf(ComposerPart(text = legacy))) }
    fun edit(peer: String, parts: List<ComposerPart>) {
        val key = peer.lowercase(java.util.Locale.ROOT); unsaved[key] = parts; refresh()
        try { store.edit(peer, parts); unsaved.remove(key); refresh() } catch (_: Exception) { notice(R.string.composer_save_failed) }
    }
    private fun current(peer: String) = unsaved[peer.lowercase(java.util.Locale.ROOT)] ?: store.get(peer)
    fun resize(value: Float, persist: Boolean) { mutableHeight.value = ComposerDraftStore.clampHeight(value); if (persist) runCatching { store.setHeight(value) }.onFailure { notice(R.string.composer_save_failed) } }
    fun receive(peer: String, payload: ContentInfo) {
        val uris = (0 until payload.clip.itemCount).mapNotNull { payload.clip.getItemAt(it).uri }
        capture(peer, uris, payload)
    }
    fun picked(peer: String, uris: List<Uri>) = capture(peer, uris, null)
    private fun capture(peer: String, uris: List<Uri>, permission: ContentInfo?) {
        if (uris.isEmpty()) return
        if (uris.size + current(peer).count { it.file != null } > ComposerDraftStore.MAX_PENDING) { notice(R.string.composer_limit); return }
        val placeholders = uris.map { val id = UUID.randomUUID(); ComposerPart(id, file = ComposerAttachment(id, store.newFile(id).absolutePath, context.getString(R.string.composer_adding), -1)) }
        mutablePreparing.update { it + (peer to ((it[peer] ?: 0) + 1)) }
        insertions[peer]?.invoke(placeholders) ?: edit(peer, current(peer) + placeholders)
        scope.launch {
            try {
                for ((index, uri) in uris.withIndex()) {
                    val placeholder = placeholders[index].file!!
                    val ready = withContext(Dispatchers.IO) { captureOne(uri, placeholder) }
                    val current = current(peer)
                    if (current.any { it.file?.id == ready.id }) edit(peer, current.map { if (it.file?.id == ready.id) it.copy(file = ready) else it })
                    else store.deleteUnsubmitted(ready)
                }
                check(permission == null || permission.clip.itemCount >= uris.size) // Retain temporary URI grants until copies finish.
            } catch (error: Exception) {
                val ids = placeholders.map { it.id }.toSet()
                edit(peer, current(peer).filterNot { it.id in ids && it.file?.size == -1L })
                if (error !is CancellationException) notice(R.string.composer_add_failed)
            } finally { mutablePreparing.update { it + (peer to ((it[peer] ?: 1) - 1)) } }
        }
    }
    private suspend fun captureOne(uri: Uri, placeholder: ComposerAttachment): ComposerAttachment {
        require(uri.scheme in setOf("content", "file"))
        var name = if (uri.scheme == "file") File(uri.path!!).name else "Attachment"
        if (uri.scheme == "content") context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst()) name = c.getString(0)?.takeIf { it.isNotBlank() }?.take(512) ?: name }
        val mime = context.contentResolver.getType(uri) ?: android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"
        val file = File(placeholder.path)
        com.bluelink.android.files.OwnedTemporaryFiles.register(file,placeholder.id)
        try {
            var total = 0L
            checkNotNull(context.contentResolver.openInputStream(uri)).use { input -> file.outputStream().use { output ->
                val buffer = ByteArray(128 * 1024)
                while (true) { currentCoroutineContext().ensureActive(); val read = input.read(buffer); if (read < 0) break; total += read; require(total <= 2L * 1024 * 1024 * 1024); output.write(buffer, 0, read) }
            } }
            return placeholder.copy(name = name, size = total, mime = mime)
        } catch (error: Throwable) { file.delete(); throw error }
        finally { com.bluelink.android.files.OwnedTemporaryFiles.release(file) }
    }
    fun send(peer: String, clearLegacy: () -> Unit = {}) {
        if (peer in mutableSending.value || (mutablePreparing.value[peer] ?: 0) > 0 || ComposerDraftStore.messages(current(peer)).isEmpty() || current(peer).any { (it.file?.size ?: 0) < 0 }) return
        mutableSending.update { it + peer }
        scope.launch {
            try {
                val key = peer.lowercase(java.util.Locale.ROOT)
                unsaved[key]?.let { store.edit(peer, it); unsaved.remove(key) }
                val parts = store.take(peer); refresh(); clearLegacy()
                for (part in parts) { check(submit(peer, part)); store.acknowledge(peer, part.id) }
            } catch (error: Exception) { if (error !is CancellationException) notice(R.string.composer_send_failed) }
            finally { runCatching { store.restore(peer); refresh() }.onFailure { notice(R.string.composer_save_failed) }; mutableSending.update { it - peer } }
        }
    }
    private fun refresh() { mutableDrafts.value = store.snapshot() + unsaved }
    private fun notice(text: Int) = Toast.makeText(context, context.getString(text), Toast.LENGTH_SHORT).show()
    fun close() { scope.cancel() }
}
