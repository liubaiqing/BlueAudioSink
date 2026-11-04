package com.blueaudiosink.sender.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import com.blueaudiosink.sender.R
import com.blueaudiosink.sender.core.SenderUiState
import kotlin.math.roundToInt

@Composable
internal fun ReceiverVolumeControl(
    state: SenderUiState,
    onVolumeChange: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    val label = stringResource(R.string.pc_receive_volume)
    val ready = state.pcConnected && state.remoteVolumeReady
    Column(modifier.fillMaxWidth()) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(label, style = MaterialTheme.typography.titleMedium)
            Text(state.remoteVolume?.takeIf { ready }?.let { "$it%" } ?: "—",
                color = MaterialTheme.colorScheme.primary)
        }
        Slider(
            value = (state.remoteVolume?.takeIf { ready } ?: 0).toFloat(),
            onValueChange = { onVolumeChange(it.roundToInt()) },
            valueRange = 0f..100f,
            enabled = ready,
            modifier = Modifier.fillMaxWidth().semantics { contentDescription = label },
        )
        if (!ready) Text(
            stringResource(if (state.pcConnected) R.string.pc_volume_waiting else R.string.pc_volume_disconnected),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
