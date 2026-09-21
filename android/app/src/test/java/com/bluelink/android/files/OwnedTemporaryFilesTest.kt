package com.bluelink.android.files

import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.util.UUID

class OwnedTemporaryFilesTest {
    @get:Rule val temporary = TemporaryFolder()
    @Test fun onlyReleasedUnreferencedOwnedFilesCanBeRemoved() {
        val root = temporary.newFolder(); val id = UUID.randomUUID()
        val owned = File(root,"task.part"); val unknown = File(root,"legacy.part")
        unknown.writeText("user or legacy data")
        OwnedTemporaryFiles.register(owned,id); owned.writeBytes(ByteArray(17))
        assertEquals(0,OwnedTemporaryFiles.collect(root,{false},false).files)
        OwnedTemporaryFiles.release(owned)
        assertEquals(0,OwnedTemporaryFiles.collect(root,{it == id},false).files)
        assertTrue(owned.exists())
        val preview = OwnedTemporaryFiles.collect(root,{false},true)
        assertEquals(17L,preview.bytes); assertEquals(1,preview.files); assertTrue(owned.exists())
        val result = OwnedTemporaryFiles.collect(root,{false},false)
        assertEquals(preview,result); assertFalse(owned.exists()); assertTrue(unknown.exists())
        assertEquals(0,OwnedTemporaryFiles.collect(root,{false},false).files)
    }
    @Test fun cleanupRechecksReferencesAndPreservesMalformedOwnership() {
        val root = temporary.newFolder(); val file = File(root,"test.part")
        OwnedTemporaryFiles.register(file,UUID.randomUUID()); file.writeText("keep")
        OwnedTemporaryFiles.release(file)
        var checks = 0
        assertEquals(0,OwnedTemporaryFiles.collect(root,{ ++checks > 1 },false).files)
        assertTrue(file.exists())
        File(root,"test.part.owner.json").writeText("{broken")
        assertEquals(1,OwnedTemporaryFiles.collect(root,{false},false).errors)
        assertTrue(file.exists())
    }
    @Test fun registrationCannotClaimAnExistingFile() {
        val file = temporary.newFile(); file.writeText("original")
        try { OwnedTemporaryFiles.register(file,UUID.randomUUID()); fail() } catch (_: IllegalStateException) { }
        assertEquals("original",file.readText())
    }
}
