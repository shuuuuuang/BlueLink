package com.bluelink.android.data.local

import org.junit.Assert.*
import org.junit.Test

class SettingsCodecTest {
    @Test fun legacyPreferencesKeepTheirMeaningAndGainSafeDefaults() {
        val restored = SettingsCodec.decode(listOf(
            AppSettingEntity("auto_download_files", "0"),
            AppSettingEntity("auto_save_images", "0"),
            AppSettingEntity("auto_save_other_attachments", "1"),
            AppSettingEntity("confirm_new_connections", "0")))
        assertFalse(restored.autoDownloadFiles)
        assertFalse(restored.autoSaveImages)
        assertTrue(restored.autoSaveOtherAttachments)
        assertTrue(restored.messageNotifications)
        assertEquals(500L * 1024 * 1024, restored.receiveSizeLimitBytes)
        assertEquals("rename", restored.duplicateFilePolicy)
        assertFalse(SettingsCodec.encode(restored).any { it.key == "confirm_new_connections" })
    }
    @Test fun alignedPreferencesSurviveReload() {
        val chosen = AppSettings(language = "zh-TW", messageNotifications = false, connectionNotifications = false,
            transferNotifications = false, autoConnectTrustedDevices = false, reconnectAfterDisconnect = true,
            allowDiscovery = false, scanOnStartup = false,
            autoDownloadFiles = false, duplicateFilePolicy = "ask", receiveSizeLimitEnabled = false,
            showImageThumbnails = false, saveTransferHistory = false, diagnosticsEnabled = false, retentionPeriod = "7d")
        assertEquals(chosen, SettingsCodec.decode(SettingsCodec.encode(chosen)))
    }
    @Test fun removedConnectionLimitIsIgnoredAndNeverSaved() {
        for (legacy in listOf("1", "4", "8", "999", "invalid")) {
            val restored = SettingsCodec.decode(listOf(AppSettingEntity("max_connections", legacy)))
            assertEquals(AppSettings(), restored)
            assertFalse(SettingsCodec.encode(restored).any { it.key == "max_connections" })
        }
    }
    @Test fun invalidOptionsFallBackWithoutGrantingAdditionalPermissions() {
        val restored = SettingsCodec.decode(listOf(AppSettingEntity("duplicate_file_policy", "unknown"),
            AppSettingEntity("max_connections", "999"), AppSettingEntity("retention_period", "bad")))
        assertEquals("rename", restored.duplicateFilePolicy)
        assertEquals("forever", restored.retentionPeriod)
        assertFalse(restored.usbTransferEnabled)
    }
}
