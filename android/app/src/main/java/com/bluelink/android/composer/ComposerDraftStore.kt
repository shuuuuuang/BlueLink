package com.bluelink.android.composer

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID

data class ComposerAttachment(val id: UUID, val path: String, val name: String, val size: Long, val mime: String = "application/octet-stream")
data class ComposerPart(val id: UUID = UUID.randomUUID(), val text: String? = null, val file: ComposerAttachment? = null)

class ComposerDraftStore(private val root: File) {
    private val manifest = File(root, "drafts.json")
    private var editedThisProcess = false
    private var data = if (manifest.exists()) JSONObject(manifest.readText()) else JSONObject().put("height", 68).put("drafts", JSONObject()).put("sending", JSONObject())
    init {
        if (!data.has("sending")) data.put("sending", JSONObject())
        data.getJSONObject("sending").keys().asSequence().toList().forEach(::restore)
    }
    companion object {
        const val MAX_PENDING = 100
        fun clampHeight(value: Float, available: Float = 1000f): Float = (if (value.isFinite()) value else 68f).coerceIn(68f, (available - 180f).coerceIn(68f, 280f))
        fun messages(parts: List<ComposerPart>): List<ComposerPart> {
            val result = mutableListOf<ComposerPart>()
            for (part in parts) {
                if (part.file == null && result.lastOrNull()?.file == null && result.isNotEmpty()) {
                    val previous = result.removeAt(result.lastIndex); result.add(previous.copy(text = previous.text.orEmpty() + part.text.orEmpty()))
                } else result.add(part)
            }
            return result.filter { it.file != null || !it.text.isNullOrBlank() }
        }
    }
    @Synchronized fun associate(aliases: Map<String, String>, legacy: Map<String, String>): Map<String, String> {
        fun key(value: String) = value.lowercase(java.util.Locale.ROOT)
        val map = aliases.mapKeys { key(it.key) }.mapValues { key(it.value) }
        val targets = map.keys.sorted().associateWith { old ->
            var target = old; val seen = mutableSetOf<String>()
            while (target in map) { check(seen.add(target)) { "设备身份关联存在循环" }; target = map.getValue(target) }
            target
        }
        if (targets.isEmpty()) return emptyMap()
        val affected = targets.keys + targets.values
        check(affected.none { data.getJSONObject("sending").has(it) || get(it).any { p -> p.file?.size == -1L } }) {
            "附件仍在准备或发送，请结束后重试身份关联"
        }
        val next = JSONObject(data.toString()); val drafts = next.getJSONObject("drafts")
        for ((peer, text) in legacy) if (key(peer) in affected && !drafts.has(key(peer)))
            drafts.put(key(peer), encode(if (text.isEmpty()) emptyList() else listOf(ComposerPart(text = text))))
        for ((old, target) in targets) {
            if (!drafts.has(old)) continue
            val source = decode(drafts.optJSONArray(old)); val destination = decode(drafts.optJSONArray(target))
            val divider = if (source.isNotEmpty() && destination.isNotEmpty()) listOf(ComposerPart(text = "\n")) else emptyList()
            drafts.put(target, encode(destination + divider + source)); drafts.put(old, JSONArray())
        }
        save(next)
        return targets.values.distinct().filter(drafts::has).associateWith { get(it).joinToString("") { p -> p.text.orEmpty() } }
    }
    @Synchronized fun clear(peer: String? = null) {
        val next = JSONObject(data.toString())
        val keys = peer?.let { listOf(it.lowercase(java.util.Locale.ROOT)) } ?: (next.getJSONObject("drafts").keys().asSequence().toList() + next.getJSONObject("sending").keys().asSequence().toList()).distinct()
        for (key in keys) { next.getJSONObject("drafts").put(key, JSONArray()); next.getJSONObject("sending").remove(key) }
        save(next)
    }
    @Synchronized fun height() = clampHeight(data.optDouble("height", 68.0).toFloat())
    @Synchronized fun setHeight(value: Float) { save(JSONObject(data.toString()).put("height", clampHeight(value))) }
    @Synchronized fun contains(peer: String) = data.getJSONObject("drafts").has(peer.lowercase(java.util.Locale.ROOT))
    @Synchronized fun get(peer: String?): List<ComposerPart> = if (peer == null) emptyList() else decode(data.getJSONObject("drafts").optJSONArray(peer.lowercase(java.util.Locale.ROOT)))
    @Synchronized fun snapshot(): Map<String, List<ComposerPart>> = data.getJSONObject("drafts").keys().asSequence().associateWith(::get)
    @Synchronized fun edit(peer: String, parts: List<ComposerPart>) {
        require(parts.count { it.file != null } <= maxOf(MAX_PENDING, get(peer).count { it.file != null }))
        val next = JSONObject(data.toString()); next.getJSONObject("drafts").put(peer.lowercase(java.util.Locale.ROOT), encode(parts)); save(next)
    }
    @Synchronized fun take(peer: String): List<ComposerPart> {
        val key = peer.lowercase(java.util.Locale.ROOT); check(!data.getJSONObject("sending").has(key))
        val parts = messages(get(peer)); val next = JSONObject(data.toString())
        next.getJSONObject("sending").put(key, encode(parts)); next.getJSONObject("drafts").put(key, JSONArray()); save(next); return parts
    }
    @Synchronized fun acknowledge(peer: String, id: UUID) {
        val key = peer.lowercase(java.util.Locale.ROOT); val parts = decode(data.getJSONObject("sending").optJSONArray(key))
        check(parts.firstOrNull()?.id == id)
        val next = JSONObject(data.toString()); next.getJSONObject("sending").put(key, encode(parts.drop(1))); save(next)
    }
    @Synchronized fun restore(peer: String) {
        val key = peer.lowercase(java.util.Locale.ROOT); val next = JSONObject(data.toString())
        val pending = decode(next.getJSONObject("sending").optJSONArray(key)); next.getJSONObject("sending").remove(key)
        next.getJSONObject("drafts").put(key, encode(pending + get(peer))); save(next)
    }
    @Synchronized internal fun collectStartupOrphans(referenced: Set<String>, minimumAgeMs: Long = 86_400_000): com.bluelink.android.files.TemporaryCleanup {
        if (editedThisProcess) return com.bluelink.android.files.TemporaryCleanup(0,0,0,0)
        val ids = listOf("drafts","sending").flatMap { section ->
            val values = data.getJSONObject(section)
            values.keys().asSequence().flatMap { decode(values.optJSONArray(it)).asSequence() }.mapNotNull { it.file?.id }.toList()
        }.toSet()
        return com.bluelink.android.files.OwnedTemporaryFiles.collect(File(root,"files"),
            { id -> id in ids || newFile(id).absolutePath in referenced }, preview=false, minimumAgeMs=minimumAgeMs)
    }
    fun newFile(id: UUID) = File(root, "files/$id.bin").also { it.parentFile!!.mkdirs() }
    fun deleteUnsubmitted(item: ComposerAttachment) { if (File(item.path).canonicalFile == newFile(item.id).canonicalFile) File(item.path).delete() }
    private fun encode(parts: List<ComposerPart>) = JSONArray().also { array -> parts.forEach { p ->
        array.put(JSONObject().put("id", p.id.toString()).put("text", p.text).also { obj -> p.file?.let { f ->
            obj.put("file", JSONObject().put("id", f.id.toString()).put("path", f.path).put("name", f.name).put("size", f.size).put("mime", f.mime)) } })
    } }
    private fun decode(array: JSONArray?): List<ComposerPart> = if (array == null) emptyList() else (0 until array.length()).map { i ->
        val p = array.getJSONObject(i); val f = p.optJSONObject("file")
        ComposerPart(UUID.fromString(p.getString("id")), if (p.has("text")) p.getString("text") else null,
            f?.let { ComposerAttachment(UUID.fromString(it.getString("id")), it.getString("path"), it.getString("name"), it.getLong("size"), it.optString("mime", "application/octet-stream")) })
    }
    private fun save(next: JSONObject) {
        editedThisProcess = true
        root.mkdirs(); val temporary = File(root, "drafts.json.tmp"); temporary.writeText(next.toString())
        Files.move(temporary.toPath(), manifest.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE); data = next
    }
}
