package com.bluelink.android.ui.devices

import androidx.compose.runtime.*
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import com.bluelink.android.R
import com.bluelink.android.domain.ConversationSummary
import com.bluelink.android.ui.components.BlueLinkPrompt

@Composable internal fun PeerNotePrompt(peer: ConversationSummary,dismiss: ()->Unit,save: (String)->Unit) {
    var note by remember(peer.peerId) { mutableStateOf(peer.localNote) }
    BlueLinkPrompt(stringResource(R.string.peer_note),dismiss,footer={
        TextButton(onClick=dismiss) { Text(stringResource(R.string.cancel)) }
        Button(onClick={save(note)}) { Text(stringResource(R.string.peer_save)) }
    }) {
        Text(stringResource(R.string.peer_note_help),color=DeviceColors.Secondary)
        OutlinedTextField(note,{ value -> if(value.length<=64 && value.none { it.isISOControl() }) note=value },
            modifier=Modifier.fillMaxWidth(),singleLine=true,label={ Text(stringResource(R.string.peer_note)) },
            supportingText={ Text("${note.length}/64") })
    }
}
