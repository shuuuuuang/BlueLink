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
    val runtime by lazy { BlueLinkRuntime(this, identityStore, cryptoStartup, localRepository) }
    private val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.IO +
        CoroutineExceptionHandler { _, failure -> CrashReporter.recordNonFatal(this, "ApplicationScope", failure) })

    override fun onCreate() {
        super.onCreate()
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
    }
}

data class CryptoStartup(val ready: Boolean, val detail: String)
