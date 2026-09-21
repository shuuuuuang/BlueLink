package com.bluelink.android.files

import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.Charset
import java.nio.charset.CodingErrorAction
import java.util.Locale

internal data class TextDocument(val text: String, val encoding: String, val truncated: Boolean, val markdown: Boolean)

/** A bounded content probe, never a filename/MIME allow-list. Unknown encodings fail closed. */
internal object TextDocuments {
    const val MAX_BYTES = 256 * 1024
    fun read(input: InputStream, name: String, mime: String): TextDocument? {
        val bytes = input.readNBytes(MAX_BYTES + 1)
        val truncated = bytes.size > MAX_BYTES
        val content = if (truncated) bytes.copyOf(MAX_BYTES) else bytes
        val decoded = decode(content, truncated) ?: return null
        val extension = name.substringAfterLast('.', "").lowercase(Locale.ROOT)
        val markdown = extension in setOf("md", "markdown", "mdown", "mkd", "mdx") ||
            mime.substringBefore(';').lowercase(Locale.ROOT) in setOf("text/markdown", "text/x-markdown") ||
            (extension.isEmpty() && Regex("(?m)^(#{1,6} |```|~~~|> )").containsMatchIn(decoded.first))
        return TextDocument(decoded.first, decoded.second, truncated, markdown)
    }

    private fun decode(bytes: ByteArray, truncated: Boolean): Pair<String, String>? {
        fun starts(vararg values: Int) = bytes.size >= values.size && values.indices.all { (bytes[it].toInt() and 255) == values[it] }
        // Some binary formats start with readable ASCII and must not become a text preview.
        if (starts(0x25,0x50,0x44,0x46,0x2d) || starts(0x50,0x4b,3,4) || starts(0x50,0x4b,5,6) ||
            starts(0x89,0x50,0x4e,0x47) || starts(0xff,0xd8,0xff) || starts(0x1f,0x8b) ||
            starts(0x47,0x49,0x46,0x38) || starts(0x52,0x61,0x72,0x21) || starts(0x37,0x7a,0xbc,0xaf) ||
            (starts(0x52,0x49,0x46,0x46) && bytes.size >= 12 && bytes.copyOfRange(8,12).toString(Charsets.US_ASCII) == "WEBP")) return null
        val bom = when {
            starts(0xff,0xfe,0,0) -> "UTF-32LE" to 4
            starts(0,0,0xfe,0xff) -> "UTF-32BE" to 4
            starts(0xef,0xbb,0xbf) -> "UTF-8" to 3
            starts(0xff,0xfe) -> "UTF-16LE" to 2
            starts(0xfe,0xff) -> "UTF-16BE" to 2
            else -> null
        }
        fun attempt(encoding: String, offset: Int = 0): String? = runCatching {
            val decoder = Charset.forName(encoding).newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
            val input = ByteBuffer.wrap(bytes, offset, bytes.size - offset)
            val output = CharBuffer.allocate(bytes.size + 1)
            val result = decoder.decode(input, output, !truncated)
            if (result.isError) result.throwException()
            if (!truncated) decoder.flush(output)
            output.flip()
            output.toString().takeIf { text -> text.none { ch ->
                ch == '\uFFFD' || (Character.isISOControl(ch) && ch !in "\r\n\t\u000c")
            } }
        }.getOrNull()
        if (bom != null) return attempt(bom.first, bom.second)?.let { it to bom.first }
        if (bytes.any { it == 0.toByte() }) {
            // Detect common BOM-less UTF-16 only when alternating NULs strongly support it.
            val pairs = bytes.size / 2
            if (pairs < 2) return null
            val even = (0 until pairs).count { bytes[it * 2] == 0.toByte() }
            val odd = (0 until pairs).count { bytes[it * 2 + 1] == 0.toByte() }
            val encoding = when {
                odd >= pairs * .6 && even == 0 -> "UTF-16LE"
                even >= pairs * .6 && odd == 0 -> "UTF-16BE"
                else -> return null
            }
            return attempt(encoding)?.let { it to encoding }
        }
        attempt("UTF-8")?.let { return it to "UTF-8" }
        // Conservative legacy Chinese support; do not silently treat every arbitrary byte as text.
        val chinese = attempt("GB18030") ?: return null
        val nonAscii = chinese.count { it.code >= 128 }
        if (nonAscii == 0 || chinese.count { it in '\u3400'..'\u9fff' } < nonAscii * .6) return null
        return chinese to "GB18030"
    }
}
