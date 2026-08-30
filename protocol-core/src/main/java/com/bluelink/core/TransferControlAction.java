package com.bluelink.core;

public enum TransferControlAction {
    PAUSE(1), RESUME(2), CANCEL(3), RETRY(4);
    private final int code;
    TransferControlAction(int code) { this.code = code; }
    public int code() { return code; }
    public static TransferControlAction fromCode(int code) {
        for (TransferControlAction value : values()) if (value.code == code) return value;
        throw new IllegalArgumentException("Unknown transfer control action: " + code);
    }
}
