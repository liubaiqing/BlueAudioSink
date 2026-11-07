using System;
using Microsoft.Win32;
using SinkLite.Platform;

namespace SinkLite.UI
{
    internal sealed partial class MainWindow
    {
        private const int AreaThemeSystem = 365, AreaThemeLight = 366, AreaThemeDark = 367;
        private static readonly int[] ThemeAreas = { AreaThemeSystem, AreaThemeLight, AreaThemeDark };
        private static readonly string[] ThemeLabels = { "跟随系统", "浅色", "深色" };

        private bool darkTheme;

        private int BackgroundColor => ThemeColor(0xF5F7FB, 0x101720);
        private int SurfaceColor => ThemeColor(0xFFFFFF, 0x192330);
        private int TextColor => ThemeColor(0x17283F, 0xE5EDF7);
        private int SecondaryColor => ThemeColor(0x58667A, 0xAAB9CC);
        private int DisabledColor => ThemeColor(0x8E98A7, 0x8190A3);
        private int AccentTextColor => ThemeColor(0x006EC3, 0x83BFFF);
        private int SuccessColor => ThemeColor(0x14784B, 0x74D7A5);
        private int WarningColor => ThemeColor(0x975B16, 0xF1BA73);
        private int ErrorColor => ThemeColor(0xC43333, 0xFF9B9B);
        private int HelpBackgroundColor => ThemeColor(0xE2EAF4, 0x2B3C51);
        private int HelpTextColor => ThemeColor(0x485E7B, 0xC6D8EE);

        private int ThemeColor(int light, int dark)
        {
            int rgb = darkTheme ? dark : light;
            return Win32.Rgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        private static bool IsDarkTheme(AppTheme theme)
        {
            if (theme != AppTheme.System) return theme == AppTheme.Dark;
            try
            {
                return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1) is int light && light == 0;
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private void ApplyWindowTheme()
        {
            bool useDark = IsDarkTheme(pageSettings.Theme);
            if (darkTheme == useDark) return;
            volumeInput?.Cancel();
            smoothShapes?.Clear();
            ReleaseThemeBrushes();
            darkTheme = useDark;
            CreateThemeBrushes();
            ApplyCaptionTheme();
            RequestRepaint(true);
        }

        private void ApplyCaptionTheme()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, darkTheme ? 1 : 0, sizeof(int));
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_CAPTION_COLOR, BackgroundColor, sizeof(int));
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_TEXT_COLOR, TextColor, sizeof(int));
        }

        private void CreateThemeBrushes()
        {
            brushBack = Win32.CreateSolidBrush(BackgroundColor);
            brushCard = Win32.CreateSolidBrush(SurfaceColor);
            brushBorder = Win32.CreateSolidBrush(ThemeColor(0xDCE3ED, 0x36475A));
            brushAccent = Win32.CreateSolidBrush(ThemeColor(0x0078D7, 0x2878CA));
            brushAccentHover = Win32.CreateSolidBrush(ThemeColor(0x2896F0, 0x286FBB));
            brushAccentPress = Win32.CreateSolidBrush(ThemeColor(0x0055A0, 0x1B5695));
            brushBtn = Win32.CreateSolidBrush(SurfaceColor);
            brushBtnHover = Win32.CreateSolidBrush(ThemeColor(0xE8F0FA, 0x26374B));
            brushBtnPress = Win32.CreateSolidBrush(ThemeColor(0xCEDEF0, 0x314862));
            brushRowHover = Win32.CreateSolidBrush(ThemeColor(0xEEEEEE, 0x26374B));
            brushRowSelected = Win32.CreateSolidBrush(ThemeColor(0xE5F1FB, 0x233E5B));
            brushSwitchOff = Win32.CreateSolidBrush(ThemeColor(0xC4C4C4, 0x4B5D72));
            brushSwitchOffHover = Win32.CreateSolidBrush(ThemeColor(0xA8A8A8, 0x5B7189));
            brushTooltip = Win32.CreateSolidBrush(ThemeColor(0x223043, 0x34485F));
        }

        private void ReleaseThemeBrushes()
        {
            foreach (IntPtr brush in new[] { brushBack, brushCard, brushBorder, brushAccent, brushAccentHover,
                brushAccentPress, brushBtn, brushBtnHover, brushBtnPress, brushRowHover, brushRowSelected,
                brushSwitchOff, brushSwitchOffHover, brushTooltip })
            {
                if (brush != IntPtr.Zero) Win32.DeleteObject(brush);
            }
        }

        private void SaveTheme(int area)
        {
            var settings = pageSettings.Copy();
            settings.Theme = (AppTheme)(area - AreaThemeSystem);
            QueueSave(settings);
            ApplyWindowTheme();
        }

        private Win32.RECT ThemeOptionRect(Metrics m, int area)
        {
            int start = m.Left + S(100);
            int gap = S(6);
            int width = (m.Left + m.ContentWidth - start - gap * 2) / 3;
            int left = start + (area - AreaThemeSystem) * (width + gap);
            return Rect(left, S(394), left + width, S(426));
        }

        private void DrawThemeSettings(IntPtr hdc, Metrics m)
        {
            Text(hdc, "主题", fontSmall, TextColor, Rect(m.Left + S(8), S(394), m.Left + S(92), S(426)));
            foreach (int area in ThemeAreas)
            {
                int index = area - AreaThemeSystem;
                DrawInlineTab(hdc, ThemeOptionRect(m, area), ThemeLabels[index], (int)pageSettings.Theme == index, area);
            }
        }
    }
}
