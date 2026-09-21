package com.bluelink.android.data.local
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.nio.file.Files
import com.bluelink.android.domain.*

class PeerPreferencesTest {
    @Test fun restartClearAndIdentitySeparation() {
        val path=File(Files.createTempDirectory("peer-notes").toFile(),"notes.json")
        PeerPreferences(path).update("PEER",note="工作电脑 🌍",pinned=true)
        val store=PeerPreferences(path)
        assertEquals(PeerPreference("工作电脑 🌍",true),store.state.value["peer"])
        assertNull(store.state.value["different-identity"])
        store.update("peer",note=""); assertEquals(PeerPreference("",true),store.state.value["peer"])
        try { store.update("peer",note="x".repeat(65)); fail() } catch (_: IllegalArgumentException) { }
    }
    @Test fun badOptionalMetadataCannotBlockConnectionsOrBeOverwritten() {
        val path=File(Files.createTempDirectory("peer-broken").toFile(),"notes.json"); path.writeText("broken")
        val store=PeerPreferences(path); assertTrue(store.state.value.isEmpty())
        try { store.update("peer",pinned=true); fail() } catch (_: java.io.IOException) { }
        assertEquals("broken",path.readText())
    }
    @Test fun pinsStayWithinGroupsAndRemovedIdentityStaysHidden() {
        val original=ConversationSummary("one","Original",PeerPlatform.WINDOWS,DeviceAvailability.OFFLINE,localNote="Office",isPinned=true)
        val live=original.copy(peerId="two",availability=DeviceAvailability.CONNECTED,isPinned=false,localNote="")
        val removed=original.copy(peerId="revoked",isRemoved=true)
        val projection=DeviceScreenState.project(listOf(original,live,removed),emptyList(),BluetoothAccessState.READY,"")
        assertEquals(listOf("two"),projection.connected.map { it.peerId }); assertEquals(listOf("one"),projection.offline.map { it.peerId })
        assertEquals("Office",original.displayName); assertEquals("Original",original.peerName)
        assertEquals(listOf(original),DeviceScreenState.project(listOf(original),emptyList(),BluetoothAccessState.READY,"office").offline)
        assertEquals(listOf(original),DeviceScreenState.project(listOf(original),emptyList(),BluetoothAccessState.READY,"original").offline)
    }
}
