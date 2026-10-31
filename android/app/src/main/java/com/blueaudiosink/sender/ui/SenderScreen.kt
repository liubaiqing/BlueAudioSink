package com.blueaudiosink.sender.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Slider
import kotlin.math.roundToInt
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.blueaudiosink.sender.R
import com.blueaudiosink.sender.capture.BufferTier
import com.blueaudiosink.sender.core.LinkPhase
import com.blueaudiosink.sender.core.SenderUiState
import com.blueaudiosink.sender.net.PacketCodec
import java.util.Locale

/**
 * 单页界面（自上而下）：
 * 状态卡（一行）→ 设备列表（已保存在前 + 发现到的）→「+ 手动添加」次要入口 →
 * 缓冲档位（三个胶囊）→ 开始/停止 → 底部两个数值。
 *
 * 文案原则：只保留标签与数值，不放解释性长句；所有文字来自 res/values/strings.xml。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SenderScreen(
    state: SenderUiState,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onConnect: (String, Int) -> Unit,
    onConnectManual: (String) -> Unit,
    onRemoveSaved: (String) -> Unit,
    onTierChange: (BufferTier) -> Unit,
    onRemoteVolumeChange: (Int) -> Unit,
    onDismissMessage: () -> Unit,
    onRefreshDiscovery: () -> Unit,
) {
    val snackbarHostState = remember { SnackbarHostState() }
    val message = state.message
    LaunchedEffect(state.messageToken) {
        if (!message.isNullOrBlank()) {
            snackbarHostState.showSnackbar(message)
            onDismissMessage()
        }
    }

    var manualIp by rememberSaveable { mutableStateOf(state.targetIp.orEmpty()) }
    var manualOpen by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(state.targetIp) {
        val ip = state.targetIp
        if (!ip.isNullOrBlank()) manualIp = ip
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.app_name), fontWeight = FontWeight.SemiBold) },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    titleContentColor = MaterialTheme.colorScheme.onSurface,
                ),
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
        containerColor = MaterialTheme.colorScheme.background,
    ) { innerPadding ->
        Column(modifier = Modifier.fillMaxSize().padding(innerPadding)) {
            Column(
                modifier = Modifier
                    .weight(1f)
                    .verticalScroll(rememberScrollState())
                    .padding(start = 20.dp, end = 20.dp, top = 12.dp, bottom = 20.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                StatusCard(state)
                DeviceSection(
                    state = state,
                    manualIp = manualIp,
                    manualOpen = manualOpen,
                    onManualIpChange = { manualIp = it },
                    onManualToggle = { manualOpen = !manualOpen },
                    onConnect = onConnect,
                    onConnectManual = {
                        onConnectManual(manualIp)
                        manualOpen = false
                    },
                    onRemoveSaved = onRemoveSaved,
                    onRefreshDiscovery = onRefreshDiscovery,
                )
                TierSection(state.tier, onTierChange)
                if (state.pcConnected) {
                    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                        Column(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 12.dp)) {
                            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                                Text(stringResource(R.string.pc_receive_volume), style = MaterialTheme.typography.titleMedium)
                                Text(state.remoteVolume?.let { "$it%" } ?: "—", color = MaterialTheme.colorScheme.primary)
                            }
                            Slider(
                                value = (state.remoteVolume ?: 0).toFloat(),
                                onValueChange = { onRemoteVolumeChange(it.roundToInt()) },
                                valueRange = 0f..100f,
                                enabled = state.remoteVolumeReady,
                                modifier = Modifier.fillMaxWidth().semantics { contentDescription = "电脑接收音量" },
                            )
                            if (!state.remoteVolumeReady) Text(
                                stringResource(if (state.targetIp == null) R.string.pc_volume_disconnected else R.string.pc_volume_waiting),
                                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
                PrimaryButton(state, onStart, onStop)
            }
            StatsBar(state)
        }
    }
}

@Composable
private fun StatusCard(state: SenderUiState) {
    val dotColor = when (state.phase) {
        LinkPhase.STREAMING -> MaterialTheme.colorScheme.primary
        LinkPhase.CONNECTED -> Color(0xFF2E9E5B)
        LinkPhase.IDLE -> MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.45f)
    }
    val statusText = when (state.phase) {
        LinkPhase.STREAMING -> stringResource(R.string.status_streaming)
        LinkPhase.CONNECTED -> stringResource(R.string.status_connected)
        LinkPhase.IDLE -> stringResource(R.string.status_disconnected)
    }
    val deviceText = state.targetIp?.let { ip ->
        state.targetName?.takeIf { it.isNotBlank() && it != ip } ?: ip
    }

    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 20.dp, vertical = 18.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(12.dp).background(dotColor, CircleShape))
            Spacer(Modifier.width(10.dp))
            Text(
                text = statusText,
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.SemiBold,
            )
            if (!deviceText.isNullOrBlank()) {
                Spacer(Modifier.width(12.dp))
                Text(
                    text = deviceText,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    textAlign = TextAlign.End,
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

/** 列表项：已保存设备与广播发现到的设备合并后的展示模型。 */
private data class DeviceItem(
    val ip: String,
    val port: Int,
    val name: String,
    val saved: Boolean,
    val lastUsed: Boolean,
)

