package com.bluelink.core;

import java.util.ArrayList;
import java.util.BitSet;
import java.util.List;

public final class ExtentMap {
    private final int extentCount;
    private final BitSet completed;

    public ExtentMap(int extentCount) {
        if (extentCount < 0) throw new IllegalArgumentException("extentCount");
        this.extentCount = extentCount;
        this.completed = new BitSet(extentCount);
    }

    public static ExtentMap decode(int extentCount, byte[] bitmap) {
        ExtentMap map = new ExtentMap(extentCount);
        BitSet decoded = BitSet.valueOf(bitmap);
        if (decoded.length() > extentCount) throw new IllegalArgumentException("Bitmap has out-of-range extents");
        map.completed.or(decoded);
        return map;
    }

    public synchronized void complete(int index) { check(index); completed.set(index); }
    public synchronized boolean isComplete(int index) { check(index); return completed.get(index); }
    public synchronized boolean allComplete() { return completed.cardinality() == extentCount; }
    public synchronized byte[] encode() { return completed.toByteArray(); }

    public synchronized List<Integer> missing() {
        List<Integer> result = new ArrayList<>();
        for (int i = 0; i < extentCount; i++) if (!completed.get(i)) result.add(i);
        return result;
    }

    public synchronized int contiguousCount() {
        int index = 0;
        while (index < extentCount && completed.get(index)) index++;
        return index;
    }

    private void check(int index) {
        if (index < 0 || index >= extentCount) throw new IndexOutOfBoundsException(index);
    }
}

