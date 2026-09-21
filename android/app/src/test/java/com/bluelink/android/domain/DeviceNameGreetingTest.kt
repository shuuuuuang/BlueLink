package com.bluelink.android.domain
import com.bluelink.core.DeviceNameGreeting
import com.bluelink.core.ProtocolGreeting
import org.junit.Assert.*
import org.junit.Test
class DeviceNameGreetingTest {
    @Test fun sharesUnicodeVectorWithWindowsAndKeepsLegacyDecoderCompatible() {
        val golden = "010100000000007f424c444e0011e9aa8ce694b6e6898be69cba20f09f93b1".chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        assertArrayEquals(golden, DeviceNameGreeting.encode("验收手机 📱"))
        assertEquals("验收手机 📱", DeviceNameGreeting.decode(golden))
        assertEquals(ProtocolGreeting.current(), ProtocolGreeting.decode(golden))
    }
    @Test fun oldPeersAndMalformedExtensionsLeaveTheExistingNameAvailable() {
        val valid = DeviceNameGreeting.encode("Android")
        listOf(byteArrayOf(1,0,0,0), ProtocolGreeting.current().encode(), valid.copyOf(valid.size - 1),
            byteArrayOf(1,1,0,0,0,0,0,31,66,76,68,78,0,1,-1)).forEach { assertEquals("", DeviceNameGreeting.decode(it)) }
    }
    @Test fun invalidLocalNamesCannotEmitControlCharactersOrOversizedMetadata() {
        listOf(" ", "x".repeat(385), "a\nb").forEach { assertArrayEquals(ProtocolGreeting.current().encode(), DeviceNameGreeting.encode(it)) }
    }
}
