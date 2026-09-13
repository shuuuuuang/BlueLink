package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class DeviceNamePolicyTest {
    @Test fun acceptsMeaningfulUnicodeNames() {
        assertTrue(DeviceNamePolicy.isValid(" 我的蓝联手机 📱 "))
        assertTrue(DeviceNamePolicy.isValid("A".repeat(32)))
    }
    @Test fun rejectsEmptyControlCharactersAndOversizedNames() {
        listOf(" ", "Phone\nName", "A".repeat(33), "蓝".repeat(27)).forEach {
            assertFalse(it, DeviceNamePolicy.isValid(it))
        }
    }
    @Test fun utf8LimitMatchesTransportOfferCapacity() {
        assertTrue(DeviceNamePolicy.isValid("📱".repeat(20)))
        assertFalse(DeviceNamePolicy.isValid("📱".repeat(21)))
    }
}
