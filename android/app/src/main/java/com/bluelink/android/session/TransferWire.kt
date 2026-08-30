package com.bluelink.android.session

import com.bluelink.core.AttachmentRole
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

internal data class FileOffer(
    val id: UUID,
    val name: String,
    val size: Long,
    val extentSize: Int,
    val hash: ByteArray,
    val messageId: UUID? = null,
    val attachmentId: UUID? = null,
    val mimeType: String = "application/octet-stream",
    val role: AttachmentRole = AttachmentRole.FILE,
) {
    val hasAttachmentMetadata: Boolean get() = messageId != null && attachmentId != null
}
internal data class FileExtent(val id: UUID, val index: Int, val hash: ByteArray, val data: ByteArray)
internal data class FileExtentAck(val id: UUID, val index: Int)
internal data class FileTransferAccept(val id: UUID, val nextExtent: Int)
internal data class FileTransferFailure(val id: UUID, val reason: String)

internal object TransferWire {
    fun offer(value: FileOffer): ByteArray = ByteArrayOutputStream().also { bytes ->
        DataOutputStream(bytes).use { output ->
            if (value.hasAttachmentMetadata) {
                output.write(byteArrayOf('B'.code.toByte(), 'O'.code.toByte(), 1))
                output.writeUuid(value.id)
                output.writeUuid(requireNotNull(value.messageId))
                output.writeUuid(requireNotNull(value.attachmentId))
                output.writeByte(value.role.code())
                output.writeLong(value.size)
                output.writeInt(value.extentSize)
                require(value.hash.size == 32)
                output.write(value.hash)
                output.writeUtf8(value.name)
                output.writeUtf8(value.mimeType, 255)
            } else {
                output.writeUuid(value.id)
                output.writeUtf8(value.name)
                output.writeLong(value.size)
                output.writeInt(value.extentSize)
                require(value.hash.size == 32)
                output.write(value.hash)
            }
        }
    }.toByteArray()

    fun readOffer(payload: ByteArray): FileOffer = DataInputStream(ByteArrayInputStream(payload)).use { input ->
        if (payload.size >= 3 && payload[0] == 'B'.code.toByte() && payload[1] == 'O'.code.toByte()) {
            require(input.readUnsignedByte() == 'B'.code && input.readUnsignedByte() == 'O'.code && input.readUnsignedByte() == 1)
            val id = input.readUuid()
            val messageId = input.readUuid()
            val attachmentId = input.readUuid()
            val role = AttachmentRole.fromCode(input.readUnsignedByte())
            val size = input.readLong()
            val extentSize = input.readInt()
            val hash = input.readNBytes(32)
            val name = input.readUtf8(512)
            val mime = input.readUtf8(255)
            require(size >= 0 && extentSize in 1..(768 * 1024) && hash.size == 32 && input.available() == 0)
            return@use FileOffer(id, name, size, extentSize, hash, messageId, attachmentId, mime, role)
        }
        val id = input.readUuid()
        val name = input.readUtf8(512)
        val size = input.readLong()
        val extentSize = input.readInt()
        val hash = input.readNBytes(32)
        require(size >= 0 && extentSize in 1..(768 * 1024) && hash.size == 32)
        FileOffer(id, name, size, extentSize, hash)
    }

    fun id(value: UUID): ByteArray = ByteBuffer.allocate(16).order(ByteOrder.BIG_ENDIAN)
        .putLong(value.mostSignificantBits).putLong(value.leastSignificantBits).array()

    fun readId(payload: ByteArray): UUID {
        require(payload.size >= 16)
        return ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN).let { UUID(it.long, it.long) }
    }

    fun accept(value: FileTransferAccept): ByteArray {
        require(value.nextExtent >= 0)
        return ByteBuffer.allocate(20).order(ByteOrder.BIG_ENDIAN)
            .putLong(value.id.mostSignificantBits).putLong(value.id.leastSignificantBits)
            .putInt(value.nextExtent).array()
    }

    fun readAccept(payload: ByteArray): FileTransferAccept {
        require(payload.size == 16 || payload.size == 20)
        return ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN).let {
            val value = FileTransferAccept(UUID(it.long, it.long), if (payload.size == 20) it.int else 0)
            require(value.nextExtent >= 0)
            value
        }
    }

    fun extentAck(value: FileExtentAck): ByteArray = ByteBuffer.allocate(20).order(ByteOrder.BIG_ENDIAN)
        .putLong(value.id.mostSignificantBits).putLong(value.id.leastSignificantBits)
        .putInt(value.index).array()

    fun readExtentAck(payload: ByteArray): FileExtentAck {
        require(payload.size == 20)
        return ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN).let {
            val value = FileExtentAck(UUID(it.long, it.long), it.int)
            require(value.index >= 0)
            value
        }
    }

    fun failure(value: FileTransferFailure): ByteArray = ByteArrayOutputStream().also { bytes ->
        DataOutputStream(bytes).use { output ->
            output.writeUuid(value.id)
            output.writeUtf8(value.reason.take(256))
        }
    }.toByteArray()

    fun readFailure(payload: ByteArray): FileTransferFailure = DataInputStream(ByteArrayInputStream(payload)).use { input ->
        val id = input.readUuid()
        val reason = input.readUtf8(512)
        require(input.available() == 0)
        FileTransferFailure(id, reason)
    }

    fun extent(value: FileExtent): ByteArray = ByteArrayOutputStream().also { bytes ->
        DataOutputStream(bytes).use { output ->
            output.writeUuid(value.id)
            output.writeInt(value.index)
            require(value.hash.size == 32)
            output.write(value.hash)
            output.write(value.data)
        }
    }.toByteArray()

    fun readExtent(payload: ByteArray): FileExtent = DataInputStream(ByteArrayInputStream(payload)).use { input ->
        val id = input.readUuid()
        val index = input.readInt()
        val hash = input.readNBytes(32)
        val data = input.readAllBytes()
        require(index >= 0 && hash.size == 32)
        FileExtent(id, index, hash, data)
    }

    private fun DataOutputStream.writeUuid(id: UUID) {
        writeLong(id.mostSignificantBits)
        writeLong(id.leastSignificantBits)
    }

    private fun DataInputStream.readUuid() = UUID(readLong(), readLong())

    private fun DataOutputStream.writeUtf8(value: String, max: Int = 512) {
        val encoded = value.toByteArray(Charsets.UTF_8)
        require(encoded.size <= max)
        writeShort(encoded.size)
        write(encoded)
    }

    private fun DataInputStream.readUtf8(max: Int): String {
        val length = readUnsignedShort()
        require(length <= max)
        return readNBytes(length).toString(Charsets.UTF_8)
    }
}
