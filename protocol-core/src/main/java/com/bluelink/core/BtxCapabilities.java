package com.bluelink.core;

public final class BtxCapabilities {
    public static final int NONE = 0;
    public static final int STRUCTURED_MESSAGES = 1 << 0;
    public static final int MESSAGE_RECEIPTS = 1 << 1;
    public static final int ATTACHMENT_METADATA = 1 << 2;
    public static final int TRANSFER_CONTROL = 1 << 3;
    public static final int RESUME_STATE = 1 << 4;
    public static final int MTP_FILES = 1 << 5;
    public static final int TRANSFER_ATTEMPT_STREAMS = 1 << 6;
    public static final int CURRENT = STRUCTURED_MESSAGES | MESSAGE_RECEIPTS | ATTACHMENT_METADATA |
            TRANSFER_CONTROL | RESUME_STATE | MTP_FILES | TRANSFER_ATTEMPT_STREAMS;

    private BtxCapabilities() {}
}
