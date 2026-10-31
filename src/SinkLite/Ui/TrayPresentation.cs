using SinkLite.Core;

namespace SinkLite.UI
{
    internal enum TrayState { Waiting, Preparing, Connected, Playing, Warning, Faulted }

    internal sealed class TrayPresentation
    {
        public TrayState State;
        public string Text;

        public static TrayPresentation Create(bool wifiMode, bool switching, SinkSnapshot bluetooth, WifiSnapshot wifi)
        {
            if (switching) return new TrayPresentation { State = TrayState.Preparing, Text = "正在切换模式" };
            string mode = wifiMode ? "Wi-Fi" : "蓝牙";
            if (wifiMode)
            {
                ConnectionPresentation view = ConnectionPresentation.Wifi(wifi);
                TrayState state = view.Tone == ConnectionTone.Error ? TrayState.Faulted :
                    view.Tone == ConnectionTone.Warning ? TrayState.Warning :
                    view.Tone == ConnectionTone.Active ? TrayState.Playing :
                    wifi.State == SinkState.Busy || wifi.State == SinkState.Backoff ||
                    wifi.State == SinkState.LinkReady || wifi.Draining ? TrayState.Preparing :
                    view.PhoneReady || view.OutputReady ? TrayState.Connected : TrayState.Waiting;
                return new TrayPresentation { State = state, Text = mode + " · " + view.Title };
            }
            TrayState btState = bluetooth.State == SinkState.Faulted ? TrayState.Faulted :
                bluetooth.State == SinkState.Backoff ? TrayState.Warning :
                bluetooth.State == SinkState.Busy ? TrayState.Preparing :
                bluetooth.State == SinkState.Flowing ? TrayState.Playing :
                bluetooth.State == SinkState.Armed || bluetooth.State == SinkState.LinkReady ? TrayState.Connected : TrayState.Waiting;
            string text = btState == TrayState.Playing ? "检测到音频活动" :
                btState == TrayState.Connected ? "连接已就绪" :
                btState == TrayState.Preparing ? "正在连接" :
                btState == TrayState.Warning ? "正在重试连接" :
                btState == TrayState.Faulted ? "连接遇到问题" : "等待连接";
            return new TrayPresentation { State = btState, Text = mode + " · " + text };
        }
    }
}