@Composable
private fun DeviceSection(
    state: SenderUiState,
    manualIp: String,
    manualOpen: Boolean,
    onManualIpChange: (String) -> Unit,
    onManualToggle: () -> Unit,
    onConnect: (String, Int) -> Unit,
    onConnectManual: () -> Unit,
    onRemoveSaved: (String) -> Unit,
    onRefreshDiscovery: () -> Unit,
) {
    val items = remember(state.savedDevices, state.devices) { mergeDevices(state) }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        if (items.isEmpty()) {
            Text(
                text = stringResource(R.string.device_empty),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(vertical = 4.dp),
            )
        } else {
            items.forEach { item ->
                DeviceRow(
                    item = item,
                    selected = item.ip == state.targetIp,
                    onClick = { onConnect(item.ip, item.port) },
                    onRemove = { onRemoveSaved(item.ip) },
                )
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(onClick = onManualToggle) {
                Text(stringResource(R.string.action_manual_add))
            }
            Spacer(Modifier.weight(1f))
            TextButton(onClick = onRefreshDiscovery) {
                Text(stringResource(R.string.action_refresh))
            }
        }

        if (manualOpen) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                OutlinedTextField(
                    value = manualIp,
                    onValueChange = onManualIpChange,
                    modifier = Modifier.weight(1f),
                    placeholder = { Text(stringResource(R.string.manual_ip_hint)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Text,
                        imeAction = ImeAction.Done,
                    ),
                    keyboardActions = KeyboardActions(onDone = { onConnectManual() }),
                )
                Button(
                    onClick = onConnectManual,
                    enabled = manualIp.isNotBlank(),
                ) {
                    Text(stringResource(R.string.action_connect))
                }
            }
        }
    }
}

private fun mergeDevices(state: SenderUiState): List<DeviceItem> {
    val saved = state.savedDevices
    val savedIps = saved.mapTo(HashSet()) { it.ip }
    val result = ArrayList<DeviceItem>(saved.size + state.devices.size)
    saved.forEachIndexed { index, device ->
        result.add(
            DeviceItem(
                ip = device.ip,
                port = device.port,
                name = device.name,
                saved = true,
                lastUsed = index == 0,
            ),
        )
    }
    state.devices.forEach { device ->
        if (device.ip !in savedIps) {
            result.add(
                DeviceItem(
                    ip = device.ip,
                    port = device.port,
                    name = device.name,
                    saved = false,
                    lastUsed = false,
                ),
            )
        }
    }
    return result
}

