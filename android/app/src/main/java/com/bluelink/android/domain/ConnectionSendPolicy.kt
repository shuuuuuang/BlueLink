package com.bluelink.android.domain

/** An authenticated USB channel is independent of the Bluetooth radio and permission. */
fun ConnectionState.canSendContent(bluetoothAccess: BluetoothAccessState): Boolean =
    phase == ConnectionPhase.CONNECTED &&
        (transport == SessionTransport.USB || bluetoothAccess.canUseBluetooth)
