package com.bluelink.android.updates

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class GitHubUpdateTest {
    private fun version(tag: String) = requireNotNull(ReleaseVersion.parse(tag))
    private fun entry(tag: String, draft: Boolean = false): JSONObject {
        val name = "BlueLink-${version(tag).base}-android-universal-release.apk"
        return JSONObject().put("tag_name", tag).put("draft", draft).put("prerelease", tag.contains("preview"))
            .put("body", "Release notes").put("assets", JSONArray().put(JSONObject().put("name", name)
                .put("size", 1024).put("digest", "sha256:" + "a".repeat(64))
                .put("browser_download_url", "https://github.com/shuuuuuang/BlueLink/releases/download/$tag/$name")))
    }
    private fun feed(vararg entries: JSONObject) = JSONArray(entries.toList()).toString()
    private fun parse(vararg entries: JSONObject) = GitHubUpdateService.parse(feed(*entries), version("v0.2.16"))

    @Test fun numericPreviewOrderingAndStablePromotion() {
        assertTrue(version("v0.2.17-preview.10") > version("v0.2.17-preview.9"))
        assertTrue(version("v0.2.17") > version("v0.2.17-preview.2147483647"))
        assertTrue(version("v0.2.18-preview.1") > version("v0.2.17"))
    }
    @Test fun rejectsMalformedTags() {
        listOf("vv0.2.17", "v0.2", "v0.2.17.0", "v00.2.17", "v0.2.17-preview.0",
            "v0.2.17-beta.1", "v0.2.17-preview.99999999999").forEach { assertNull(ReleaseVersion.parse(it)) }
    }
    @Test fun selectsNewestCompleteReleaseWithNumericPreview() {
        val result = parse(entry("v0.2.17-preview.2"), entry("v0.2.17-preview.10"), entry("v9.0.0", true))
        assertEquals("v0.2.17-preview.10", result?.tag)
        assertEquals("https://github.com/shuuuuuang/BlueLink/releases/tag/v0.2.17-preview.10", result?.pageUrl)
    }
    @Test fun ignoresDraftAndMismatchedPrereleaseFlag() {
        assertNull(parse(entry("v9.0.0", true), entry("v0.2.17-preview.1").put("prerelease", false)))
    }
    @Test fun doesNotDowngradeOrRepeat() {
        val releases = feed(entry("v0.2.17-preview.3"))
        assertNull(GitHubUpdateService.parse(releases, version("v0.2.17-preview.3")))
        assertNull(GitHubUpdateService.parse(releases, version("v0.2.17")))
        assertNull(GitHubUpdateService.parse(releases, version("v0.3.0")))
    }
    @Test fun emptyFeedIsUpToDate() { assertNull(parse()) }
    @Test fun rejectsForeignDownloadUrl() {
        val release = entry("v0.2.17")
        release.getJSONArray("assets").getJSONObject(0).put("browser_download_url", "https://example.com/update.apk")
        assertThrows(IllegalArgumentException::class.java) { parse(release) }
    }
    @Test fun rejectsMissingOrDuplicateUniversalApk() {
        val release = entry("v0.2.17")
        val assets = release.getJSONArray("assets")
        assets.put(assets.getJSONObject(0))
        assertThrows(IllegalArgumentException::class.java) { parse(release) }
        release.put("assets", JSONArray())
        assertThrows(IllegalArgumentException::class.java) { parse(release) }
    }
    @Test fun rejectsInvalidSizeAndDigest() {
        listOf(0L, 512L * 1024 * 1024 + 1).forEach {
            val release = entry("v0.2.17")
            release.getJSONArray("assets").getJSONObject(0).put("size", it)
            assertThrows(IllegalArgumentException::class.java) { parse(release) }
        }
        val release = entry("v0.2.17")
        release.getJSONArray("assets").getJSONObject(0).put("digest", "sha256:invalid")
        assertThrows(IllegalArgumentException::class.java) { parse(release) }
    }
    @Test fun boundsNotesAndFeed() {
        assertEquals(4000, parse(entry("v0.2.17").put("body", "x".repeat(6000)))?.notes?.length)
        assertThrows(IllegalArgumentException::class.java) {
            GitHubUpdateService.parse(JSONArray(List(101) { entry("v0.2.17") }).toString(), version("v0.2.16"))
        }
    }
}
