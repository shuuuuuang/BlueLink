package com.bluelink.android.files

import org.junit.Assert.*
import org.junit.Test
import java.nio.charset.Charset

class TextDocumentsTest {
    private fun read(bytes: ByteArray, name: String = "unknown.bin", mime: String = "application/octet-stream") =
        bytes.inputStream().use { TextDocuments.read(it,name,mime) }
    @Test fun detectsTextContentWithoutExtensionOrMimeHints() {
        val text="中文\nhello 🌍\t123"
        assertEquals(text,read(text.toByteArray())!!.text)
        assertEquals(text,read(text.toByteArray(),"README")!!.text)
        assertNotNull(read("{\"name\":\"blue\"}".toByteArray(),"data.dat"))
        assertFalse(read(text.toByteArray())!!.markdown)
    }
    @Test fun rejectsBinaryEvenWhenNamedAsText() {
        for (bytes in listOf(byteArrayOf(0,1,2,3,4,5),byteArrayOf(80,75,3,4,20,0),
            "%PDF-1.7\nreadable".toByteArray(), byteArrayOf(0x89.toByte(),80,78,71),
            "hello\u0000world".toByteArray(),"header\ntext\u0001tail".toByteArray())) {
            assertNull(read(bytes,"notes.md","text/markdown"))
        }
    }
    @Test fun officeAndPdfContentCannotEnterTextPreviewRegardlessOfName() {
        val office = java.io.ByteArrayOutputStream().also { output ->
            java.util.zip.ZipOutputStream(output).use { zip ->
                zip.putNextEntry(java.util.zip.ZipEntry("word/document.xml"))
                zip.write("<document>readable XML inside binary container</document>".toByteArray())
                zip.closeEntry()
            }
        }.toByteArray()
        for (name in listOf("manual.docx", "manual.md", "README")) {
            assertNull(read(office, name, "text/plain"))
            assertNull(read("%PDF-1.7\nreadable text".toByteArray(), name, "text/plain"))
        }
        // A binary-looking suffix is not a reason to bypass content detection.
        assertEquals("actual text", read("actual text".toByteArray(), "manual.docx")!!.text)
    }
    @Test fun bomAndCommonUtf16AreDecodedStrictly() {
        val text="会议记录 🌍\r\nsecond line"
        for ((encoding,bom) in listOf("UTF-8" to byteArrayOf(-17,-69,-65),
            "UTF-16LE" to byteArrayOf(-1,-2),"UTF-16BE" to byteArrayOf(-2,-1),
            "UTF-32LE" to byteArrayOf(-1,-2,0,0),"UTF-32BE" to byteArrayOf(0,0,-2,-1))) {
            val result=read(bom+text.toByteArray(Charset.forName(encoding)))!!
            assertEquals(text,result.text);assertEquals(encoding,result.encoding)
        }
        assertEquals("plain text",read("plain text".toByteArray(Charsets.UTF_16LE))!!.text)
        assertNull(read(byteArrayOf(-1,-2,65)))
        assertNull(read(byteArrayOf(-17,-69,-65,-64,-81)))
    }
    @Test fun conservativeChineseLegacyEncoding() {
        val text="会议记录：中文测试\nABC 123"
        assertEquals(text,read(text.toByteArray(Charset.forName("GB18030")))!!.text)
        assertNull(read(byteArrayOf(-1,-1,-1,-1)))
    }
    @Test fun largeFileReadIsBoundedAndSplitCharactersAreNotReplaced() {
        val source="a".repeat(TextDocuments.MAX_BYTES-1)+"🌍"+"tail"
        val input=source.toByteArray().inputStream()
        val result=TextDocuments.read(input,"a.txt","text/plain")!!
        assertTrue(result.truncated)
        assertEquals("a".repeat(TextDocuments.MAX_BYTES-1),result.text)
        assertTrue(input.available()>0)
        assertFalse(result.text.contains('\uFFFD'))
    }
    @Test fun markdownHintsOnlyApplyAfterSuccessfulContentProbe() {
        assertTrue(read("# Heading".toByteArray(),"README")!!.markdown)
        assertTrue(read("# Heading".toByteArray(),"a.md")!!.markdown)
        assertTrue(read("# Heading".toByteArray(),"a.bin","text/markdown")!!.markdown)
        assertFalse(read("# python comment".toByteArray(),"a.py")!!.markdown)
        assertEquals("",read(ByteArray(0))!!.text)
    }
    @Test fun markdownRendersStructureAndEscapesActiveContent() {
        val html=DocumentHtml.body("# Title\n\n**bold** and ~~old~~\n\n- one\n- two\n\n> quote\n\n```kotlin\nval x = 1\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |",true)
        for (tag in listOf("<h1 id=","<strong>","<del>","<ul>","<blockquote>","<pre>","<table>")) assertTrue(tag,html.contains(tag))
        val malicious=DocumentHtml.body("<script>alert(1)</script>\n\n[x](javascript:alert)\n\n<iframe src=\"file:///x\"></iframe>",true)
        assertFalse(malicious.contains("<script>"));assertFalse(malicious.contains("<iframe"));assertFalse(malicious.contains("href=\"javascript:"))
        assertTrue(DocumentHtml.page(html,false).contains("default-src 'none'"))
        assertTrue(DocumentHtml.body("<h1>raw</h1>",false).contains("&lt;h1&gt;raw&lt;/h1&gt;"))
    }
    @Test fun contentsFollowParsedHeadingsWithStableUniqueAnchors() {
        val source = "# 中文 **标题** `code`\n\n## 重复\n\n## 重复\n\nSetext\n======\n\n```md\n# not a heading\n```\n\n###### 六级\n\n# <b>\n"
        val result = DocumentHtml.render(source, true)
        assertEquals(listOf(1, 2, 2, 1, 6, 1), result.headings.map { it.level })
        assertEquals("中文 标题 code", result.headings.first().title)
        assertEquals("", result.headings.last().title)
        assertEquals(result.headings, DocumentHtml.render(source, true).headings)
        result.headings.forEachIndexed { index, heading ->
            assertEquals("section-${index + 1}", heading.id)
            assertTrue(result.body.contains("id=\"${heading.id}\""))
        }
        assertTrue(DocumentHtml.render(source, false).headings.isEmpty())
        assertFalse(DocumentHtml.render(source, false).body.contains("id=\"section-"))
        assertTrue(DocumentHtml.render("ordinary paragraph", true).headings.isEmpty())
    }

}
