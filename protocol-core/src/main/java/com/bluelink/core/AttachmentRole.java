package com.bluelink.core;

public enum AttachmentRole {
    FILE(0), IMAGE_PREVIEW(1), IMAGE_ORIGINAL(2);
    private final int code;
    AttachmentRole(int code) { this.code = code; }
    public int code() { return code; }
    public static AttachmentRole fromCode(int code) {
        for (AttachmentRole value : values()) if (value.code == code) return value;
        throw new IllegalArgumentException("Unknown attachment role: " + code);
    }
}
