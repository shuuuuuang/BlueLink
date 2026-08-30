package com.bluelink.core;

import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Arrays;

public final class RoleElection {
    private RoleElection() {}

    public static boolean localIsListener(byte[] localPeer, byte[] remotePeer,
                                          byte[] localNonce, byte[] remoteNonce) {
        if (localPeer.length != 16 || remotePeer.length != 16) throw new IllegalArgumentException("Peer id must be 16 bytes");
        int comparison = Arrays.compareUnsigned(localPeer, remotePeer);
        if (comparison == 0) throw new IllegalArgumentException("Peer ids must differ");
        byte[] smaller = comparison < 0 ? localPeer : remotePeer;
        byte[] larger = comparison < 0 ? remotePeer : localPeer;
        byte[] smallerNonce = comparison < 0 ? localNonce : remoteNonce;
        byte[] largerNonce = comparison < 0 ? remoteNonce : localNonce;
        byte[] seed;
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            digest.update(smaller);
            digest.update(larger);
            digest.update(smallerNonce);
            digest.update(largerNonce);
            seed = digest.digest();
        } catch (NoSuchAlgorithmException impossible) {
            throw new IllegalStateException(impossible);
        }
        boolean smallerListens = (seed[seed.length - 1] & 1) == 0;
        return comparison < 0 == smallerListens;
    }
}

