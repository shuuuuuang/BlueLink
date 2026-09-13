package com.bluelink.android.data.local

/** Persisted preference migration is independent of database and UI lifecycles. */
internal object SettingsCodec {
    fun decode(values: List<AppSettingEntity>): AppSettings {
        val map = values.associate { it.key to it.value }
        val defaults = AppSettings()
        return AppSettings(
            theme = map["theme"]?.takeIf { it in setOf("system", "light", "dark") } ?: defaults.theme,
            language = map["language"]?.takeIf { it in setOf("zh-CN", "zh-TW", "en", "system") } ?: defaults.language,
            messageNotifications = map.bool("message_notifications", defaults.messageNotifications),
            connectionNotifications = map.bool("connection_notifications", defaults.connectionNotifications),
            transferNotifications = map.bool("transfer_notifications", defaults.transferNotifications),
            autoConnectTrustedDevices = map.bool("auto_connect_trusted", defaults.autoConnectTrustedDevices),
            reconnectAfterDisconnect = map.bool("reconnect_after_disconnect", defaults.reconnectAfterDisconnect),
            usbTransferEnabled = map.bool("usb_transfer_enabled", defaults.usbTransferEnabled),
            allowDiscovery = map.bool("allow_discovery", defaults.allowDiscovery),
            localDeviceName = map["local_device_name"].orEmpty().trim().takeIf { com.bluelink.android.domain.DeviceNamePolicy.isValid(it) }.orEmpty(),
            scanOnStartup = map.bool("scan_on_startup", defaults.scanOnStartup),
            keepBackgroundSessions = map.bool("keep_background_sessions", defaults.keepBackgroundSessions),
            autoDownloadFiles = map.bool("auto_download_files", defaults.autoDownloadFiles),
            autoSaveImages = map.bool("auto_save_images", defaults.autoSaveImages),
            autoSaveOtherAttachments = map.bool("auto_save_other_attachments", defaults.autoSaveOtherAttachments),
            largeFilesOnlyWhileCharging = map.bool("large_files_only_while_charging", defaults.largeFilesOnlyWhileCharging),
            duplicateFilePolicy = map["duplicate_file_policy"]?.takeIf { it in setOf("rename", "ask", "overwrite") } ?: defaults.duplicateFilePolicy,
            receiveSizeLimitEnabled = map.bool("receive_limit_enabled", defaults.receiveSizeLimitEnabled),
            receiveSizeLimitBytes = map.long("receive_limit_bytes", defaults.receiveSizeLimitBytes).coerceAtLeast(0),
            showImageThumbnails = map.bool("show_image_thumbnails", defaults.showImageThumbnails),
            saveChatHistory = map.bool("save_chat_history", defaults.saveChatHistory),
            saveTransferHistory = map.bool("save_transfer_history", defaults.saveTransferHistory),
            diagnosticsEnabled = map.bool("diagnostics_enabled", defaults.diagnosticsEnabled),
            retentionPeriod = map["retention_period"]?.takeIf { it in com.bluelink.android.domain.RecordRetention.choices } ?: defaults.retentionPeriod,
            downloadDirectory = map["download_directory"] ?: defaults.downloadDirectory,
        )
    }

    fun encode(value: AppSettings) = listOf(
        AppSettingEntity("theme", value.theme),
        AppSettingEntity("language", value.language),
        AppSettingEntity("message_notifications", value.messageNotifications.value()),
        AppSettingEntity("connection_notifications", value.connectionNotifications.value()),
        AppSettingEntity("transfer_notifications", value.transferNotifications.value()),
        AppSettingEntity("auto_connect_trusted", value.autoConnectTrustedDevices.value()),
        AppSettingEntity("reconnect_after_disconnect", value.reconnectAfterDisconnect.value()),
        AppSettingEntity("usb_transfer_enabled", value.usbTransferEnabled.value()),
        AppSettingEntity("allow_discovery", value.allowDiscovery.value()),
        AppSettingEntity("local_device_name", value.localDeviceName.trim()),
        AppSettingEntity("scan_on_startup", value.scanOnStartup.value()),
        AppSettingEntity("keep_background_sessions", value.keepBackgroundSessions.value()),
        AppSettingEntity("auto_download_files", value.autoDownloadFiles.value()),
        AppSettingEntity("auto_save_images", value.autoSaveImages.value()),
        AppSettingEntity("auto_save_other_attachments", value.autoSaveOtherAttachments.value()),
        AppSettingEntity("large_files_only_while_charging", value.largeFilesOnlyWhileCharging.value()),
        AppSettingEntity("duplicate_file_policy", value.duplicateFilePolicy),
        AppSettingEntity("receive_limit_enabled", value.receiveSizeLimitEnabled.value()),
        AppSettingEntity("receive_limit_bytes", value.receiveSizeLimitBytes.toString()),
        AppSettingEntity("show_image_thumbnails", value.showImageThumbnails.value()),
        AppSettingEntity("save_chat_history", value.saveChatHistory.value()),
        AppSettingEntity("save_transfer_history", value.saveTransferHistory.value()),
        AppSettingEntity("diagnostics_enabled", value.diagnosticsEnabled.value()),
        AppSettingEntity("retention_period", value.retentionPeriod),
        AppSettingEntity("download_directory", value.downloadDirectory),
    )

    private fun Boolean.value() = if (this) "1" else "0"
    private fun Map<String, String>.bool(key: String, fallback: Boolean) = this[key]?.let { it == "1" || it.equals("true", true) } ?: fallback
    private fun Map<String, String>.long(key: String, fallback: Long) = this[key]?.toLongOrNull() ?: fallback
}
