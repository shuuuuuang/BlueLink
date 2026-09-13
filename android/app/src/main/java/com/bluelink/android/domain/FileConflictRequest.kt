package com.bluelink.android.domain

import java.util.UUID

data class FileConflictRequest(val requestId: UUID = UUID.randomUUID(), val fileName: String)
