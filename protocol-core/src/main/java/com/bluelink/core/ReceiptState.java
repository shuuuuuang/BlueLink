package com.bluelink.core;

public enum ReceiptState {
    DELIVERED(1), READ(2), FAILED(3);
    private final int code;
    ReceiptState(int code) { this.code = code; }
    public int code() { return code; }
    public static ReceiptState fromCode(int code) {
        for (ReceiptState value : values()) if (value.code == code) return value;
        throw new IllegalArgumentException("Unknown receipt state: " + code);
    }
}
