package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import java.util.UUID

class MessageShareContentTest {
    private fun file(n: Long) = ChatAttachment(UUID(0,n),UUID(0,n),"file$n.bin","application/octet-stream",1,"content://qa/$n","COMPLETED")
    private fun row(n: Long, text: String = "", files: List<ChatAttachment> = emptyList()) =
        ChatItem(UUID(0,n),text,false,Instant.ofEpochSecond(n),MessageStatus.RECEIVED,attachments=files)
    @Test fun pureTextHasVisibleSeparatorsAndPreservesOriginalFormatting() {
        val first="  text🌍\nline2  "
        val rows=listOf(row(1,first),row(2,"same"),row(3,"same"))
        assertEquals(listOf(MessageSharePart.Text(first+MessageShareContent.SEPARATOR+"same"+MessageShareContent.SEPARATOR+"same")),MessageShareContent.parts(rows))
        assertEquals(0,MessageShareContent.fileCount(rows))
        assertEquals(listOf(MessageSharePart.Text(first)),MessageShareContent.parts(listOf(rows.first())))
    }
    @Test fun requestedExampleProducesExactlyTextFileTextInOrder() {
        val attachment=file(1)
        val rows=listOf(row(1,"文本1"),row(2,"文本2"),row(3,files=listOf(attachment)),row(4,"文本3"),row(5,"文本4"))
        assertEquals(listOf(MessageSharePart.Text("文本1"+MessageShareContent.SEPARATOR+"文本2"),MessageSharePart.File(attachment),MessageSharePart.Text("文本3"+MessageShareContent.SEPARATOR+"文本4")),MessageShareContent.parts(rows))
        assertEquals(3,MessageShareContent.fileCount(rows))
    }
    @Test fun filesAtEitherEdgeAndConsecutiveFilesDoNotCreateEmptyText() {
        val a=file(1);val b=file(2)
        assertEquals(listOf(MessageSharePart.File(a),MessageSharePart.File(b)),MessageShareContent.parts(listOf(row(1,files=listOf(a,b,a)))))
        assertEquals(listOf(MessageSharePart.File(a),MessageSharePart.Text("caption"),MessageSharePart.File(b)),MessageShareContent.parts(listOf(row(1,files=listOf(a)),row(2,"caption",listOf(b)))))
        assertTrue(MessageShareContent.parts(emptyList()).isEmpty())
    }
    @Test fun generatedFilesCountTowardsTheAttachmentLimit() {
        val rows=(1L..50L).flatMap { listOf(row(it*2,"text$it"),row(it*2+1,files=listOf(file(it)))) }
        assertEquals(100,MessageShareContent.fileCount(rows))
        assertEquals(101,MessageShareContent.fileCount(rows+row(200,"tail")))
    }
    @Test fun filenamesUseOrderedReadableSafeSummariesWithoutSplittingEmoji() {
        assertEquals("01-明天下午三点开会.txt",MessageShareContent.textFileName("\n 明天下午三点开会\n第二行",1,"分享文字"))
        assertEquals("02-计划 材料.txt",MessageShareContent.textFileName("计划/材料?*",2,"分享文字"))
        assertEquals("03-分享文字.txt",MessageShareContent.textFileName("...***",3,"分享文字"))
        assertEquals("04-CON.txt",MessageShareContent.textFileName("CON",4,"分享文字"))
        assertEquals("05-"+"🌍".repeat(28)+".txt",MessageShareContent.textFileName("🌍".repeat(40),5,"分享文字"))
    }

}
