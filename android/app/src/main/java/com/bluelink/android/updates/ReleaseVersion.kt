package com.bluelink.android.updates

internal data class ReleaseVersion(val major: Int, val minor: Int, val patch: Int, val preview: Int?) : Comparable<ReleaseVersion> {
    val base: String get() = "$major.$minor.$patch"
    val tag: String get() = "v" + base + (preview?.let { "-preview.$it" } ?: "")
    override fun compareTo(other: ReleaseVersion): Int {
        compareValuesBy(this, other, { it.major }, { it.minor }, { it.patch }).let { if (it != 0) return it }
        if (preview == null) return if (other.preview == null) 0 else 1
        return other.preview?.let { preview.compareTo(it) } ?: -1
    }
    companion object {
        fun parse(tag: String): ReleaseVersion? {
            val match = Regex("""^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-preview\.([1-9][0-9]*))?$""").matchEntire(tag) ?: return null
            val parts = (1..3).map { match.groupValues[it].toIntOrNull() ?: return null }
            val preview = match.groupValues[4].takeIf { it.isNotEmpty() }?.let { it.toIntOrNull() ?: return null }
            return ReleaseVersion(parts[0], parts[1], parts[2], preview)
        }
    }
}
