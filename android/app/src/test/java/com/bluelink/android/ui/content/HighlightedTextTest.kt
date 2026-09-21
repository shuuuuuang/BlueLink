package com.bluelink.android.ui.content

import androidx.compose.ui.graphics.Color
import org.junit.Assert.*
import org.junit.Test

class HighlightedTextTest {
    @Test fun repeatedCaseInsensitiveMatchesKeepTheOriginalFilename() {
        val name = "报告-Needle-needle.PDF"
        val value = highlightedSearchText(name, " NEEDLE ", Color.Blue)
        assertEquals(name, value.text)
        assertEquals(listOf(3 to 9, 10 to 16), value.spanStyles.map { it.start to it.end })
        assertTrue(value.spanStyles.all { it.item.color == Color.Blue })
    }

    @Test fun chineseAndUnicodePrefixUseCorrectTextOffsets() {
        val value = highlightedSearchText("🌍报告-报告.pdf", "报告", Color.Blue)
        assertEquals(listOf(2 to 4, 5 to 7), value.spanStyles.map { it.start to it.end })
    }

    @Test fun clearingOrMissingQueryLeavesPlainText() {
        for (query in listOf("", "   ", "missing")) {
            val value = highlightedSearchText("报告.pdf", query, Color.Blue)
            assertEquals("报告.pdf", value.text)
            assertTrue(value.spanStyles.isEmpty())
        }
    }

    @Test fun changingTheThemeChangesOnlyHighlightColor() {
        val light = highlightedSearchText("Needle.pdf", "needle", Color.Blue)
        val dark = highlightedSearchText("Needle.pdf", "needle", Color.Cyan)
        assertEquals(light.text, dark.text)
        assertEquals(Color.Cyan, dark.spanStyles.single().item.color)
        assertEquals(light.spanStyles.single().start, dark.spanStyles.single().start)
        assertEquals(light.spanStyles.single().end, dark.spanStyles.single().end)
    }
}
