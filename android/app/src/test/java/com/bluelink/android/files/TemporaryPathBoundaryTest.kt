package com.bluelink.android.files

import java.io.File
import java.nio.file.Files
import org.junit.Assert.*
import org.junit.Assume.assumeNoException
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class TemporaryPathBoundaryTest {
    @get:Rule val temporary = TemporaryFolder()
    @Test fun missingPrivateCacheIsSafeButTraversalOutsideAppIsRejected() {
        val app = temporary.newFolder("app")
        validateTemporaryPath(File(app,"cache/outgoing/missing.snapshot"),app)
        try { validateTemporaryPath(File(app,"../other/file"),app); fail("Outside app boundary accepted") }
        catch (_: IllegalStateException) { }
    }
    @Test fun systemAliasIsAllowedOnlyAtTheOsBoundaryAndChildLinksAreRejected() {
        val actual = temporary.newFolder("actual")
        val alias = File(temporary.root,"os-alias")
        try { Files.createSymbolicLink(alias.toPath(),actual.toPath()) }
        catch (error: Exception) { assumeNoException(error); return }
        validateTemporaryPath(File(alias,"cache/missing"),alias)
        val outside = temporary.newFolder("outside")
        Files.createSymbolicLink(File(actual,"redirected").toPath(),outside.toPath())
        try { validateTemporaryPath(File(alias,"redirected/file"),alias); fail("Child redirection accepted") }
        catch (_: IllegalStateException) { }
    }
}
