package com.blueaudiosink.sender.ui

import android.os.Build
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.blueaudiosink.sender.R
import com.blueaudiosink.sender.capture.SpeakerGuardService
import com.blueaudiosink.sender.core.Diagnostics
import com.blueaudiosink.sender.net.DiscoveryTargets
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private const val PROJECT_URL = "https://github.com/liubaiqing/BlueAudioSink"

@Composable
internal fun SettingsContent(page: String, modifier: Modifier, onSelect: (String) -> Unit) {
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)) {
        when (page) {
            "settings" -> {
                val context = LocalContext.current
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    SettingsRow(R.string.overlay_volume_settings) {
                        val intent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                            Uri.fromParts("package", context.packageName, null))
                        runCatching { context.startActivity(intent) }.onFailure {
                            Toast.makeText(context, R.string.overlay_volume_unavailable, Toast.LENGTH_SHORT).show()
                        }
                    }
                    ThemeRow()
                    SpeakerGuardRow()
                    ExportDiagnosticsRow()
                }
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    SettingsRow(R.string.local_device_title) { onSelect("device") }
                    SettingsRow(R.string.about_title) { onSelect("about") }
                }
            }
            "device" -> LocalDeviceInfo()
            "about" -> AboutInfo()
        }
    }
}

@Composable
private fun ExportDiagnosticsRow() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val export = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/x-ndjson")) { uri ->
        if (uri != null) scope.launch {
            val saved = withContext(Dispatchers.IO) {
                runCatching {
                    context.contentResolver.openOutputStream(uri)?.bufferedWriter(Charsets.UTF_8)?.use {
                        it.write(Diagnostics.export())
                    } != null
                }.getOrDefault(false)
            }
            Toast.makeText(context, if (saved) R.string.diagnostics_saved else R.string.diagnostics_export_failed, Toast.LENGTH_SHORT).show()
        }
    }
    SettingsRow(R.string.diagnostics_export) {
        runCatching { export.launch("BlueAudioSink-Android.jsonl") }
            .onFailure { Toast.makeText(context, R.string.diagnostics_export_failed, Toast.LENGTH_SHORT).show() }
    }
}

@Composable
private fun SpeakerGuardRow() {
    val context = LocalContext.current
    var enabled by remember(context) { mutableStateOf(SpeakerGuardService.isEnabled(context)) }
    val available by SpeakerGuardService.available.collectAsState()
    fun openPermissionSettings() {
        runCatching { context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) }
            .onFailure { Toast.makeText(context, R.string.speaker_guard_unavailable, Toast.LENGTH_SHORT).show() }
    }
    ListItem(
        headlineContent = { Text(stringResource(R.string.speaker_guard_title)) },
        supportingContent = {
            if (enabled && !available) {
                Text(
                    stringResource(R.string.speaker_guard_permission),
                    modifier = Modifier.clickable(onClick = ::openPermissionSettings),
                    color = MaterialTheme.colorScheme.primary,
                )
            } else {
                Text(stringResource(R.string.speaker_guard_summary))
            }
        },
        trailingContent = {
            Switch(checked = enabled, onCheckedChange = {
                enabled = it
                SpeakerGuardService.setEnabled(context, it)
                if (it && !available) openPermissionSettings()
            })
        },
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surface),
    )
}

@Composable
private fun SettingsRow(label: Int, onClick: () -> Unit) {
    ListItem(
        headlineContent = { Text(stringResource(label)) },
        trailingContent = {
            Text("›", style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        },
        modifier = Modifier.clickable(onClick = onClick),
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surface),
    )
}

@Composable
private fun ThemeRow() {
    val context = LocalContext.current
    val selected = currentThemeMode()
    val colors = MaterialTheme.colorScheme
    ListItem(
        headlineContent = { Text(stringResource(R.string.theme_settings)) },
        trailingContent = {
            Row(Modifier.selectableGroup()) {
                ThemeMode.entries.forEach { mode ->
                    val active = selected == mode
                    Box(
                        modifier = Modifier
                            .sizeIn(minWidth = 48.dp, minHeight = 48.dp)
                            .selectable(
                                selected = active,
                                role = Role.RadioButton,
                                onClick = { saveThemeMode(context, mode) },
                            ),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(
                            stringResource(mode.label),
                            modifier = Modifier
                                .background(
                                    if (active) colors.primaryContainer else colors.surface,
                                    MaterialTheme.shapes.small,
                                )
                                .padding(horizontal = 8.dp, vertical = 4.dp),
                            style = MaterialTheme.typography.labelMedium,
                            color = if (active) colors.onPrimaryContainer else colors.onSurfaceVariant,
                        )
                    }
                }
            }
        },
        colors = ListItemDefaults.colors(containerColor = colors.surface),
    )
}

@Composable
private fun LocalDeviceInfo() {
    var addresses by remember { mutableStateOf<List<String>>(emptyList()) }
    LaunchedEffect(Unit) {
        while (true) {
            addresses = withContext(Dispatchers.IO) {
                DiscoveryTargets.lans().map { lan -> lan.ip.joinToString(".") { (it.toInt() and 255).toString() } }.distinct()
            }
            delay(1000)
        }
    }
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.fillMaxWidth().padding(20.dp), verticalArrangement = Arrangement.spacedBy(20.dp)) {
            InfoField(stringResource(R.string.local_model), "${Build.MANUFACTURER} ${Build.MODEL}")
            InfoField(stringResource(R.string.local_android), "Android ${Build.VERSION.RELEASE}")
            InfoField(stringResource(R.string.local_address), addresses.joinToString("\n").ifEmpty { stringResource(R.string.local_no_network) })
        }
    }
}

@Composable
private fun AboutInfo() {
    val context = LocalContext.current
    val uriHandler = LocalUriHandler.current
    val version = remember(context) {
        @Suppress("DEPRECATION")
        context.packageManager.getPackageInfo(context.packageName, 0).versionName.orEmpty()
    }
    val failed = stringResource(R.string.error_open_link)
    var linkError by remember { mutableStateOf(false) }
    fun open(url: String) { linkError = runCatching { uriHandler.openUri(url) }.isFailure }
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.fillMaxWidth().padding(20.dp), verticalArrangement = Arrangement.spacedBy(20.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Image(painterResource(R.drawable.ic_launcher), null, Modifier.size(48.dp))
                Column {
                    Text(stringResource(R.string.brand_chinese_name), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                    Text(stringResource(R.string.brand_english_name), style = MaterialTheme.typography.bodyMedium)
                    Text(stringResource(R.string.about_subtitle), style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            InfoField(stringResource(R.string.about_version), version)
            InfoField(stringResource(R.string.about_author), "liubaiqing")
            InfoField(stringResource(R.string.about_license), "MIT License")
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(stringResource(R.string.about_project), style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text("github.com/liubaiqing/BlueAudioSink", color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.clickable { open(PROJECT_URL) }.padding(vertical = 12.dp))
            }
        }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        OutlinedButton(onClick = { open(PROJECT_URL) }, modifier = Modifier.weight(1f)) { Text(stringResource(R.string.about_open_project)) }
        OutlinedButton(onClick = { open("$PROJECT_URL/blob/HEAD/LICENSE") }, modifier = Modifier.weight(1f)) { Text(stringResource(R.string.about_open_license)) }
    }
    if (linkError) Text(failed, color = MaterialTheme.colorScheme.error)
    Text("© 2026 liubaiqing", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun InfoField(label: String, value: String) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        SelectionContainer { Text(value, style = MaterialTheme.typography.bodyLarge) }
    }
}
