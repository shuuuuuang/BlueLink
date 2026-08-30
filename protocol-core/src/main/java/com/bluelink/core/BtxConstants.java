package com.bluelink.core;

import java.util.UUID;

public final class BtxConstants {
    public static final int PROTOCOL_MAJOR = 1;
    public static final UUID RFCOMM_SERVICE_UUID = UUID.fromString("9c6c51a8-8f9a-4f67-96aa-2df57177b101");
    public static final UUID BLE_PRESENCE_UUID = UUID.fromString("9c6c51a8-8f9a-4f67-96aa-2df57177b102");
    public static final UUID BLE_RENDEZVOUS_UUID = UUID.fromString("9c6c51a8-8f9a-4f67-96aa-2df57177b103");
    public static final UUID TRANSPORT_OFFER_UUID = UUID.fromString("9c6c51a8-8f9a-4f67-96aa-2df57177b104");
    public static final UUID CONNECT_REQUEST_UUID = UUID.fromString("9c6c51a8-8f9a-4f67-96aa-2df57177b105");
    public static final int MAX_RECORD_SIZE = 1024 * 1024;
    public static final int SEGMENT_SIZE = 64 * 1024;
    public static final int EXTENT_SIZE = 4 * 1024 * 1024;

    private BtxConstants() {}
}
