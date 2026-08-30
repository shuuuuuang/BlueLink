package com.bluelink.core;

import java.util.UUID;

public record ChatReceipt(UUID messageId, ReceiptState state, long timestamp) {}
