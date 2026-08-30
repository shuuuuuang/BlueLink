package com.bluelink.core;

public enum WireMessageType {
    PROTOCOL_HELLO(1, 0), SESSION_READY(2, 0), PING(3, 1), PONG(4, 1), GOAWAY(5, 0),
    CHAT(10, 1), CHAT_RECEIPT(11, 1), TRANSFER_OFFER(20, 2), TRANSFER_ACCEPT(21, 2),
    TRANSFER_REJECT(22, 2), TRANSFER_EXTENT(23, 5), TRANSFER_FINISH(24, 5),
    RESUME_QUERY(25, 2), RESUME_STATE(26, 2), TRANSFER_EXTENT_ACK(27, 2),
    TRANSFER_COMPLETE(28, 2), TRANSFER_FAILED(29, 2), WINDOW_UPDATE(30, 0), STREAM_CANCEL(31, 0),
    TRANSFER_CONTROL(32, 0);

    private final int code;
    private final int priority;

    WireMessageType(int code, int priority) {
        this.code = code;
        this.priority = priority;
    }

    public int code() { return code; }
    public int priority() { return priority; }

    public static WireMessageType fromCode(int code) {
        for (WireMessageType type : values()) if (type.code == code) return type;
        throw new IllegalArgumentException("Unknown BTX message type: " + code);
    }
}
