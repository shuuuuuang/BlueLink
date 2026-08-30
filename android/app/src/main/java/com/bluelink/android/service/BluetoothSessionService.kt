package com.bluelink.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import com.bluelink.android.BlueLinkApplication
import com.bluelink.android.MainActivity

class BluetoothSessionService : Service() {
    override fun onCreate() {
        super.onCreate()
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL_ID, "蓝联连接", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(com.bluelink.android.R.drawable.ic_notification_bluelink)
            .setContentTitle("蓝联正在运行")
            .setContentText("可接收附近已配对设备的聊天和文件")
            .setContentIntent(open)
            .setOngoing(true)
            .build()
        startForeground(NOTIFICATION_ID, notification)
        (application as BlueLinkApplication).runtime.start()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val runtime = (application as BlueLinkApplication).runtime
        runtime.start()
        return if (runtime.keepBackgroundSessionsEnabled()) START_STICKY else START_NOT_STICKY
    }
    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        private const val CHANNEL_ID = "bluelink_session"
        private const val NOTIFICATION_ID = 1001
    }
}
