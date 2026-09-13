package com.bluelink.core;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.HexFormat;
import java.util.List;
import java.util.UUID;

public final class VerificationMain {
    private int checks;

    public static void main(String[] args) throws Exception {
        new VerificationMain().run();
    }

    private void run() throws Exception {
        verifyHandshakeAndRecords();
        verifyProtocol11Payloads();
        verifyRoleAndTransportPolicy();
        verifyScheduler();
        verifySafePaths();
        verifyResumeAndIntegrity();
        System.out.println("BlueLink BTX verification passed: " + checks + " checks");
    }

    private void verifyHandshakeAndRecords() throws Exception {
        DeviceIdentity alice = DeviceIdentity.generate();
        DeviceIdentity bob = DeviceIdentity.generate();
        HandshakeHello aliceLocal = HandshakeHello.create(alice);
        HandshakeHello bobLocal = HandshakeHello.create(bob);
        HandshakeHello aliceRemote = HandshakeHello.decode(aliceLocal.encode());
        HandshakeHello bobRemote = HandshakeHello.decode(bobLocal.encode());
        SessionKeys aliceKeys = aliceLocal.derive(bobRemote);
        SessionKeys bobKeys = bobLocal.derive(aliceRemote);
        check(Arrays.equals(aliceKeys.sendKey(), bobKeys.receiveKey()), "directional send/receive keys");
        check(Arrays.equals(aliceKeys.receiveKey(), bobKeys.sendKey()), "reverse directional keys");
        check(aliceKeys.safetyCode() == bobKeys.safetyCode(), "safety code agreement");

        BtxFrame sent = new BtxFrame(WireMessageType.CHAT, 0, 1, 0,
                "hello over bluetooth".getBytes(StandardCharsets.UTF_8));
        ByteArrayOutputStream wire = new ByteArrayOutputStream();
        BtxRecordCodec.write(wire, sent, aliceKeys.sendKey(), aliceKeys.sendNoncePrefix());
        BtxFrame received = BtxRecordCodec.read(new ByteArrayInputStream(wire.toByteArray()),
                bobKeys.receiveKey(), bobKeys.receiveNoncePrefix(), new ReplayGuard(0));
        check(sent.type() == received.type() && sent.streamId() == received.streamId(), "record header round trip");
        check(Arrays.equals(sent.payload(), received.payload()), "encrypted payload round trip");

        byte[] tampered = wire.toByteArray();
        tampered[tampered.length - 1] ^= 1;
        boolean rejected = false;
        try {
            BtxRecordCodec.read(new ByteArrayInputStream(tampered), bobKeys.receiveKey(),
                    bobKeys.receiveNoncePrefix(), new ReplayGuard(0));
        } catch (Exception expected) {
            rejected = true;
        }
        check(rejected, "AEAD tamper rejection");

        byte[] vectorKey = new byte[32];
        for (int index = 0; index < vectorKey.length; index++) vectorKey[index] = (byte) index;
        ByteArrayOutputStream vector = new ByteArrayOutputStream();
        BtxRecordCodec.write(vector, new BtxFrame(WireMessageType.CHAT, 0, 1, 7,
                "BTX vector".getBytes(StandardCharsets.UTF_8)), vectorKey, 0x11223344);
        check(java.util.HexFormat.of().formatHex(vector.toByteArray()).equals(
                "0000002a9f4673f31c1cefbcfc9f0ce6a1bf3ca2b34897a9e18964c813c4f0fcef14228ac8144be19a0549bb0e1c"),
                "stable BTX/1 encrypted record vector");

        byte[] transferSegment = new byte[BtxConstants.SEGMENT_SIZE + 52];
        new SecureRandom().nextBytes(transferSegment);
        ByteArrayOutputStream segmentWire = new ByteArrayOutputStream();
        BtxRecordCodec.write(segmentWire, new BtxFrame(WireMessageType.TRANSFER_EXTENT, 0, 2, 0,
                transferSegment), aliceKeys.sendKey(), aliceKeys.sendNoncePrefix());
        BtxFrame decodedSegment = BtxRecordCodec.read(new ByteArrayInputStream(segmentWire.toByteArray()),
                bobKeys.receiveKey(), bobKeys.receiveNoncePrefix(), new ReplayGuard(0));
        check(Arrays.equals(transferSegment, decodedSegment.payload()), "64 KiB transfer record round trip");
        check(segmentWire.size() < BtxConstants.MAX_RECORD_SIZE, "64 KiB transfer record stays below maximum");
        check(WireMessageType.fromCode(27) == WireMessageType.TRANSFER_EXTENT_ACK
                && WireMessageType.fromCode(28) == WireMessageType.TRANSFER_COMPLETE
                && WireMessageType.fromCode(29) == WireMessageType.TRANSFER_FAILED,
                "reliable transfer message codes");
    }

