package com.bluelink.android

import android.app.Application
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.data.local.BlueLinkDatabase
import com.bluelink.android.data.local.BlueLinkRepository
import com.bluelink.android.diagnostics.CrashReporter
import com.bluelink.android.runtime.BlueLinkRuntime
import com.bluelink.core.CryptoRuntime
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import org.conscrypt.Conscrypt

class BlueLinkApplication : Application() {
    lateinit var cryptoStartup: CryptoStartup
        private set
    val identityStore by lazy { IdentityStore(this) }
    val database by lazy { BlueLinkDatabase.open(this) }
    val localRepository by lazy { BlueLinkRepository(database) }
    val shareInbox by lazy { com.bluelink.android.sharing.ShareInbox(this) }
    val shareScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    val runtime by lazy { BlueLinkRuntime(this, identityStore, cryptoStartup, localRepository) }
    private val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.IO +
        CoroutineExceptionHandler { _, failure -> CrashReporter.recordNonFatal(this, "ApplicationScope", failure) })

    private val startedClients = mutableSetOf<android.app.Activity>()
    fun clientStarted(activity: android.app.Activity) {
        if(startedClients.add(activity) && startedClients.size==1) runtime.onAppForegrounded()
    }
    fun clientStopped(activity: android.app.Activity): Boolean {
        startedClients.remove(activity)
        if(startedClients.isNotEmpty() || activity.isChangingConfigurations) return false
        runtime.onAppBackgrounded()
        if(!runtime.keepBackgroundSessionsEnabled()) stopService(android.content.Intent(this,com.bluelink.android.service.BluetoothSessionService::class.java))
        return true
    }

    override fun onCreate() {
        super.onCreate()
        com.bluelink.android.files.OwnedTemporaryFiles.configurePrivateRoot(dataDir)
        CrashReporter.install(this)
        cryptoStartup = try {
            CryptoRuntime.preferProvider(Conscrypt.newProvider())
            CryptoStartup(ready = true, detail = CryptoRuntime.selfTest())
        } catch (failure: Throwable) {
            CryptoStartup(
                ready = false,
                detail = "${failure.javaClass.simpleName}: ${failure.message ?: "未知加密初始化错误"}",
            )
        }
        applicationScope.launch { localRepository.initialize(identityStore) }
        applicationScope.launch { shareInbox.load() }
    }
}

data class CryptoStartup(val ready: Boolean, val detail: String)
