using System;
using SinkLite.Platform;

namespace SinkLite.UI
{
    // UI state and actions live with the window; the registry has no UI dependencies.
    internal sealed partial class MainWindow
    {
        private const int AreaSettingsHotkeys = 352;
        private const int AreaHotkeyBind = 380, AreaHotkeyClear = 390;
        private bool hotkeySettings;
        private int recordingHotkey = -1;
        private GlobalHotkeys hotkeys;
        private HotkeyCapture hotkeyCapture;
        private void StopHotkeyRecording() { recordingHotkey=-1; hotkeyCapture?.Stop(); }
        private void BeginHotkeyRecording(int index)
        {
            hotkeyCapture ??= new HotkeyCapture(hwnd);
            if(!hotkeyCapture.Start()) { StopHotkeyRecording(); ShowNotice("无法录入快捷键，请重试"); return; }
            recordingHotkey=index;
        }
        private string reconnectWifiPeer, reconnectWifiName;
        private long reconnectWifiGeneration;
        private static readonly string[] HotkeyLabels = { "断开所有连接", "重连当前模式", "调高 Wi-Fi 音量", "调低 Wi-Fi 音量" };

        private Win32.RECT HotkeyRect(Metrics m, int index, bool clear)
        {
            int right = m.Left + m.ContentWidth - S(12), top = S(104 + index * 76);
            return clear ? Rect(right-S(48),top,right,top+S(28)) : Rect(m.Left+S(12),top,right-S(56),top+S(28));
        }
        private void DrawHotkeys(IntPtr hdc, Metrics m)
        {
            for(int i=0;i<4;i++)
            {
                int top = S(68+i*76), right=m.Left+m.ContentWidth;
                var card = Rect(m.Left,top,right,top+S(70));
                RoundFill(hdc,card,S(8),brushCard); RoundBorder(hdc,card,S(8),brushBorder);
                Text(hdc,HotkeyLabels[i],fontBody,TextColor,Rect(m.Left+S(12),top+S(5),right-S(70),top+S(31)));
                var binding = hotkeys == null ? pageSettings.Hotkeys[i] : hotkeys[i];
                if(hotkeys != null && !binding.Empty && !hotkeys.Active(i))
                    Text(hdc,"未生效",fontSmall,WarningColor,Rect(right-S(66),top+S(5),right-S(12),top+S(31)),true);
                DrawButton(hdc,HotkeyRect(m,i,false),recordingHotkey==i ? "请按组合键…" : binding.ToString(),AreaHotkeyBind+i,recordingHotkey==i);
                DrawButton(hdc,HotkeyRect(m,i,true),"清除",AreaHotkeyClear+i,false);
            }
        }
        private bool SaveHotkey(int index, HotkeyBinding binding)
        {
            if(!hotkeys.Set(index,binding,out var error)) { ShowNotice(error,5); return false; }
            var settings = Settings.Load(); settings.Hotkeys[index] = binding; settings.Save(); pageSettings = settings;
            StopHotkeyRecording(); focusArea=AreaHotkeyBind+index; ShowNotice(binding.Empty ? "快捷键已清除" : "快捷键已保存"); RequestRepaint(true); return true;
        }
        private bool CaptureHotkey(int key, int modifiers)
        {
            if(recordingHotkey<0) return false;
            if(key==0x1B || key==0x09) { StopHotkeyRecording(); RequestRepaint(true); return key!=0x09; }
            if(key==0x10 || key==0x11 || key==0x12 || key==0x5B || key==0x5C) return true;
            if(key==0x08 || key==0x2E) SaveHotkey(recordingHotkey,default);
            else SaveHotkey(recordingHotkey,new HotkeyBinding(modifiers,key));
            RequestRepaint(true); return true;
        }
        private static int CurrentModifiers() => (Win32.GetKeyState(0x11)<0 ? 2 : 0) |
            (Win32.GetKeyState(0x12)<0 ? 1 : 0) | (Win32.GetKeyState(0x10)<0 ? 4 : 0) |
            (Win32.GetKeyState(0x5B)<0 || Win32.GetKeyState(0x5C)<0 ? 8 : 0);

        private void RememberWifiPeer()
        {
            var current=wifi.Current;
            if(!string.IsNullOrEmpty(current.Remote)) { reconnectWifiPeer=current.Remote; reconnectWifiName=current.DeviceName; reconnectWifiGeneration=current.NetworkGeneration; }
        }
        private void DisconnectWifi(string reason)
        {
            RememberWifiPeer(); wifi.Stop(reason);
        }
        private void EnableWifi(bool restart)
        {
            controller.RequestDisconnect();
            if(restart) DisconnectWifi("reconnect");
            if(wifi.Start())
            {
                wifi.PrepareReconnect(reconnectWifiPeer,reconnectWifiName,reconnectWifiGeneration);
                ShowNotice("Wi-Fi 已启用，等待手机响应");
            }
            else ShowNotice("Wi-Fi 无法启用，请检查端口占用");
            RequestRepaint(true); UpdateTray();
        }
        private void ExecuteHotkey(int action)
        {
            if(modeSwitching) return;
            volumeInput?.Cancel();
            if(action==0)
            {
                controller.RequestDisconnect(); DisconnectWifi("disconnect"); ShowNotice("已断开所有连接");
            }
            else if(action==1)
            {
                if(wifiMode) EnableWifi(true);
                else
                {
                    controller.RequestDisconnect(); DisconnectWifi("bluetooth reconnect");
                    var settings=Settings.Load();
                    controller.RequestConnect(string.IsNullOrEmpty(settings.DeviceId) ? controller.Current.DeviceId : settings.DeviceId);
                    ShowNotice("正在重新连接蓝牙手机");
                }
            }
            else if(wifiMode)
            {
                SetWifiVolume(Math.Clamp(Settings.Load().WifiVolume+(action==2 ? 5 : -5),0,100));
                ShowNotice("Wi-Fi 音量 "+pageSettings.WifiVolume+"%");
            }
            else ShowNotice("音量快捷键仅用于 Wi-Fi 模式");
            RequestRepaint(true); UpdateTray();
        }
    }
}
