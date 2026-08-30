package com.bluelink.core;

import java.util.Arrays;

public record BtxFrame(WireMessageType type, int flags, int streamId, long sequence, byte[] payload) {
    public BtxFrame {
        if (streamId < 0) throw new IllegalArgumentException("streamId must be non-negative");
        if (sequence < 0) throw new IllegalArgumentException("sequence must be non-negative");
        payload = Arrays.copyOf(payload, payload.length);
    }

    @Override public byte[] payload() { return Arrays.copyOf(payload, payload.length); }
}

