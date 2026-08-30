package com.bluelink.core;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

public record ProtocolGreeting(int major, int minor, int capabilities) {
    public static final int CURRENT_MAJOR = 1;
    public static final int CURRENT_MINOR = 1;

    public ProtocolGreeting {
        if (major < 0 || major > 255 || minor < 0 || minor > 255)
            throw new IllegalArgumentException("Protocol version is out of range");
    }

    public static ProtocolGreeting current() {
        return new ProtocolGreeting(CURRENT_MAJOR, CURRENT_MINOR, BtxCapabilities.CURRENT);
    }

    public static ProtocolGreeting legacy() { return new ProtocolGreeting(1, 0, BtxCapabilities.NONE); }

    public byte[] encode() {
        return ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN)
                .put((byte) major).put((byte) minor).putShort((short) 0).putInt(capabilities).array();
    }

    public static ProtocolGreeting decode(byte[] payload) throws IOException {
        if (payload.length != 4 && payload.length < 8) throw new IOException("Invalid PROTOCOL_HELLO payload");
        ByteBuffer input = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN);
        int major = Byte.toUnsignedInt(input.get());
        int minor = Byte.toUnsignedInt(input.get());
        if (input.getShort() != 0) throw new IOException("Unsupported PROTOCOL_HELLO flags");
        return new ProtocolGreeting(major, minor, payload.length >= 8 ? input.getInt() : BtxCapabilities.NONE);
    }

    public Negotiation negotiate(ProtocolGreeting remote) throws IOException {
        if (major != remote.major) throw new IOException("Unsupported BTX major version " + remote.major);
        return new Negotiation(major, Math.min(minor, remote.minor), capabilities & remote.capabilities);
    }

    public record Negotiation(int major, int minor, int capabilities) {
        public boolean supports(int capability) { return (capabilities & capability) == capability; }
    }
}
