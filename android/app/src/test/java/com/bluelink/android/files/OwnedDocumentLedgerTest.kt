package com.bluelink.android.files
import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files
import java.io.File
import java.util.UUID

class OwnedDocumentLedgerTest {
    @Test fun cleanupNeverDeletesActiveReferencedUnknownOrUnreadableObjects() {
        val root=Files.createTempDirectory("owned-document").toFile(); val path=File(root,"index.json")
        val live=OwnedDocumentLedger(path); val task=UUID.randomUUID()
        live.begin("epoch","tree","directory"); live.add("epoch","owned",task)
        val objects=mutableMapOf("owned" to 13L,"unknown" to 17L)
        fun collect(ledger: OwnedDocumentLedger, reference: Boolean=false, preview: Boolean=false)=ledger.collect(preview,{reference},{objects[it]},
            { objects.remove(it)!=null },{_,_->objects.isEmpty()},0)
        assertEquals(0,collect(live).files)
        val restart=OwnedDocumentLedger(path)
        assertEquals(0,collect(restart,true).files)
        assertEquals(13L,collect(restart,preview=true).bytes)
        assertEquals(2,objects.size)
        assertEquals(1,restart.collect(false,{false},{throw SecurityException()},{error("must not delete")},{_,_->false},0).errors)
        assertEquals(1,collect(restart).files)
        assertEquals(mapOf("unknown" to 17L),objects)
        assertTrue(path.readText().contains("directory"))
    }
}
