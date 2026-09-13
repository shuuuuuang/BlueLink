package com.bluelink.android

import android.app.Activity
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import com.bluelink.android.domain.ConnectionPhase
import com.bluelink.android.domain.SessionRoute
import com.bluelink.android.domain.SessionTransport
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.security.SecureRandom

/** Debug-only, explicitly selected real USB peer. No system picker or unrelated app is opened. */
@Composable
internal fun PhysicalTransferAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    val application = context.applicationContext as BlueLinkApplication
    val runtime = application.runtime
    val activity = generateSequence(context) { (it as? android.content.ContextWrapper)?.baseContext }
        .filterIsInstance<Activity>().firstOrNull()
    val peer = activity?.intent?.getStringExtra("peerId").orEmpty()
    val sessions by runtime.sessions.collectAsState()
    val transfers by runtime.transfers.collectAsState()
    val messages by runtime.messages.collectAsState()
    val route = SessionRoute.preferred(sessions, peer, true)
    val ready = peer.matches(Regex("[a-fA-F0-9]{32}")) && route?.phase == ConnectionPhase.CONNECTED && route.usbFileReady
    val scope = rememberCoroutineScope()
    var busy by remember { mutableStateOf(false) }
    var result by remember { mutableStateOf("") }
    LaunchedEffect(peer) { if (peer.matches(Regex("[a-fA-F0-9]{32}"))) runtime.selectPeer(peer) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("BlueLink QA · 真实 USB 传输", style = MaterialTheme.typography.titleLarge)
        Text(if (ready) "已认证 USB 对端" else "等待指定 USB 对端连接")
        Text(peer, style = MaterialTheme.typography.bodySmall)
        Button(onClick = { runtime.sendChat("BlueLink QA phone to Windows") }, enabled = ready) { Text("发送验收消息") }
        for (bytes in listOf(0L, 1024L, 65535L, 65536L, 65537L, 16L * 1024 * 1024, 256L * 1024 * 1024)) {
            Button(enabled = ready && !busy, onClick = {
                busy = true
                scope.launch {
                    try {
                        val file = withContext(Dispatchers.IO) {
                            val root = File(context.cacheDir, "shared/physical-transfer").apply { mkdirs() }
                            val file = File(root, "bluelink-qa-phone-$bytes.bin")
                            if (!file.exists()) {
                                check(android.os.StatFs(root.path).availableBytes > bytes + 64L * 1024 * 1024) { "Insufficient QA storage" }
                                file.outputStream().use { output ->
                                    val block = ByteArray(65536); val random = SecureRandom(); var remaining = bytes
                                    while (remaining > 0) { random.nextBytes(block); val count = minOf(remaining, block.size.toLong()).toInt(); output.write(block, 0, count); remaining -= count }
                                }
                            }
                            check(file.length() == bytes)
                            val digest = MessageDigest.getInstance("SHA-256")
                            file.inputStream().use { input -> val block = ByteArray(65536); while (true) { val count = input.read(block); if (count < 0) break; digest.update(block, 0, count) } }
                            val hash = digest.digest().joinToString("") { "%02x".format(it) }
                            File(root, "sources.jsonl").appendText(JSONObject().put("name", file.name).put("bytes", bytes).put("sha256", hash).toString() + "\n")
                            file
                        }
                        check(SessionRoute.preferred(runtime.sessions.value, peer, true)?.usbFileReady == true)
                        runtime.selectPeer(peer)
                        runtime.sendFile(FileProvider.getUriForFile(context, "${context.packageName}.files", file), file.name, bytes)
                        result = "已提交 ${file.name}"
                    } catch (error: Exception) { result = "FAILED: ${error.message}" }
                    finally { busy = false }
                }
            }) { Text("发送 $bytes B") }
        }
        Text(result)
        transfers.values.filter { it.name.startsWith("bluelink-qa-") }.takeLast(8).forEach { item ->
            Text("${item.name}: ${item.status} ${item.completedBytes}/${item.totalBytes}")
        }
        messages.filter { it.text.startsWith("BlueLink QA ") }.takeLast(4).forEach { Text("${it.text}: ${it.status}") }
        TextButton(onClick = close) { Text("返回蓝联") }
    }
}
