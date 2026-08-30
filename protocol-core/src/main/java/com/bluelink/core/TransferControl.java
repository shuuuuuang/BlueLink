package com.bluelink.core;

import java.util.UUID;

public record TransferControl(UUID transferId, TransferControlAction action, long timestamp, String reason) {}
