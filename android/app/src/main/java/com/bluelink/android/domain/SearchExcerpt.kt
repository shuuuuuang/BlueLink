package com.bluelink.android.domain

/** Display-only slices; actions must continue to use the original message or filename. */
data class SearchExcerpt(val text: String, val highlights: List<IntRange>) {
    companion object {
        fun create(source: String, query: String, budget: Int = 96, fileName: Boolean = false): SearchExcerpt {
            val bounds = Regex("\\X").findAll(source).map { it.range.first }.toList() + source.length
            val count = bounds.size - 1
            val limit = budget.coerceAtLeast(1)
            val needle = query.trim()
            val match = if (needle.isEmpty()) -1 else source.indexOf(needle, ignoreCase = true)
            val unit = if (match < 0) 0 else bounds.indexOfLast { it <= match }.coerceAtMost(count)
            val dot = if (fileName) source.lastIndexOf('.').takeIf { it > 0 && it < source.lastIndex } else null
            val suffix = dot?.let { bounds.indexOfLast { b -> b <= it } } ?: count
            val suffixUnits = count - suffix
            val reserve = if (count > limit && suffixUnits in 1 until limit / 2) suffixUnits else 0
            val window = (limit - reserve).coerceAtLeast(1)
            val start = if (count <= limit) 0 else (unit - minOf(12, window / 4)).coerceAtLeast(0)
            val end = if (count <= limit) count else (start + window).coerceAtMost(count)
            val parts = mutableListOf(bounds[start] to bounds[end])
            if (reserve > 0 && suffix >= end && end < count) parts += bounds[suffix] to source.length
            val matches = mutableListOf<IntRange>()
            if (needle.isNotEmpty()) {
                var index = source.indexOf(needle, ignoreCase = true)
                while (index >= 0) {
                    matches += index until index + needle.length
                    index = source.indexOf(needle, index + needle.length, ignoreCase = true)
                }
            }
            val output = StringBuilder()
            val spans = mutableListOf<IntRange>()
            var previous = 0
            for ((from, to) in parts) {
                if (from > previous) output.append('…')
                val offset = output.length
                output.append(source.substring(from, to).replace('\r', ' ').replace('\n', ' '))
                for (hit in matches) {
                    val left = maxOf(from, hit.first)
                    val right = minOf(to, hit.last + 1)
                    if (left < right) spans += (offset + left - from) until (offset + right - from)
                }
                previous = to
            }
            if (previous < source.length) output.append('…')
            return SearchExcerpt(output.toString(), spans)
        }
    }
}
