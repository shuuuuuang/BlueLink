package com.bluelink.android.sharing

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import android.util.AtomicFile
import androidx.core.content.IntentCompat
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.UUID

data class SharedFile(val id: UUID, val name: String, val mime: String, val size: Long, val sha256: String, val submitted: Boolean = false)
data class SharedRequest(val id: UUID, val fingerprint: String, val text: String, val files: List<SharedFile>,
    val peerId: String? = null, val textSubmitted: Boolean = false) {
    val complete get() = (text.isEmpty() || textSubmitted) && files.all { it.submitted }
    internal val completedPeerId: String?
        get() = peerId?.takeIf { it.isNotBlank() && complete && (text.isNotEmpty() || files.isNotEmpty()) }
}
class ShareInputException(val code: Code) : IOException(code.name) {
    enum class Code { UNSUPPORTED, TOO_MANY, TOO_LARGE, UNREADABLE, NO_SPACE, INTERRUPTED }
}

/** Only app-owned snapshots are written here. User sources are never moved or deleted. */
class ShareInbox(private val context: Context, private val root: File = File(context.filesDir,"share-inbox"),
    private val availableBytes: ()->Long = {root.usableSpace}) {
    companion object {
        const val MAX_FILES = 100
        const val MAX_TEXT_BYTES = 64 * 1024
        const val MAX_TOTAL_BYTES = 2L * 1024 * 1024 * 1024
        private const val RESERVE_BYTES = 16L * 1024 * 1024
    }
    private val mutex = Mutex()
    private val sendMutex = Mutex()
    private val mutableSending = MutableStateFlow<UUID?>(null)
    val sending = mutableSending.asStateFlow()
    interface Sender {
        suspend fun text(peerId: String, id: UUID, text: String): Boolean
        suspend fun file(peerId: String, id: UUID, source: Uri, name: String, size: Long): Boolean
    }
    suspend fun send(id: UUID,peerId: String,runtime: com.bluelink.android.runtime.BlueLinkRuntime): Boolean =
        send(id,peerId,object : Sender {
            override suspend fun text(peerId: String,id: UUID,text: String) = runtime.sendSharedText(peerId,id,text)
            override suspend fun file(peerId: String,id: UUID,source: Uri,name: String,size: Long) = runtime.sendSharedFile(peerId,id,source,name,size)
        })
    suspend fun send(id: UUID,peerId: String,sender: Sender): Boolean {
        if(!sendMutex.tryLock()) return false
        mutableSending.value=id
        try {
            load()
            var request=requests.value.first {it.id==id}
            require(request.peerId==null || request.peerId==peerId || (!request.textSubmitted && request.files.none {it.submitted}))
            setTarget(id,peerId)
            request=requests.value.first {it.id==id}
            if(request.text.isNotEmpty() && !request.textSubmitted) {
                if(!sender.text(peerId,id,request.text)) return false
                markText(id)
            }
            for(item in request.files.filterNot {it.submitted}) {
                val file=source(id,item)
                if(!sender.file(peerId,item.id,Uri.fromFile(file),item.name,item.size)) return false
                markFile(id,item.id)
            }
            return true
        } finally { mutableSending.value=null;sendMutex.unlock() }
    }
    private val mutableRequests = MutableStateFlow<List<SharedRequest>>(emptyList())
    val requests = mutableRequests.asStateFlow()
    private var loaded = false
    suspend fun load() = withContext(Dispatchers.IO) { mutex.withLock { loadLocked() } }
    private fun loadLocked() {
        if(loaded) return
        check(root.mkdirs() || root.isDirectory)
        val found = root.listFiles().orEmpty().mapNotNull { directory ->
            runCatching {
                val id=UUID.fromString(directory.name)
                require(directory.canonicalFile.parentFile == root.canonicalFile)
                decode(JSONObject(AtomicFile(File(directory,"request.json")).readFully().toString(Charsets.UTF_8))).also { require(it.id==id) }
            }.getOrNull()
        }
        mutableRequests.value=found; loaded=true
    }
    suspend fun capture(intent: Intent, progress: (String,Long)->Unit = {_,_->}): UUID = withContext(Dispatchers.IO) {
        mutex.withLock {
            loadLocked()
            if(intent.action !in setOf(Intent.ACTION_SEND,Intent.ACTION_SEND_MULTIPLE)) throw ShareInputException(ShareInputException.Code.UNSUPPORTED)
            val text=intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()?.takeUnless(String::isBlank).orEmpty()
            if(text.toByteArray(Charsets.UTF_8).size > MAX_TEXT_BYTES) throw ShareInputException(ShareInputException.Code.TOO_LARGE)
            val streams=if(intent.action==Intent.ACTION_SEND_MULTIPLE)
                IntentCompat.getParcelableArrayListExtra(intent,Intent.EXTRA_STREAM,Uri::class.java).orEmpty()
            else listOfNotNull(IntentCompat.getParcelableExtra(intent,Intent.EXTRA_STREAM,Uri::class.java))
            val uris=(streams.ifEmpty { (0 until (intent.clipData?.itemCount ?: 0)).mapNotNull { intent.clipData?.getItemAt(it)?.uri } }).distinct()
            if(uris.size>MAX_FILES) throw ShareInputException(ShareInputException.Code.TOO_MANY)
            if(uris.any { it.scheme != "content" } || (uris.isEmpty() && (text.isBlank() || intent.type?.startsWith("text/") != true)))
                throw ShareInputException(ShareInputException.Code.UNSUPPORTED)
            if(uris.isEmpty()) mutableRequests.value.firstOrNull {
                !it.complete && it.text == text && it.files.isEmpty()
            }?.let { return@withLock it.id }
            val id=UUID.randomUUID(); val folder=directory(id); check(folder.mkdir())
            val created=mutableListOf<File>()
            try {
                var total=0L
                val files=uris.map { uri ->
                    currentCoroutineContext().ensureActive()
                    var name="shared-file"; var knownSize: Long?=null
                    context.contentResolver.query(uri,arrayOf(OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE),null,null,null)?.use { cursor ->
                        if(cursor.moveToFirst()) {
                            cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME).takeIf {it>=0}?.let { name=cursor.getString(it) ?: name }
                            cursor.getColumnIndex(OpenableColumns.SIZE).takeIf {it>=0 && !cursor.isNull(it)}?.let { knownSize=cursor.getLong(it).takeIf {size->size>=0} }
                        }
                    }
                    if(name.isBlank() || name.any { it=='/' || it=='\\' || it.code<32 } || name.toByteArray().size>1024)
                        throw ShareInputException(ShareInputException.Code.UNSUPPORTED)
                    if(knownSize != null && knownSize!! > MAX_TOTAL_BYTES-total) throw ShareInputException(ShareInputException.Code.TOO_LARGE)
                    if(knownSize != null && knownSize!! > availableBytes()-RESERVE_BYTES) throw ShareInputException(ShareInputException.Code.NO_SPACE)
                    val fileId=UUID.randomUUID(); val destination=File(folder,"$fileId.bin").also(created::add)
                    val digest=MessageDigest.getInstance("SHA-256"); var size=0L
                    progress(name,0)
                    context.contentResolver.openInputStream(uri)?.use { input -> destination.outputStream().use { output ->
                        val buffer=ByteArray(128*1024)
                        while(true) {
                            currentCoroutineContext().ensureActive()
                            val count=input.read(buffer); if(count<0) break; if(count==0) continue
                            if(count > MAX_TOTAL_BYTES-total) throw ShareInputException(ShareInputException.Code.TOO_LARGE)
                            if(availableBytes()-count<RESERVE_BYTES) throw ShareInputException(ShareInputException.Code.NO_SPACE)
                            output.write(buffer,0,count); digest.update(buffer,0,count); size+=count; total+=count
                            progress(name,size)
                        }
                        output.fd.sync()
                    } } ?: throw ShareInputException(ShareInputException.Code.UNREADABLE)
                    SharedFile(fileId,name,context.contentResolver.getType(uri)?.takeIf { it.contains('/') } ?: "application/octet-stream",size,digest.digest().hex())
                }
                val fingerprint = contentFingerprint(text,files)
                mutableRequests.value.firstOrNull { !it.complete && contentFingerprint(it.text,it.files) == fingerprint }?.let { existing ->
                    created.forEach { check(it.delete()) };check(folder.delete())
                    return@withLock existing.id
                }
                val request=SharedRequest(id,fingerprint,text,files)
                persist(request); mutableRequests.value=mutableRequests.value+request; id
            } catch(error: Throwable) {
                created.forEach { it.delete() }; folder.delete()
                if(error is CancellationException || error is ShareInputException) throw error
                throw ShareInputException(ShareInputException.Code.UNREADABLE)
            }
        }
    }
    suspend fun setTarget(id: UUID,peerId: String) = change(id) {
        require(it.peerId==null || it.peerId==peerId || (!it.textSubmitted && it.files.none {file->file.submitted}))
        it.copy(peerId=peerId)
    }
    suspend fun markText(id: UUID) = change(id) { it.copy(textSubmitted=true) }
    suspend fun markFile(id: UUID,fileId: UUID) = change(id) { it.copy(files=it.files.map { file->if(file.id==fileId) file.copy(submitted=true) else file }) }
    private suspend fun change(id: UUID,update:(SharedRequest)->SharedRequest) = withContext(Dispatchers.IO) { mutex.withLock {
        loadLocked();val request=mutableRequests.value.first { it.id==id };val changed=update(request)
        persist(changed);mutableRequests.value=mutableRequests.value.map { if(it.id==id) changed else it }
    } }
    suspend fun source(id: UUID,item: SharedFile): File = withContext(Dispatchers.IO) {
        val file=File(directory(id),"${item.id}.bin")
        if(file.length()!=item.size || file.inputStream().use { input ->
                val digest=MessageDigest.getInstance("SHA-256");val buffer=ByteArray(128*1024)
                while(true) { currentCoroutineContext().ensureActive();val n=input.read(buffer);if(n<0)break;digest.update(buffer,0,n) }
                digest.digest().hex()
            } != item.sha256) throw ShareInputException(ShareInputException.Code.UNREADABLE)
        file
    }
    suspend fun discard(id: UUID) = withContext(Dispatchers.IO) { mutex.withLock {
        loadLocked();check(mutableSending.value!=id)
        val request=mutableRequests.value.firstOrNull { it.id==id } ?: return@withLock
        val folder=directory(id)
        // Delete only this manifest's exact owned names. No recursive directory deletion.
        for(item in request.files) { val file=File(folder,"${item.id}.bin"); if(file.exists() && !file.delete()) throw IOException("Snapshot cleanup failed") }
        AtomicFile(File(folder,"request.json")).delete();folder.delete()
        mutableRequests.value=mutableRequests.value.filterNot { it.id==id }
    } }
    private fun directory(id: UUID) = File(root,id.toString()).also { require(it.canonicalFile.parentFile==root.canonicalFile) }
    private fun persist(request: SharedRequest) {
        val file=AtomicFile(File(directory(request.id),"request.json"));val out=file.startWrite()
        try { out.write(encode(request).toString().toByteArray());file.finishWrite(out) } catch(error:Throwable) {file.failWrite(out);throw error}
    }
    private fun encode(value: SharedRequest)=JSONObject().put("version",1).put("id",value.id).put("fingerprint",value.fingerprint)
        .put("text",value.text).put("peerId",value.peerId).put("textSubmitted",value.textSubmitted)
        .put("files",JSONArray(value.files.map { JSONObject().put("id",it.id).put("name",it.name).put("mime",it.mime)
            .put("size",it.size).put("hash",it.sha256).put("submitted",it.submitted) }))
    private fun decode(value:JSONObject):SharedRequest {
        require(value.getInt("version")==1)
        val array=value.getJSONArray("files");require(array.length()<=MAX_FILES)
        val text=value.getString("text");require(text.toByteArray().size<=MAX_TEXT_BYTES)
        val files=(0 until array.length()).map { index->array.getJSONObject(index).let {
            SharedFile(UUID.fromString(it.getString("id")),it.getString("name"),it.getString("mime"),it.getLong("size"),it.getString("hash"),it.optBoolean("submitted")) } }
        require(files.all { it.size>=0 && it.size<=MAX_TOTAL_BYTES } && files.map { it.id }.distinct().size==files.size && files.sumOf {it.size}<=MAX_TOTAL_BYTES)
        return SharedRequest(UUID.fromString(value.getString("id")),value.getString("fingerprint"),text,files,
            value.optString("peerId").takeIf {it.isNotBlank()},value.optBoolean("textSubmitted"))
    }
    private fun contentFingerprint(text: String,files: List<SharedFile>) = hash(JSONObject().put("text",text)
        .put("files",JSONArray(files.map { JSONObject().put("name",it.name).put("mime",it.mime).put("size",it.size).put("hash",it.sha256) }))
        .toString().toByteArray(Charsets.UTF_8))
    private fun hash(bytes: ByteArray)=MessageDigest.getInstance("SHA-256").digest(bytes).hex()
    private fun ByteArray.hex()=joinToString("") { "%02x".format(it) }
}
