package com.blueaudiosink.sender.ui

import androidx.compose.foundation.background
import androidx.activity.compose.BackHandler
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
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.ui.res.painterResource
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import com.blueaudiosink.sender.core.LinkController
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
import com.blueaudiosink.sender.core.SendDuration
import com.blueaudiosink.sender.net.PacketCodec
import java.util.Locale

/**
 * 主界面：状态卡 → 设备操作与列表 → 缓冲档位 → 接收音量 → 开始/停止 → 统计。
 * 齿轮入口提供独立设置和信息页，页面切换不影响连接与采集。
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
    var page by rememberSaveable { mutableStateOf("main") }
    val goBack = { page = if (page == "settings") "main" else "settings" }
    BackHandler(enabled = page != "main", onBack = goBack)
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
                title = { Text(stringResource(when (page) {
                    "settings" -> R.string.settings_title
                    "device" -> R.string.local_device_title
                    "about" -> R.string.about_title
                    else -> R.string.app_name
                }), fontWeight = FontWeight.SemiBold) },
                navigationIcon = {
                    if (page != "main") IconButton(onClick = goBack) {
                        Icon(painterResource(R.drawable.ic_back), stringResource(R.string.action_back))
                    }
                },
                actions = {
                    if (page == "main") IconButton(onClick = { page = "settings" }) {
                        Icon(painterResource(R.drawable.ic_settings), stringResource(R.string.settings_title))
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    titleContentColor = MaterialTheme.colorScheme.onSurface,
                ),
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
        containerColor = MaterialTheme.colorScheme.background,
    ) { innerPadding ->
        if (page != "main") {
            SettingsContent(page, Modifier.padding(innerPadding), onSelect = { page = it })
            return@Scaffold
        }
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
                        ReceiverVolumeControl(
                            state, onRemoteVolumeChange,
                            Modifier.padding(horizontal = 20.dp, vertical = 12.dp),
                        )
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
    val statusText = state.connectionStage ?: if(state.resolving) "正在查找电脑" else when (state.phase) {
        LinkPhase.STREAMING -> stringResource(R.string.status_streaming)
        LinkPhase.CONNECTED -> stringResource(R.string.status_connected)
        LinkPhase.IDLE -> stringResource(R.string.status_disconnected)
    }
    val deviceText = if(state.resolving) state.targetName else state.targetIp?.let { ip ->
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
                text = statusText + if(state.compatibleTransport) " · 兼容" else "",
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
    val key: String,
    val legacy: Boolean,
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
    val items = remember(state.savedDevices, state.devices, state.targetIp, state.pcConnected) { mergeDevices(state) }
    var oldDevice by remember { mutableStateOf<DeviceItem?>(null) }
    var mergeOpen by remember { mutableStateOf(false) }
    oldDevice?.let { old -> AlertDialog(
        onDismissRequest={oldDevice=null}, title={Text("确认旧地址记录")},
        text={Text("这条旧记录还没有电脑身份。请确认 ${old.name}（${old.ip}）是要连接的电脑；连接成功后会记住它的固定身份")},
        confirmButton={TextButton(onClick={onConnect(old.key,old.port);oldDevice=null}){Text("确认连接")}},
        dismissButton={TextButton(onClick={oldDevice=null}){Text("取消")}},
    ) }
    if(mergeOpen) LegacyMergeDialog(state) { mergeOpen=false }


    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(onClick = onManualToggle) {
                Text(stringResource(R.string.action_manual_add))
            }
            Spacer(Modifier.weight(1f))
            IconButton(onClick = onRefreshDiscovery, modifier = Modifier.size(48.dp)) {
                Icon(painter = painterResource(R.drawable.ic_refresh),
                    contentDescription = stringResource(R.string.action_refresh),
                    tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(24.dp))
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
                    selected = if(state.targetDeviceId!=null) item.key=="id:${state.targetDeviceId}" else item.ip==state.targetIp,
                    onClick = { if(item.legacy) oldDevice=item else onConnect(item.key, item.port) },
                    onRemove = { onRemoveSaved(item.key) },
                )
            }
        }

        if(state.pcConnected && state.targetDeviceId!=null && state.savedDevices.any { it.deviceId==null }) {
            TextButton(onClick={mergeOpen=true}) { Text("合并旧地址记录") }
        }

    }
}

private fun mergeDevices(state: SenderUiState): List<DeviceItem> {
    val result=ArrayList<DeviceItem>()
    val keys=HashSet<String>()
    state.savedDevices.forEachIndexed { index,saved ->
        val live=state.devices.firstOrNull { saved.deviceId!=null && it.deviceId==saved.deviceId }
        val active=state.pcConnected && saved.deviceId==state.targetDeviceId
        val ip=if(active) state.targetIp ?: saved.ip else live?.ip ?: saved.ip
        result.add(DeviceItem(saved.key,saved.deviceId==null,ip,live?.port ?: saved.port,live?.name ?: saved.name,true,index==0))
        keys.add(saved.key)
    }
    state.devices.forEach { d ->
        val key=d.deviceId?.let { "id:$it" } ?: "ip:${d.ip}"
        if(keys.add(key)) result.add(DeviceItem(key,false,d.ip,d.port,d.name,false,false))
    }
    return result
}

@Composable
private fun LegacyMergeDialog(state: SenderUiState, close: ()->Unit) {
    var selected by remember { mutableStateOf(emptySet<String>()) }
    AlertDialog(onDismissRequest=close,title={Text("合并到 ${state.targetName.orEmpty()}")},
        text={Column {
            Text("只选择属于当前这台电脑的旧地址，不会根据同名自动合并")
            state.savedDevices.filter { it.deviceId==null }.forEach { d ->
                Row(verticalAlignment=Alignment.CenterVertically) {
                    Checkbox(checked=d.key in selected,onCheckedChange={checked->selected=if(checked) selected+d.key else selected-d.key})
                    Text("${d.name} · ${d.ip}")
                }
            }
        }},
        confirmButton={TextButton(enabled=selected.isNotEmpty() && state.pcConnected,onClick={LinkController.mergeLegacy(selected);close()}){Text("确认合并")}},
        dismissButton={TextButton(onClick=close){Text("取消")}},
    )
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
            val named = item.name.isNotBlank() && !Regex("[0-9.]+(:[0-9]+)?").matches(item.name)
            Text(
                text = if(named) item.name else stringResource(if(item.saved) R.string.device_saved_unnamed else R.string.device_unnamed),
                style = MaterialTheme.typography.bodyLarge,
                maxLines = 1, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
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
        enabled = streaming || (state.pcConnected && !state.resolving),
        modifier = Modifier.fillMaxWidth().heightIn(min = 72.dp),
        shape = MaterialTheme.shapes.large,
        colors = ButtonDefaults.buttonColors(
            containerColor = if (streaming) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
            contentColor = if (streaming) MaterialTheme.colorScheme.onError else MaterialTheme.colorScheme.onPrimary,
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
                StatItem(stringResource(R.string.stats_duration), SendDuration.format(state.sendingSeconds))
            }
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
