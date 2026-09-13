package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class ConnectionSendPolicyTest {
    @Test fun authenticatedUsbRemainsUsableWithoutBluetooth() {
        val connection = ConnectionState(ConnectionPhase.CONNECTED, transport = SessionTransport.USB)
        BluetoothAccessState.entries.forEach { assertTrue(connection.canSendContent(it)) }
    }
    @Test fun usbCannotSendBeforeAuthenticationOrAfterDisconnect() {
        ConnectionPhase.entries.filter { it != ConnectionPhase.CONNECTED }.forEach { phase ->
            BluetoothAccessState.entries.forEach { access ->
                assertFalse(ConnectionState(phase, transport = SessionTransport.USB).canSendContent(access))
            }
        }
    }
    @Test fun bluetoothNeedsBothAuthenticatedConnectionAndBluetoothAccess() {
        val connected = ConnectionState(ConnectionPhase.CONNECTED, transport = SessionTransport.BLUETOOTH)
        assertTrue(connected.canSendContent(BluetoothAccessState.READY))
        listOf(BluetoothAccessState.OFF, BluetoothAccessState.REQUIRED, BluetoothAccessState.DENIED).forEach {
            assertFalse(connected.canSendContent(it))
        }
        assertFalse(connected.copy(phase = ConnectionPhase.DISCONNECTED).canSendContent(BluetoothAccessState.READY))
    }
}
