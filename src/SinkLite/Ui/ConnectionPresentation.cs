using System;
using SinkLite.Core;

namespace SinkLite.UI
{
    internal enum ConnectionTone { Neutral, Ready, Active, Warning, Error }

    /// <summary>Presentation only. Transport state is never inferred from UI text.</summary>
    internal sealed class ConnectionPresentation
    {
        public string Title;
        public string Description;
        public ConnectionTone Tone;
        public bool PhoneReady;
        public bool StreamReady;
        public bool OutputReady;

        public static ConnectionPresentation Wifi(WifiSnapshot s)
        {
            var v = new ConnectionPresentation();
            v.PhoneReady = s.Enabled && !string.IsNullOrEmpty(s.Remote) &&
                ((!double.IsNaN(s.ControlAgeMs) && s.ControlAgeMs < 6000) ||
                 (!double.IsNaN(s.LastAudioAgeMs) && s.LastAudioAgeMs < 1000));
            v.StreamReady = s.Enabled && !double.IsNaN(s.LastAudioAgeMs) && s.LastAudioAgeMs < 1000;
            v.OutputReady = s.Enabled && s.OutputActive;
            if (s.State == SinkState.Faulted)
            {
                v.Title = "连接遇到问题"; v.Description = s.Detail ?? "请重新进入 Wi-Fi 模式重试。"; v.Tone = ConnectionTone.Error;
            }
            else if (s.State == SinkState.Busy || s.State == SinkState.Backoff)
            {
                v.Title = "正在准备连接"; v.Description = "连接准备好后会自动接收音频。"; v.Tone = ConnectionTone.Ready;
            }
            else if (!s.Enabled)
            {
                v.Title = "Wi-Fi 已断开"; v.Description = "点击「启用」，等待手机连接。";
            }
            else if (s.Draining)
            {
                v.Title = "正在结束播放"; v.Description = "手机已停止发送，正在播完剩余音频。"; v.Tone = ConnectionTone.Ready;
            }
            else if (!double.IsNaN(s.LastAudioAgeMs) && s.LastAudioAgeMs >= 1000)
            {
                v.Title = "音频暂时中断"; v.Description = "正在等待手机恢复发送。"; v.Tone = ConnectionTone.Warning;
                v.StreamReady = false; v.OutputReady = false;
            }
            else if (s.Recovering)
            {
                v.Title = "正在恢复播放"; v.Description = "正在补充缓冲，恢复后会自动播放。"; v.Tone = ConnectionTone.Warning;
            }
            else if (s.State == SinkState.LinkReady)
            {
                v.Title = "正在准备播放"; v.Description = "已收到手机音频，正在准备输出。"; v.Tone = ConnectionTone.Ready;
            }
            else if (s.State == SinkState.Flowing && s.OutputActive)
            {
                v.Title = s.HasAudibleSignal ? "正在播放手机音频" : "已连接，当前静音";
                v.Description = s.HasAudibleSignal ? "声音正在传到电脑。" : "连接正常，等待手机播放声音。";
                v.Tone = s.HasAudibleSignal ? ConnectionTone.Active : ConnectionTone.Ready;
            }
            else if (s.State == SinkState.Flowing)
            {
                v.Title = "播放设备尚未就绪"; v.Description = "已收到音频，请检查电脑输出设备。"; v.Tone = ConnectionTone.Warning;
            }
            else
            {
                v.Title = v.PhoneReady ? "手机已连接" : "等待手机响应";
                v.Description = v.PhoneReady ? "在手机上点击「开始发送」。" : "等待手机响应，请在手机端选择这台电脑。";
                v.Tone = v.PhoneReady ? ConnectionTone.Ready : ConnectionTone.Neutral;
            }
            return v;
        }
    }
}
