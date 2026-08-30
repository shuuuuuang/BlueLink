package com.bluelink.core;

public record TransportPlan(TransportType type, Role localRole, boolean degraded) {
    public enum Role { LISTENER, DIALER }
}

