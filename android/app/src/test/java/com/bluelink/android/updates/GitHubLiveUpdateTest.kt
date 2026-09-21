package com.bluelink.android.updates

import com.bluelink.android.BuildConfig
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test

/** Explicitly opted-in JVM HTTPS probe; this is not Android device acceptance. */
class GitHubLiveUpdateTest {
    @Test fun productionMetadataClientReadsPublicRelease() = runBlocking {
        assumeTrue(System.getenv("BLUELINK_LIVE_UPDATE_CHECK") == "true")
        val release = GitHubUpdateService.check(ReleaseVersion(0, 0, 0, null))
        assertNotNull(release)
        assertNotNull(ReleaseVersion.parse(release!!.tag))
        assertTrue(release.pageUrl.startsWith("https://github.com/shuuuuuang/BlueLink/releases/tag/"))
        assertEquals(System.getenv("BLUELINK_RELEASE_TAG") ?: "v" + BuildConfig.VERSION_NAME, BuildConfig.RELEASE_TAG)
        println("Public GitHub Android JVM probe passed: " + release.tag + "; embedded tag=" + BuildConfig.RELEASE_TAG)
    }
}
