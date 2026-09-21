package com.bluelink.android.data.local

import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject

internal data class PeerPreference(val note: String="",val pinned: Boolean=false)
/** Keyed only by the exact verified identity. No alias/trust side effects. */
internal class PeerPreferences(private val path: File) {
    private fun read(): Map<String,PeerPreference> {
        if (!path.exists()) return emptyMap()
        val data=JSONObject(path.readText())
        return data.keys().asSequence().associate { key -> val row=data.getJSONObject(key)
            key.lowercase(java.util.Locale.ROOT) to PeerPreference(row.getString("note"),row.getBoolean("pinned")) }
    }
    private val initial=runCatching { read() }
    private val mutable=MutableStateFlow(initial.getOrDefault(emptyMap())); val state=mutable.asStateFlow()
    @Synchronized fun update(peer: String,note: String?=null,pinned: Boolean?=null) {
        initial.exceptionOrNull()?.let { throw java.io.IOException("Peer preferences unavailable",it) }
        require(peer.isNotBlank()); val key=peer.lowercase(java.util.Locale.ROOT)
        val old=mutable.value[key] ?: PeerPreference(); val text=note?.trim() ?: old.note
        require(text.length<=64 && text.none { it.isISOControl() })
        val rows=mutable.value + (key to PeerPreference(text,pinned ?: old.pinned)); val data=JSONObject()
        rows.forEach { (id,value) -> data.put(id,JSONObject().put("note",value.note).put("pinned",value.pinned)) }
        path.parentFile!!.mkdirs(); val temporary=File(path.path+".new")
        temporary.outputStream().use { it.write(data.toString().toByteArray()); it.fd.sync() }
        Files.move(temporary.toPath(),path.toPath(),StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING)
        mutable.value=rows
    }
}
