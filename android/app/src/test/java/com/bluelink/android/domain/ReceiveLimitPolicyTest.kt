package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class ReceiveLimitPolicyTest {
    @Test fun defaultAndEditedValuesUseBinaryMiB() {
        assertEquals(524288000L, ReceiveLimitPolicy.DEFAULT_BYTES)
        assertEquals(ReceiveLimitPolicy.DEFAULT_BYTES, ReceiveLimitPolicy.parseMiB("500"))
        assertEquals(1073741824L, ReceiveLimitPolicy.parseMiB(" 1024 "))
    }
    @Test fun rejectsInvalidInputAndOverflowWithoutChangingTheLimit() {
        for (input in listOf("", " ", "0", "-1", "+1", "1.5", "1e3", "abc", "9223372036854775807", "999999999999999999999999"))
            assertNull(input, ReceiveLimitPolicy.parseMiB(input))
        val maximum = Long.MAX_VALUE / ReceiveLimitPolicy.MIB
        assertEquals(maximum * ReceiveLimitPolicy.MIB, ReceiveLimitPolicy.parseMiB(maximum.toString()))
        assertNull(ReceiveLimitPolicy.parseMiB((maximum + 1).toString()))
    }
    @Test fun restoresStoredValuesWithoutFloatPrecisionLossAndRoundsLegacyPartialMiBUp() {
        assertEquals("500", ReceiveLimitPolicy.initialMiB(ReceiveLimitPolicy.DEFAULT_BYTES))
        assertEquals("2", ReceiveLimitPolicy.initialMiB(ReceiveLimitPolicy.MIB + 1))
        assertEquals("1", ReceiveLimitPolicy.initialMiB(0))
        assertEquals("8796093022208", ReceiveLimitPolicy.initialMiB(Long.MAX_VALUE))
    }
}
