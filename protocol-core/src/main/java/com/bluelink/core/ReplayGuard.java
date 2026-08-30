package com.bluelink.core;

public final class ReplayGuard {
    private long nextSequence;

    public ReplayGuard(long initialSequence) {
        if (initialSequence < 0) throw new IllegalArgumentException("initialSequence");
        this.nextSequence = initialSequence;
    }

    synchronized long expected() { return nextSequence; }

    public synchronized void accept(long sequence) {
        if (sequence != nextSequence) {
            throw new SecurityException("Unexpected record sequence " + sequence + ", expected " + nextSequence);
        }
        nextSequence++;
    }
}
