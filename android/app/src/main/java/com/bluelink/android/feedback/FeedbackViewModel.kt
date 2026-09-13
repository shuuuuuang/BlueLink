package com.bluelink.android.feedback

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.bluelink.android.domain.DiagnosticEntry
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

data class FeedbackState(
    val type: FeedbackType = FeedbackType.CONNECTION,
    val description: String = "",
    val includeDiagnostics: Boolean = false,
    val invalid: Boolean = false,
    val generating: Boolean = false,
    val failed: Boolean = false,
    val packageFile: File? = null,
)

class FeedbackViewModel : ViewModel() {
    private val mutable = MutableStateFlow(FeedbackState())
    val state = mutable.asStateFlow()

    fun edit(type: FeedbackType = mutable.value.type, description: String = mutable.value.description,
             includeDiagnostics: Boolean = mutable.value.includeDiagnostics) {
        if (mutable.value.generating) return
        mutable.value = FeedbackState(type, description.take(1000), includeDiagnostics)
    }

    fun generate(directory: File, version: String, api: Int, diagnostics: () -> List<DiagnosticEntry>) {
        val current = mutable.value
        if (current.generating) return
        val request = FeedbackRequest(current.type, current.description, current.includeDiagnostics)
        if (!request.isValid) { mutable.value = current.copy(invalid = true); return }
        mutable.value = current.copy(generating = true, failed = false, packageFile = null)
        viewModelScope.launch {
            try {
                val file = withContext(Dispatchers.IO) { FeedbackPackage.create(directory, request, version, api, diagnostics) }
                mutable.value = current.copy(packageFile = file)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                mutable.value = current.copy(failed = true)
            }
        }
    }
}
