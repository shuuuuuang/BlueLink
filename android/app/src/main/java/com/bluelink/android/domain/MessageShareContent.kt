package com.bluelink.android.domain

sealed interface MessageSharePart {
    data class Text(val content: String) : MessageSharePart
    data class File(val attachment: ChatAttachment) : MessageSharePart
}

object MessageShareContent {
    const val SEPARATOR = "\n\n━━━━━━━━━━━━━━━━━━━━\n\n"
    fun merge(texts: List<String>): String = texts.joinToString(SEPARATOR)

    /** Input is the selected timeline. Every attachment terminates the current text run. */
    fun parts(messages: List<ChatItem>): List<MessageSharePart> {
        val result = mutableListOf<MessageSharePart>()
        val texts = mutableListOf<String>()
        val seen = mutableSetOf<java.util.UUID>()
        fun flushText() {
            if (texts.isNotEmpty()) { result += MessageSharePart.Text(merge(texts)); texts.clear() }
        }
        for (message in messages) {
            if (message.text.isNotBlank()) texts += message.text
            if (message.attachments.isNotEmpty()) {
                flushText()
                message.attachments.filter { seen.add(it.attachmentId) }.forEach { result += MessageSharePart.File(it) }
            }
        }
        flushText()
        return result
    }

    fun textFileName(text: String, index: Int, fallback: String): String {
        require(index > 0)
        val firstLine = text.lineSequence().firstOrNull { it.isNotBlank() }.orEmpty()
        val cleaned = firstLine.map { ch ->
            if (ch.code < 32 || ch in "<>:\"/\\|?*" || Character.getType(ch) == Character.FORMAT.toInt()) ' ' else ch
        }.joinToString("").replace(Regex("\\s+"), " ").trim(' ', '.')
        val summary = cleaned.codePoints().limit(28).toArray().let { points ->
            StringBuilder().apply { points.forEach { appendCodePoint(it) } }.toString()
        }.trim(' ', '.').ifBlank { fallback }
        return index.toString().padStart(2, '0') + "-" + summary + ".txt"
    }

    fun fileCount(messages: List<ChatItem>): Int =
        if (messages.any { it.attachments.isNotEmpty() }) parts(messages).size else 0
}
