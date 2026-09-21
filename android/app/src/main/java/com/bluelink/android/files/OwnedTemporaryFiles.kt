package com.bluelink.android.files

import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID

internal data class TemporaryCleanup(val bytes: Long, val files: Int, val retained: Int, val errors: Int)
internal object OwnedTemporaryFiles {
    private val active = mutableSetOf<String>()
    private var privateRoot: File? = null
    @Synchronized fun configurePrivateRoot(root: File) {
        check(root.isDirectory)
        val normalized = root.absoluteFile.normalize()
        check(privateRoot == null || privateRoot == normalized)
        privateRoot = normalized
    }
    @Synchronized fun register(file: File, taskId: UUID) {
        safe(file)
        check(!file.exists() && file.absolutePath !in active) { "Temporary file already exists" }
        check(file.parentFile!!.isDirectory || file.parentFile!!.mkdirs())
        val marker = File(file.path + ".owner.json"); safe(marker)
        val row = JSONObject().put("version",1).put("name",file.name).put("task",taskId.toString()).put("created",System.currentTimeMillis())
        val temporary = File(marker.path + ".new"); safe(temporary)
        FileOutputStream(temporary).use { it.write(row.toString().toByteArray()); it.fd.sync() }
        Files.move(temporary.toPath(),marker.toPath(),StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING)
        active.add(file.absolutePath)
    }
    @Synchronized fun release(file: File) {
        active.remove(file.absolutePath)
        runCatching { safe(file); if (!file.exists()) File(file.path + ".owner.json").delete() }
    }
    @Synchronized fun collect(root: File, referenced: (UUID) -> Boolean, preview: Boolean, minimumAgeMs: Long = 0): TemporaryCleanup {
        safe(root)
        if (!root.exists()) return TemporaryCleanup(0,0,0,0)
        val markers = root.listFiles { file -> file.name.endsWith(".owner.json") } ?: return TemporaryCleanup(0,0,0,1)
        var bytes = 0L; var files = 0; var retained = 0; var errors = 0
        for (marker in markers) {
            try {
                safe(marker)
                val row = JSONObject(marker.readText()); val name = row.getString("name")
                val id = UUID.fromString(row.getString("task"))
                check(row.getInt("version") == 1 && File(name).name == name && name + ".owner.json" == marker.name)
                val file = File(root,name); safe(file)
                if (file.absolutePath in active || referenced(id) || System.currentTimeMillis()-row.getLong("created") < minimumAgeMs) { retained++; continue }
                if (!file.exists()) { if (!preview) check(marker.delete()); continue }
                val length = Files.size(file.toPath())
                if (!preview) {
                    if (referenced(id)) { retained++; continue }
                    check(file.delete())
                    check(marker.delete())
                }
                bytes = Math.addExact(bytes,length); files++
            } catch (_: Exception) { errors++ }
        }
        return TemporaryCleanup(bytes,files,retained,errors)
    }
    private fun safe(file: File) = validateTemporaryPath(file, privateRoot)
}

/** The OS-supplied application data directory is the trust boundary; aliases above it
 * belong to Android's per-app mount namespace, not to temporary-file owners. */
internal fun validateTemporaryPath(file: File, privateRoot: File? = null) {
    val boundary = privateRoot?.absoluteFile?.normalize()?.toPath()
    var current: java.nio.file.Path? = file.absoluteFile.normalize().toPath()
    check(boundary == null || current!!.startsWith(boundary)) { "Temporary path outside application storage" }
    while (current != null && current != boundary) {
        check(!Files.isSymbolicLink(current)) { "Redirected temporary storage is not eligible for cleanup" }
        current = current.parent
    }
}
