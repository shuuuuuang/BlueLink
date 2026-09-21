package com.bluelink.android.files

import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class LocalStorageInventoryTest {
    @get:Rule val temporary = TemporaryFolder()
    @Test fun nestedRootsCountEachFileOnceAndClassifyUsbSeparately() {
        val root = temporary.newFolder()
        val cache = File(root,"cache/outgoing").also { it.mkdirs() }
        File(root,"db").writeBytes(ByteArray(3))
        File(cache,"file.snapshot").writeBytes(ByteArray(5))
        File(cache,"file.blm").writeBytes(ByteArray(7))
        val locations = listOf(StorageLocation(root,StorageCategory.OTHER), StorageLocation(cache,StorageCategory.SNAPSHOTS))
        val result = LocalStorageInventory.scan(locations + locations)
        assertEquals(15L,result.bytes.values.sum())
        assertEquals(3L,result.bytes[StorageCategory.OTHER])
        assertEquals(5L,result.bytes[StorageCategory.SNAPSHOTS])
        assertEquals(7L,result.bytes[StorageCategory.USB_STAGING])
        assertFalse(result.partial)
    }
    @Test fun inaccessibleOrMissingRootIsNotReportedAsKnownZero() {
        val result = LocalStorageInventory.scan(listOf(StorageLocation(File(temporary.root,"missing"),StorageCategory.RECEIVED)))
        assertTrue(result.partial)
    }
    @Test fun inventoryHonorsCancellation() {
        try { LocalStorageInventory.scan(listOf(StorageLocation(temporary.root,StorageCategory.OTHER))) { throw java.util.concurrent.CancellationException() }; fail() }
        catch (_: java.util.concurrent.CancellationException) { }
    }
}