    private void verifyProtocol11Payloads() throws Exception {
        check(HexFormat.of().formatHex(ProtocolGreeting.current().encode()).equals("010100000000003f"),
                "BTX/1.1 greeting vector");
        ProtocolGreeting legacy = ProtocolGreeting.decode(new byte[]{1, 0, 0, 0});
        check(ProtocolGreeting.current().negotiate(legacy).minor() == 0
                        && ProtocolGreeting.current().negotiate(legacy).capabilities() == 0,
                "BTX/1.0 capability downgrade");
        var previous = ProtocolGreeting.decode(HexFormat.of().parseHex("010100000000001f"));
        var previousNegotiation = ProtocolGreeting.current().negotiate(previous);
        check(previousNegotiation.capabilities() == 0x1f
                        && !previousNegotiation.supports(BtxCapabilities.MTP_FILES),
                "previous BTX/1.1 peer preserves Bluetooth capabilities without MTP");
        check(WireMessageType.fromCode(33) == WireMessageType.MTP_CONTROL,
                "MTP control message code");

        byte[] hash = new byte[32];
        for (int index = 0; index < hash.length; index++) hash[index] = (byte) index;
        var envelope = new ChatEnvelope(
                UUID.fromString("00112233-4455-6677-8899-aabbccddeeff"), ChatPayloadKind.FILE,
                1_700_000_000_123L, "BTX 1.1",
                List.of(new AttachmentDescriptor(
                        UUID.fromString("11111111-2222-3333-4444-555555555555"),
                        UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                        AttachmentRole.FILE, "report.pdf", "application/pdf", 123_456L, hash)));
        byte[] encoded = MessagePayloadCodec.encode(envelope);
        check(HexFormat.of().formatHex(encoded).equals(
                "424d010300112233445566778899aabbccddeeff0000018bcfe5687b0000000742545820312e310111111111222233334444555555555555aaaaaaaabbbbccccddddeeeeeeeeeeee00000000000001e240000a7265706f72742e706466000f6170706c69636174696f6e2f70646620000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"),
                "stable BTX/1.1 attachment message vector");
        ChatEnvelope decoded = MessagePayloadCodec.decode(encoded);
        check(decoded.messageId().equals(envelope.messageId()) && decoded.attachments().size() == 1
                        && Arrays.equals(decoded.attachments().get(0).sha256(), hash),
                "structured attachment message round trip");

        var receipt = new ChatReceipt(envelope.messageId(), ReceiptState.DELIVERED, 1_700_000_000_321L);
        check(MessagePayloadCodec.decodeReceipt(MessagePayloadCodec.encodeReceipt(receipt)).equals(receipt),
                "message receipt round trip");

        var control = new TransferControl(UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                TransferControlAction.CANCEL, 1_700_000_000_456L, "user canceled");
        byte[] encodedControl = TransferControlCodec.encode(control);
        check(HexFormat.of().formatHex(encodedControl).equals(
                "425401aaaaaaaabbbbccccddddeeeeeeeeeeee030000018bcfe569c8000d757365722063616e63656c6564"),
                "stable BTX/1.1 transfer control vector");
        check(TransferControlCodec.decode(encodedControl).equals(control), "transfer control round trip");

        boolean truncatedRejected = false;
        try { MessagePayloadCodec.decode(Arrays.copyOf(encoded, encoded.length - 1)); }
        catch (Exception expected) { truncatedRejected = true; }
        check(truncatedRejected, "truncated structured message rejection");
        check(WireMessageType.fromCode(32) == WireMessageType.TRANSFER_CONTROL,
                "BTX/1.1 transfer control message code");
    }

