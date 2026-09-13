package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class PeerIdentityHintTest {
    @Test fun normalizesOnlyValidBluetoothAddresses() {
        assertEquals("bt:AABBCCDDEEFF", PeerIdentityHint.bluetooth("aa-bb-cc-dd-ee-ff"))
        assertEquals(PeerIdentityHint.bluetooth("aa:bb:cc:dd:ee:ff"), PeerIdentityHint.bluetooth("AABBCCDDEEFF"))
        listOf("", "Windows PC", "00:00:00:00:00:00", "02:00:00:00:00:00", "FF:FF:FF:FF:FF:FF", "usb:port1").forEach {
            assertNull(PeerIdentityHint.bluetooth(it))
        }
    }
    @Test fun usbHintsRequireAnExplicitBlueLinkHostIdentifier() {
        val id = "0123456789abcdef0123456789abcdef"
        assertEquals("usb-host:${id.uppercase()}", PeerIdentityHint.usbHost("usb:BlueLink:$id"))
        assertNull(PeerIdentityHint.usbHost("usb:BlueLink:"))
        assertNull(PeerIdentityHint.usbHost("usb:Other:$id"))
    }
}
