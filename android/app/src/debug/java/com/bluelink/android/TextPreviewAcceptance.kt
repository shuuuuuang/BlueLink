package com.bluelink.android

import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.FileProvider
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.files.*
import java.io.File
import java.util.UUID
import kotlinx.coroutines.launch

/** Only generated QA documents; actual viewer component and completed-file routing. */
@Composable internal fun TextPreviewAcceptance(kind: String, close: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var opening by remember(kind) { mutableStateOf(false) }
    var status by remember(kind) { mutableStateOf("QA Ready") }
    val routing = kind.startsWith("route-")
    val fixture = kind.removePrefix("route-")
    val attachment = remember(kind) {
        val root = File(context.cacheDir,"shared/acceptance/text-preview").apply {mkdirs()}
        val name = when(fixture) { "docx" -> "文档.docx"; "pdf" -> "说明.pdf"; "missing" -> "missing.txt"; "markdown" -> "项目说明.md"; "toc" -> "目录验收.md"; "empty-md" -> "无标题.md"; "utf16" -> "旧编码日志.log"; "large" -> "长文本.txt"; "large-md" -> "长文档.md"; "binary" -> "伪装文本.md"; else -> "实际为文本.bin" }
        val file = File(root,name)
        when(fixture) {
            "docx" -> java.util.zip.ZipOutputStream(file.outputStream()).use { zip ->
                zip.putNextEntry(java.util.zip.ZipEntry("word/document.xml"))
                zip.write("<document>QA binary office document</document>".toByteArray())
                zip.closeEntry()
            }
            "pdf" -> file.writeText("%PDF-1.7\nQA unsupported binary document")
            "missing" -> file.delete()
            "toc" -> file.writeText("# 目录验收\n\n" + (1..12).joinToString("\n\n") { index ->
                "## 章节 $index\n\n" + "段落内容，用于检查目录定位。\n\n".repeat(10) + "### 小节 $index\n\n小节正文。"
            })
            "empty-md" -> file.writeText("没有标题的 Markdown 文档。")
            "markdown" -> file.writeText("""# 蓝联文档预览

这是 **粗体**、*斜体* 和 ~~删除线~~。中文与 emoji 🌍。

## 待办事项
- 检查无后缀文本
- 保留原始内容

> 引用：本地阅读，无需离开蓝联。

| 格式 | 状态 |
| --- | --- |
| Markdown | 支持排版 |
| 文本 | 支持复制 |

```kotlin
val message = "Hello BlueLink"
println(message)
```

<script>document.body.innerHTML='UNSAFE SCRIPT RAN'</script>

![远程图片](https://example.invalid/never-load.png)
""",Charsets.UTF_8)
            "utf16" -> file.writeBytes(byteArrayOf(-1,-2)+"UTF-16 编码日志\n第一行：中文 🌍\n第二行：原始换行保留".toByteArray(Charsets.UTF_16LE))
            "large-md" -> file.writeText("# 大文档预览\n\n" + "文档内容，检查预览范围提示。\n\n".repeat(12000))
            "large" -> file.writeText("大文件预览\n"+"abcdef 123456\n".repeat(24000))
            "binary" -> file.writeBytes(byteArrayOf(0x89.toByte(),80,78,71,13,10,26,10,0,1,2,3))
            else -> file.writeText("没有依赖后缀名判断。\n这其实是一份 UTF-8 文本，包含中文和 emoji 🌍。\n{\"message\":\"保留原文\"}")
        }
        val uri = FileProvider.getUriForFile(context,"${context.packageName}.files",file)
        ChatAttachment(UUID.randomUUID(),UUID.randomUUID(),name,"application/octet-stream",file.length(),uri.toString(),"COMPLETED")
    }
    LaunchedEffect(Unit) {
        var checks = 0
        for (encoding in listOf("UTF-8", "UTF-16LE", "UTF-16BE", "UTF-32LE", "GB18030")) {
            val text = "中文文本 Hello 123"
            val bom = when (encoding) {
                "UTF-16LE" -> byteArrayOf(-1,-2)
                "UTF-16BE" -> byteArrayOf(-2,-1)
                "UTF-32LE" -> byteArrayOf(-1,-2,0,0)
                else -> ByteArray(0)
            }
            val bytes = bom + text.toByteArray(java.nio.charset.Charset.forName(encoding))
            check(TextDocuments.read(bytes.inputStream(),"unknown.bin","application/octet-stream")?.text == text)
            checks++
        }
        check(DocumentHtml.body("# runtime markdown",true).contains("<h1 id=\"section-1\">runtime markdown</h1>")); checks++
        check(TextDocuments.read(byteArrayOf(0,1,2,3).inputStream(),"fake.md","text/markdown") == null); checks++
        File(context.cacheDir,"text-preview-runtime-result.txt").writeText("PASS $checks Android encoding, Markdown and binary checks")
    }
    DisposableEffect(kind) {
        val application = context.applicationContext as android.app.Application
        val events = File(context.cacheDir, "text-preview-route-events.txt")
        events.writeText("origin=$kind\n")
        val listener = object : android.app.Application.ActivityLifecycleCallbacks {
            override fun onActivityCreated(activity: android.app.Activity, state: android.os.Bundle?) {
                if (activity is TextPreviewActivity) events.appendText("preview-created\n")
            }
            override fun onActivityStarted(activity: android.app.Activity) = Unit
            override fun onActivityResumed(activity: android.app.Activity) = Unit
            override fun onActivityPaused(activity: android.app.Activity) = Unit
            override fun onActivityStopped(activity: android.app.Activity) = Unit
            override fun onActivitySaveInstanceState(activity: android.app.Activity, state: android.os.Bundle) = Unit
            override fun onActivityDestroyed(activity: android.app.Activity) = Unit
        }
        application.registerActivityLifecycleCallbacks(listener)
        onDispose { application.unregisterActivityLifecycleCallbacks(listener) }
    }
    Column(Modifier.fillMaxSize().statusBarsPadding()) {
        Row {
            TextButton(enabled = !opening, onClick = {
                opening = true
                scope.launch {
                    try { FileInteraction.open(context, attachment); status = "QA Open returned" }
                    catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
                    catch (_: Exception) { status = "QA Open failed at origin" }
                    finally { opening = false }
                }
            }) { Text("QA Open actual viewer") }
            TextButton(onClick=close) {Text("QA Close")}
        }
        if (routing) {
            Text("QA File origin: ${attachment.fileName}")
            Text(status)
        } else Box(Modifier.weight(1f)) {
            TextPreviewScreen(attachment,close,external={
                File(context.cacheDir,"text-preview-binary-result.txt").writeText("PASS binary .md routed to external open")
            })
        }
    }
}
