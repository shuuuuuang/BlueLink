package com.bluelink.core;

import java.util.UUID;

public record AttachmentDescriptor(UUID attachmentId, UUID transferId, AttachmentRole role,
                                   String fileName, String mimeType, long size, byte[] sha256) {}
