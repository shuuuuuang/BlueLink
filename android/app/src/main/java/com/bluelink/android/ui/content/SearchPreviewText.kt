package com.bluelink.android.ui.content

import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.sp
import com.bluelink.android.domain.SearchExcerpt
import com.bluelink.android.ui.devices.DeviceColors

@Composable
internal fun SearchPreviewText(text: String, query: String, fileName: Boolean = false, fontSize: TextUnit = 14.sp, maxLines: Int = 2) {
    val measure = rememberTextMeasurer()
    val style = LocalTextStyle.current.copy(fontSize = fontSize)
    val blue = DeviceColors.Blue
    BoxWithConstraints(Modifier.fillMaxWidth().semantics { contentDescription = text }) {
        val width = with(LocalDensity.current) { maxWidth.roundToPx().coerceAtLeast(1) }
        val excerpt = remember(text, query, fileName, width, style, measure, maxLines) {
            var low = 1
            var high = minOf(text.length.coerceAtLeast(1), 200)
            var best = SearchExcerpt.create(text, query, 1, fileName)
            while (low <= high) {
                val middle = (low + high) / 2
                val candidate = SearchExcerpt.create(text, query, middle, fileName)
                val result = measure.measure(candidate.text, style, maxLines = maxLines, constraints = Constraints(maxWidth = width))
                if (!result.hasVisualOverflow) { best = candidate; low = middle + 1 } else high = middle - 1
            }
            best
        }
        val annotated = remember(excerpt, blue) { buildAnnotatedString {
            append(excerpt.text)
            excerpt.highlights.forEach { addStyle(SpanStyle(color = blue), it.first, it.last + 1) }
        } }
        Text(annotated, style = style, maxLines = maxLines, overflow = TextOverflow.Ellipsis)
    }
}
