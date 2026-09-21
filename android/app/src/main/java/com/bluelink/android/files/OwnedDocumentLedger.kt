package com.bluelink.android.files

import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID
import org.json.JSONObject
import org.json.JSONArray

/** Exact provider objects only. Never infer ownership from a directory or filename prefix. */
internal class OwnedDocumentLedger(private val manifest: File) {
    private val active = mutableSetOf<String>()
    private fun read() = if (manifest.exists()) JSONObject(manifest.readText()) else JSONObject()
    private fun save(data: JSONObject) {
        manifest.parentFile!!.mkdirs()
        val temporary = File(manifest.path+".new")
        temporary.outputStream().use { it.write(data.toString().toByteArray()); it.fd.sync() }
        Files.move(temporary.toPath(),manifest.toPath(),StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING)
    }
    @Synchronized fun begin(epoch: String, tree: String, directory: String) {
        val data=read(); check(!data.has(epoch));
        data.put(epoch,JSONObject().put("tree",tree).put("directory",directory).put("created",System.currentTimeMillis()).put("objects",JSONArray()))
        save(data); active.add(epoch)
    }
    @Synchronized fun add(epoch: String, uri: String, task: UUID? = null) {
        check(epoch in active); val data=read(); val objects=data.getJSONObject(epoch).getJSONArray("objects")
        if ((0 until objects.length()).none { objects.getJSONObject(it).getString("uri")==uri }) {
            objects.put(JSONObject().put("uri",uri).put("task",task?.toString())); save(data)
        }
    }
    @Synchronized fun release(epoch: String) { active.remove(epoch) }
    @Synchronized fun collect(preview: Boolean, referenced: (UUID)->Boolean, size: (String)->Long?,
        delete: (String)->Boolean, deleteEmptyDirectory: (String,String)->Boolean, minimumAgeMs: Long=86_400_000): TemporaryCleanup {
        val data=read(); var bytes=0L; var files=0; var retained=0; var errors=0
        for (epoch in data.keys().asSequence().toList()) {
            val row=data.getJSONObject(epoch)
            if (epoch in active || System.currentTimeMillis()-row.getLong("created")<minimumAgeMs) { retained++; continue }
            val objects=row.getJSONArray("objects"); val keep=JSONArray()
            for (i in 0 until objects.length()) {
                val item=objects.getJSONObject(i)
                try {
                    val task=item.optString("task").takeIf { it.isNotEmpty() }?.let(UUID::fromString)
                    if (task!=null && referenced(task)) { retained++; keep.put(item); continue }
                    val uri=item.getString("uri"); val length=size(uri)
                    if (length==null) continue // Already absent; unreadable providers must throw.
                    if (length<0) { retained++; keep.put(item); continue }
                    if (preview || (task==null || !referenced(task)) && delete(uri)) { bytes+=length; files++ }
                    else { retained++; keep.put(item) }
                } catch (_: Exception) { errors++; keep.put(item) }
            }
            if (!preview) {
                row.put("objects",keep)
                if (keep.length()==0) try { if (deleteEmptyDirectory(row.getString("tree"),row.getString("directory"))) data.remove(epoch) }
                catch (_: Exception) { errors++ }
            }
        }
        if (!preview) save(data)
        return TemporaryCleanup(bytes,files,retained,errors)
    }
    @Synchronized fun measure(size: (String)->Long?): Pair<Long,Boolean> {
        var bytes=0L; var partial=false; val data=read(); val seen=mutableSetOf<String>()
        for (epoch in data.keys()) {
            val rows=data.getJSONObject(epoch).getJSONArray("objects")
            for (i in 0 until rows.length()) try {
                val uri=rows.getJSONObject(i).getString("uri"); if (!seen.add(uri)) continue
                val length=size(uri) ?: continue
                if (length<0) partial=true else bytes+=length
            } catch (_: Exception) { partial=true }
        }
        return bytes to partial
    }
}
