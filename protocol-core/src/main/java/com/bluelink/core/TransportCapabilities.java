package com.bluelink.core;

public record TransportCapabilities(Platform platform, boolean gattControl, boolean rfcommDial,
        boolean rfcommListen, boolean l2capDial, boolean l2capListen,
        boolean exportClassicAddress, int maxConcurrentSessions) {}

