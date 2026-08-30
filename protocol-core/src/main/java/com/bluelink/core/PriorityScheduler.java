package com.bluelink.core;

import java.util.concurrent.PriorityBlockingQueue;
import java.util.concurrent.atomic.AtomicLong;

public final class PriorityScheduler {
    private final AtomicLong ordinal = new AtomicLong();
    private final PriorityBlockingQueue<QueuedFrame> queue = new PriorityBlockingQueue<>();

    public void offer(BtxFrame frame) {
        queue.offer(new QueuedFrame(frame.type().priority(), ordinal.getAndIncrement(), frame));
    }

    public BtxFrame take() throws InterruptedException { return queue.take().frame(); }
    public int size() { return queue.size(); }

    private record QueuedFrame(int priority, long ordinal, BtxFrame frame) implements Comparable<QueuedFrame> {
        @Override public int compareTo(QueuedFrame other) {
            int byPriority = Integer.compare(priority, other.priority);
            return byPriority != 0 ? byPriority : Long.compare(ordinal, other.ordinal);
        }
    }
}

