package com.blueaudiosink.sender.ui

import android.content.Context
import android.content.SharedPreferences
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import com.blueaudiosink.sender.R

internal enum class ThemeMode(val key: String, val label: Int) {
    SYSTEM("system", R.string.theme_system),
    LIGHT("light", R.string.theme_light),
    DARK("dark", R.string.theme_dark),
}

private const val THEME_KEY = "theme_mode"
private fun themePrefs(context: Context) =
    context.getSharedPreferences("appearance", Context.MODE_PRIVATE)

internal fun saveThemeMode(context: Context, mode: ThemeMode) {
    themePrefs(context).edit().putString(THEME_KEY, mode.key).apply()
}

@Composable
internal fun currentThemeMode(): ThemeMode {
    val context = LocalContext.current
    val prefs = remember(context) { themePrefs(context) }
    fun read() = ThemeMode.entries.firstOrNull { it.key == prefs.getString(THEME_KEY, null) }
        ?: ThemeMode.SYSTEM
    var mode by remember(prefs) { mutableStateOf(read()) }
    DisposableEffect(prefs) {
        val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
            if (key == THEME_KEY || key == null) mode = read()
        }
        prefs.registerOnSharedPreferenceChangeListener(listener)
        mode = read()
        onDispose { prefs.unregisterOnSharedPreferenceChangeListener(listener) }
    }
    return mode
}

@Composable
internal fun useDarkTheme(): Boolean = when (currentThemeMode()) {
    ThemeMode.SYSTEM -> isSystemInDarkTheme()
    ThemeMode.LIGHT -> false
    ThemeMode.DARK -> true
}

private val LightColors = lightColorScheme(
    primary = Color(0xFF1B6EF3),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDCE7FF),
    onPrimaryContainer = Color(0xFF06245C),
    secondary = Color(0xFF3F5C8A),
    background = Color(0xFFF7FAFF),
    surface = Color(0xFFFFFFFF),
    surfaceVariant = Color(0xFFE7EDF7),
    onSurfaceVariant = Color(0xFF44506A),
    error = Color(0xFFB3261E),
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFF8FB4FF),
    onPrimary = Color(0xFF002F6C),
    primaryContainer = Color(0xFF0F3D7A),
    onPrimaryContainer = Color(0xFFDCE7FF),
    background = Color(0xFF0F141C),
    surface = Color(0xFF161D28),
    surfaceVariant = Color(0xFF2A3242),
    onSurfaceVariant = Color(0xFFB9C4D6),
)

/** 全中文极简单页主题；字号/间距全部用 sp/dp，DPI 由系统自动缩放。 */
@Composable
fun BlueAudioSinkTheme(
    darkTheme: Boolean = useDarkTheme(),
    content: @Composable () -> Unit,
) {
    MaterialTheme(
        colorScheme = if (darkTheme) DarkColors else LightColors,
        typography = Typography(),
        content = content,
    )
}
