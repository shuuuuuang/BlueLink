package com.bluelink.core;

import java.util.List;
import java.util.UUID;

public record ChatEnvelope(UUID messageId, ChatPayloadKind kind, long createdAt,
                           String body, List<AttachmentDescriptor> attachments) {}
