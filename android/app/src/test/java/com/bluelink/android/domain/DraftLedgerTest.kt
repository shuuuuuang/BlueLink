package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class DraftLedgerTest {
    @Test fun draftsRetainExactTextAndStayWithTheirPeer() {
        val ledger = DraftLedger()
        ledger.load(mapOf("PEER-A" to "已有草稿\n  "))
        assertEquals("已有草稿\n  ", ledger.get("peer-a").text)
        ledger.edit("peer-b", "👩‍💻 second")
        assertEquals("已有草稿\n  ", ledger.get("PEER-A").text)
        assertEquals("👩‍💻 second", ledger.pending().single().text)
    }
    @Test fun lateSendDoesNotEraseNewerEditsEvenIfTextMatchesAgain() {
        val ledger = DraftLedger()
        val sending = ledger.edit("peer", "A")
        ledger.edit("peer", "B"); ledger.edit("peer", "A")
        assertFalse(ledger.clearAfterSend(sending))
        assertEquals("A", ledger.get("peer").text)
        assertTrue(ledger.clearAfterSend(ledger.get("peer")))
        assertEquals("", ledger.pending().single().text)
    }
    @Test fun staleDiskAcknowledgmentDoesNotDiscardDirtyDraft() {
        val ledger = DraftLedger()
        val first = ledger.edit("peer", "first")
        ledger.edit("peer", "second")
        ledger.acknowledge(first)
        assertEquals("second", ledger.pending().single().text)
        ledger.acknowledge(ledger.get("peer"))
        assertTrue(ledger.pending().isEmpty())
    }
    @Test fun restartLoadsSavedDraftAndOldSessionCannotClearIt() {
        val ledger = DraftLedger()
        val old = ledger.edit("peer", "draft")
        ledger.load(mapOf("peer" to "draft"))
        assertEquals("draft", ledger.get("peer").text)
        assertFalse(ledger.clearAfterSend(old))
        assertTrue(ledger.pending().isEmpty())
    }
    @Test fun privacyClearKeepsOtherPeerAndPreventsStaleSendClear() {
        val ledger = DraftLedger()
        ledger.load(mapOf("a" to "A", "b" to "B"))
        val old = ledger.get("a")
        ledger.clear("a")
        assertEquals("B", ledger.get("b").text)
        assertFalse(ledger.clearAfterSend(old))
        ledger.clear()
        assertTrue(ledger.texts().values.all { it.isEmpty() })
    }
}
