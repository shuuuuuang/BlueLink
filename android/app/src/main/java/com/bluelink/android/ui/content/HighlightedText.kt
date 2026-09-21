package com.bluelink.android.ui.content

import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.sp
import com.bluelink.android.ui.devices.DeviceColors

internal fun highlightedSearchText(text: String, query: String, color: Color): AnnotatedString = buildAnnotatedString {
    append(text)
    val needle = query.trim()
    if (needle.isNotEmpty()) {
        var start = text.indexOf(needle, ignoreCase = true)
        while (start >= 0) {
            addStyle(SpanStyle(color = color), start, start + needle.length)
            start = text.indexOf(needle, start + needle.length, ignoreCase = true)
        }
    }
}

@Composable
internal fun HighlightedText(text: String, query: String, fontSize: TextUnit = 14.sp, maxLines: Int = 2) {
    val highlightColor = DeviceColors.Blue
    val annotated = remember(text, query, highlightColor) { highlightedSearchText(text, query, highlightColor) }
    Text(annotated, fontSize = fontSize, fontWeight = FontWeight.Normal, maxLines = maxLines, overflow = TextOverflow.Ellipsis)
}