@Composable
private fun DeviceRow(
    item: DeviceItem,
    selected: Boolean,
    onClick: () -> Unit,
    onRemove: () -> Unit,
) {
    val deleteLabel = stringResource(R.string.action_delete)
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = if (selected) {
                MaterialTheme.colorScheme.primaryContainer
            } else {
                MaterialTheme.colorScheme.surface
            },
        ),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable(onClick = onClick)
                .padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                Modifier
                    .size(8.dp)
                    .background(
                        if (item.saved) {
                            MaterialTheme.colorScheme.primary
                        } else {
                            MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.35f)
                        },
                        CircleShape,
                    ),
            )
            Spacer(Modifier.width(12.dp))
            val address = if (item.port == PacketCodec.DEFAULT_AUDIO_PORT) {
                item.ip
            } else {
                item.ip + ":" + item.port
            }
            val named = item.name.isNotBlank() && item.name != item.ip
            Column(Modifier.weight(1f)) {
                if (named) {
                    Text(
                        text = item.name,
                        style = MaterialTheme.typography.bodyLarge,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        text = address,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                } else {
                    // 没有电脑名时只显示地址，避免同一行重复两次 IP
                    Text(
                        text = address,
                        style = MaterialTheme.typography.bodyLarge,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            if (item.lastUsed) {
                Text(
                    text = stringResource(R.string.device_last_used),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.primary,
                )
                Spacer(Modifier.width(4.dp))
            }
            if (item.saved) {
                Box(
                    modifier = Modifier
                        .size(32.dp)
                        .clip(CircleShape)
                        .clickable(onClick = onRemove)
                        .semantics { contentDescription = deleteLabel },
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = "✕",
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TierSection(tier: BufferTier, onTierChange: (BufferTier) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            text = stringResource(R.string.tier_title),
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
        )
        SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
            BufferTier.entries.forEachIndexed { index, item ->
                SegmentedButton(
                    selected = tier == item,
                    onClick = { onTierChange(item) },
                    shape = SegmentedButtonDefaults.itemShape(
                        index = index,
                        count = BufferTier.entries.size,
                    ),
                    label = { Text(stringResource(item.labelRes)) },
                )
            }
        }
    }
}

@Composable
private fun PrimaryButton(state: SenderUiState, onStart: () -> Unit, onStop: () -> Unit) {
    val streaming = state.phase == LinkPhase.STREAMING
    Button(
        onClick = { if (streaming) onStop() else onStart() },
        enabled = streaming || !state.targetIp.isNullOrBlank(),
        modifier = Modifier.fillMaxWidth().heightIn(min = 72.dp),
        shape = MaterialTheme.shapes.large,
        colors = ButtonDefaults.buttonColors(
            containerColor = if (streaming) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
            contentColor = Color.White,
            disabledContainerColor = MaterialTheme.colorScheme.surfaceVariant,
            disabledContentColor = MaterialTheme.colorScheme.onSurfaceVariant,
        ),
    ) {
        Text(
            text = stringResource(if (streaming) R.string.action_stop else R.string.action_start),
            style = MaterialTheme.typography.titleLarge,
            fontWeight = FontWeight.SemiBold,
        )
    }
}

@Composable
private fun StatsBar(state: SenderUiState) {
    val lossText = String.format(Locale.US, "%.1f%%", state.lossPermille / 10.0)
    Surface(color = MaterialTheme.colorScheme.surface, shadowElevation = 8.dp) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 24.dp, vertical = 12.dp),
                horizontalArrangement = Arrangement.SpaceEvenly,
            ) {
                StatItem(stringResource(R.string.stats_loss), lossText)
                StatItem(stringResource(R.string.stats_packets), state.packetsSent.toString())
            }
            Text(
                text = stringResource(R.string.developer_credit),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(bottom = 12.dp),
            )
        }
    }
}

@Composable
private fun StatItem(label: String, value: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(
            text = value,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
        )
        Text(
            text = label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
