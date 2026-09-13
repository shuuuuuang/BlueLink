package com.bluelink.android.domain

object DeviceNamePolicy {
    fun isValid(value: String): Boolean {
        val name = value.trim()
        return name.isNotEmpty() && name.codePointCount(0, name.length) <= 32 &&
            name.toByteArray(Charsets.UTF_8).size <= 80 && name.none { Character.isISOControl(it) }
    }
}