    private void verifyRoleAndTransportPolicy() {
        byte[] peerA = new byte[16];
        byte[] peerB = new byte[16];
        byte[] nonceA = new byte[32];
        byte[] nonceB = new byte[32];
        new SecureRandom().nextBytes(peerA);
        new SecureRandom().nextBytes(peerB);
        new SecureRandom().nextBytes(nonceA);
        new SecureRandom().nextBytes(nonceB);
        if (Arrays.equals(peerA, peerB)) peerB[0] ^= 1;
        boolean aListens = RoleElection.localIsListener(peerA, peerB, nonceA, nonceB);
        boolean bListens = RoleElection.localIsListener(peerB, peerA, nonceB, nonceA);
        check(aListens != bListens, "role election has one listener");

        TransportCapabilities android = new TransportCapabilities(Platform.ANDROID, true, true, true,
                true, true, false, 1);
        TransportCapabilities windows = new TransportCapabilities(Platform.WINDOWS, true, true, true,
                false, false, true, 4);
        TransportPlan androidPlan = TransportPolicy.select(android, windows, peerA, peerB, nonceA, nonceB);
        TransportPlan windowsPlan = TransportPolicy.select(windows, android, peerB, peerA, nonceB, nonceA);
        check(androidPlan.type() == TransportType.RFCOMM && androidPlan.localRole() == TransportPlan.Role.DIALER,
                "Android to Windows RFCOMM dialer policy");
        check(windowsPlan.localRole() == TransportPlan.Role.LISTENER, "Windows RFCOMM listener policy");
        TransportPlan coc = TransportPolicy.select(android, android, peerA, peerB, nonceA, nonceB);
        check(coc.type() == TransportType.L2CAP_COC, "Android pair prefers L2CAP CoC");
    }

    private void verifyScheduler() throws Exception {
        PriorityScheduler scheduler = new PriorityScheduler();
        scheduler.offer(new BtxFrame(WireMessageType.TRANSFER_EXTENT, 0, 7, 0, new byte[0]));
        scheduler.offer(new BtxFrame(WireMessageType.TRANSFER_FINISH, 0, 7, 1, new byte[0]));
        scheduler.offer(new BtxFrame(WireMessageType.CHAT, 0, 1, 1, new byte[0]));
        scheduler.offer(new BtxFrame(WireMessageType.WINDOW_UPDATE, 0, 0, 2, new byte[0]));
        check(scheduler.take().type() == WireMessageType.WINDOW_UPDATE, "control bypasses bulk transfer");
        check(scheduler.take().type() == WireMessageType.CHAT, "chat bypasses bulk transfer");
        check(scheduler.take().type() == WireMessageType.TRANSFER_EXTENT
                && scheduler.take().type() == WireMessageType.TRANSFER_FINISH,
                "transfer finish cannot overtake queued extent");
    }

    private void verifySafePaths() throws Exception {
        Path root = Files.createTempDirectory("bluelink-safe-");
        check(SafePaths.resolve(root, "photos/holiday.jpg").startsWith(root), "safe relative path");
        String[] unsafe = {"../secret.txt", "C:/Windows/file", "//server/share", "photos/../secret", "NUL.txt", "name. "};
        for (String candidate : unsafe) {
            boolean rejected = false;
            try { SafePaths.resolve(root, candidate); }
            catch (IllegalArgumentException expected) { rejected = true; }
            check(rejected, "reject path: " + candidate);
        }
    }

