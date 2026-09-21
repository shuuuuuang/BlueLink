package com.bluelink.android.service

import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.IBinder
import android.os.PowerManager
import com.bluelink.android.BlueLinkApplication
import com.bluelink.android.domain.BackgroundTransferSummary
import com.bluelink.android.domain.ConnectionPhase
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*

class BluetoothSessionService : Service() {
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var wakeLock: PowerManager.WakeLock? = null
    private var needsCpu = false
    override fun onCreate() {
        super.onCreate()
        val runtime = (application as BlueLinkApplication).runtime
        val notices = BlueLinkNotifications(this)
        startForeground(NOTIFICATION_ID,notices.foreground(runtime.settings.value,0,BackgroundTransferSummary(0,false,null)))
        wakeLock = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"BlueLink:FileTransfer")
            .apply { setReferenceCounted(false) }
        serviceScope.launch {
            combine(runtime.allTransfers,runtime.sessions,runtime.settings) { transfers,sessions,settings ->
                Triple(BackgroundTransferSummary.from(transfers.values),sessions.count { it.phase == ConnectionPhase.CONNECTED },settings)
            }.distinctUntilChanged().collect { (summary,connections,settings) ->
                needsCpu = settings.keepBackgroundSessions && summary.needsCpu
                updateWakeLock()
                getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID,notices.foreground(settings,connections,summary))
            }
        }
        // Renew only while actual file I/O needs CPU. Queued/paused tasks never hold the lock.
        serviceScope.launch { while (isActive) { delay(60_000); updateWakeLock() } }
        runtime.start()
    }
    private fun updateWakeLock() {
        wakeLock?.let { lock ->
            if (needsCpu) lock.acquire(120_000)
            else if (lock.isHeld) lock.release()
        }
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val runtime = (application as BlueLinkApplication).runtime
        runtime.start()
        return if (runtime.keepBackgroundSessionsEnabled()) START_STICKY else START_NOT_STICKY
    }
    override fun onDestroy() {
        serviceScope.cancel(); needsCpu = false; updateWakeLock(); wakeLock = null
        super.onDestroy()
    }
    override fun onBind(intent: Intent?): IBinder? = null
    companion object { private const val NOTIFICATION_ID = 1001 }
}
