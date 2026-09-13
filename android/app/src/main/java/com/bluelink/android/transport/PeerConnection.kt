package com.bluelink.android.transport

import android.bluetooth.BluetoothSocket
import com.bluelink.android.domain.PeerPlatform
import com.bluelink.android.domain.SessionTransport
import java.io.Closeable
import java.io.InputStream
import java.io.OutputStream

internal interface PeerConnection : Closeable {
    val input: InputStream
    val output: OutputStream
    val name: String
    val address: String
    val platform: PeerPlatform
    val transport: SessionTransport
    val listenerRole: Boolean
    val identityHint: String? get() = com.bluelink.android.domain.PeerIdentityHint.fromAddress(address)
}

internal class BluetoothPeerConnection(private val socket: BluetoothSocket,
    override val name: String, override val address: String, override val platform: PeerPlatform,
    override val listenerRole: Boolean) : PeerConnection {
    override val input get() = socket.inputStream
    override val output get() = socket.outputStream
    override val transport = SessionTransport.BLUETOOTH
    override fun close() = socket.close()
}
