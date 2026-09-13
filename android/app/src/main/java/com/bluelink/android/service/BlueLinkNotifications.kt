package com.bluelink.android.service

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import com.bluelink.android.MainActivity
import com.bluelink.android.R
import com.bluelink.android.data.local.AppSettings
import com.bluelink.android.domain.ConnectionPhase
import com.bluelink.android.domain.ManagedSessionState
import com.bluelink.android.domain.TransferItem
import java.util.Locale

/** Optional event notices are separate from the required foreground-service notice. */
internal class BlueLinkNotifications(private val context: Context) {
    private val manager = context.getSystemService(NotificationManager::class.java)

    fun message(state: ManagedSessionState, id: String, settings: AppSettings) {
        if (!settings.messageNotifications) return
        post("message:$id", MESSAGE_CHANNEL, text(settings, "新消息", "新訊息", "New message"), state.peerName, settings)
    }

    fun connection(state: ManagedSessionState, settings: AppSettings) {
        if (!settings.connectionNotifications) return
        val title = when (state.phase) {
            ConnectionPhase.CONNECTED -> text(settings, "设备已连接", "裝置已連線", "Device connected")
            ConnectionPhase.DISCONNECTED -> text(settings, "设备已断开", "裝置已中斷連線", "Device disconnected")
            else -> return
        }
        post("connection:${state.sessionId}", CONNECTION_CHANNEL, title, state.peerName, settings)
    }

    fun completed(transfer: TransferItem, settings: AppSettings) {
        if (!settings.transferNotifications) return
        val title = if (transfer.outgoing) {
            text(settings, "文件发送完成", "檔案傳送完成", "File sent")
        } else text(settings, "文件接收完成", "檔案接收完成", "File received")
        post("transfer:${transfer.id}", TRANSFER_CHANNEL, title, transfer.name, settings)
    }

    fun settingsChanged(settings: AppSettings) {
        manager.activeNotifications.forEach { notice ->
            if ((!settings.messageNotifications && notice.notification.channelId == MESSAGE_CHANNEL) ||
                (!settings.connectionNotifications && notice.notification.channelId == CONNECTION_CHANNEL) ||
                (!settings.transferNotifications && notice.notification.channelId == TRANSFER_CHANNEL)) {
                manager.cancel(notice.tag, notice.id)
            }
        }
    }

    private fun post(tag: String, channel: String, title: String, detail: String, settings: AppSettings) {
        if (context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED ||
            !manager.areNotificationsEnabled()) return
        val channelName = when (channel) {
            MESSAGE_CHANNEL -> text(settings, "消息", "訊息", "Messages")
            CONNECTION_CHANNEL -> text(settings, "连接状态", "連線狀態", "Connection status")
            else -> text(settings, "传输完成", "傳輸完成", "Completed transfers")
        }
        manager.createNotificationChannel(NotificationChannel(channel, channelName, NotificationManager.IMPORTANCE_DEFAULT))
        val open = PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        manager.notify(tag, 1, Notification.Builder(context, channel)
            .setSmallIcon(R.drawable.ic_notification_bluelink)
            .setContentTitle(title).setContentText(detail).setContentIntent(open)
            .setVisibility(Notification.VISIBILITY_PRIVATE).setOnlyAlertOnce(true).setAutoCancel(true).build())
    }

    private fun text(settings: AppSettings, simplified: String, traditional: String, english: String): String {
        val locale = if (settings.language == "system") Locale.getDefault() else Locale.forLanguageTag(settings.language)
        return when {
            locale.language != "zh" -> english
            locale.country in setOf("TW", "HK", "MO") || locale.script == "Hant" -> traditional
            else -> simplified
        }
    }

    private companion object {
        const val MESSAGE_CHANNEL = "bluelink_message_events"
        const val CONNECTION_CHANNEL = "bluelink_connection_events"
        const val TRANSFER_CHANNEL = "bluelink_transfer_events"
    }
}
