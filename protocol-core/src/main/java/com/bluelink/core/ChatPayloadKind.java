package com.bluelink.core;

public enum ChatPayloadKind {
    TEXT(1), IMAGE(2), FILE(3), SYSTEM(4);
    private final int code;
    ChatPayloadKind(int code) { this.code = code; }
    public int code() { return code; }
    public static ChatPayloadKind fromCode(int code) {
        for (ChatPayloadKind value : values()) if (value.code == code) return value;
        throw new IllegalArgumentException("Unknown message kind: " + code);
    }
}