    private void verifyResumeAndIntegrity() throws Exception {
        byte[] file = new byte[250_000];
        new SecureRandom().nextBytes(file);
        int extentSize = 64 * 1024;
        Path root = Files.createTempDirectory("bluelink-transfer-");
        byte[] resume;
        try (TransferReceiver receiver = new TransferReceiver(root, "inbox/data.bin", file.length,
                extentSize, TransferReceiver.sha256(file), null)) {
            accept(receiver, file, extentSize, 0);
            accept(receiver, file, extentSize, 2);
            resume = receiver.extentMap().encode();
            check(receiver.extentMap().missing().equals(java.util.List.of(1, 3)), "selective missing extents");
            check(receiver.contiguousCommittedOffset() == extentSize, "contiguous committed offset");
        }
        Path completed;
        try (TransferReceiver receiver = new TransferReceiver(root, "inbox/data.bin", file.length,
                extentSize, TransferReceiver.sha256(file), resume)) {
            accept(receiver, file, extentSize, 1);
            accept(receiver, file, extentSize, 3);
            completed = receiver.finish();
        }
        check(Arrays.equals(file, Files.readAllBytes(completed)), "resume and atomic finalization");

        byte[] replacement = new byte[extentSize + 17];
        new SecureRandom().nextBytes(replacement);
        try (TransferReceiver receiver = new TransferReceiver(root, "inbox/data.bin", replacement.length,
                extentSize, TransferReceiver.sha256(replacement), null)) {
            accept(receiver, replacement, extentSize, 0);
            accept(receiver, replacement, extentSize, 1);
            completed = receiver.finish();
        }
        check(Arrays.equals(replacement, Files.readAllBytes(completed)),
                "fresh transfer replaces an existing destination atomically");

        byte[] restartData = new byte[160_321];
        new SecureRandom().nextBytes(restartData);
        String resumeKey = "restart-test";
        try (TransferReceiver receiver = new TransferReceiver(root, "inbox/restart.bin", restartData.length,
                extentSize, TransferReceiver.sha256(restartData), null, resumeKey)) {
            accept(receiver, restartData, extentSize, 0);
            check(receiver.contiguousCommittedOffset() == extentSize, "durable resume offset before restart");
        }
        try (TransferReceiver receiver = new TransferReceiver(root, "inbox/restart.bin", restartData.length,
                extentSize, TransferReceiver.sha256(restartData), null, resumeKey)) {
            check(receiver.contiguousCommittedOffset() == extentSize, "durable resume offset after restart");
            accept(receiver, restartData, extentSize, 1);
            accept(receiver, restartData, extentSize, 2);
            completed = receiver.finish();
        }
        check(Arrays.equals(restartData, Files.readAllBytes(completed)),
                "durable resume and finalization after receiver restart");

        byte[] providerContent = new byte[549_845];
        new SecureRandom().nextBytes(providerContent);
        boolean tailRejectedWithSizes = false;
        try (TransferReceiver receiver = new TransferReceiver(root, "inbox/provider-size.bin", 549_675,
                extentSize, TransferReceiver.sha256(providerContent), null)) {
            for (int index = 0; index < 8; index++) accept(receiver, providerContent, extentSize, index);
            try {
                accept(receiver, providerContent, extentSize, 8);
            } catch (java.io.IOException expected) {
                tailRejectedWithSizes = expected.getMessage().contains("expected=25387")
                        && expected.getMessage().contains("actual=25557");
            }
        }
        check(tailRejectedWithSizes, "provider metadata mismatch reports exact tail sizes");
    }

    private static void accept(TransferReceiver receiver, byte[] file, int extentSize, int index) throws Exception {
        int start = index * extentSize;
        byte[] extent = Arrays.copyOfRange(file, start, Math.min(file.length, start + extentSize));
        receiver.accept(index, extent, TransferReceiver.sha256(extent));
    }

    private void check(boolean value, String name) {
        if (!value) throw new AssertionError(name);
        checks++;
    }
}
