package com.bluelink.android.domain

enum class DeviceAction { PIN, NOTE, OPEN, CONNECT, INFO, TRANSFERS, PAUSE, RESUME, DISCONNECT, CLEAR, REMOVE_TRUST }

/** The card and its menu must refer to the same task, including while it is paused. */
object DeviceActions {
    fun transfer(peerId: String, items: Collection<TransferItem>): TransferItem? = items
        .filter { it.peerId == peerId && it.status in HistoryQuery.activeStatuses && it.role != com.bluelink.core.AttachmentRole.IMAGE_PREVIEW }
        .minWithOrNull(compareBy<TransferItem> { it.startedAtEpochMs }.thenBy { it.id.toString() })

    fun transferCount(peerId: String, items: Collection<TransferItem>): Int = items.count {
        it.peerId == peerId && it.status in HistoryQuery.activeStatuses && it.role != com.bluelink.core.AttachmentRole.IMAGE_PREVIEW
    }

    fun available(peer: ConversationSummary, transfer: TransferItem?, canConnect: Boolean): List<DeviceAction> = buildList {
        add(DeviceAction.OPEN)
        if (!peer.isRemoved) { add(DeviceAction.PIN); add(DeviceAction.NOTE) }
        val active = transfer?.takeIf { it.peerId == peer.peerId && it.status in HistoryQuery.activeStatuses }
        if (peer.availability == DeviceAvailability.CONNECTED && active != null) {
            add(DeviceAction.TRANSFERS)
            val actions = TransferActions.available(active)
            if (TransferAction.PAUSE in actions) add(DeviceAction.PAUSE)
            if (TransferAction.RESUME in actions) add(DeviceAction.RESUME)
            add(DeviceAction.DISCONNECT)
        } else {
            if (peer.availability != DeviceAvailability.CONNECTED && canConnect) add(DeviceAction.CONNECT)
            add(DeviceAction.INFO)
            add(DeviceAction.CLEAR)
            if (peer.isTrusted) add(DeviceAction.REMOVE_TRUST)
        }
    }

    // Windows may advertise only the service UUID when optional service data does not fit.
    // The UUID permits a connection attempt; Transport Offer and the secure handshake still verify the peer.
    fun canConnect(device: NearbyDevice): Boolean = device.platform != PeerPlatform.ANDROID &&
        device.rendezvousAvailable && device.connectable
}
