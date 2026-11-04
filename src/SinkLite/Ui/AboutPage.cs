using System;
using System.Diagnostics;
using SinkLite.Platform;

namespace SinkLite.UI
{
    internal sealed partial class MainWindow
    {
        private const int AreaSettingsAbout = 353;
        private const int AreaProjectPage = 400, AreaLicensePage = 401;
        private const string ProjectAddress = "https://github.com/liubaiqing/BlueAudioSink";
        private bool aboutSettings;
        private Win32.RECT AboutButtonRect(Metrics m, bool license)
        {
            int width=(m.ContentWidth-S(8))/2;
            int left=license ? m.Left+width+S(8) : m.Left;
            return Rect(left,S(312),left+width,S(346));
        }
        private Win32.RECT ProjectAddressRect(Metrics m) => Rect(m.Left+S(12),S(262),m.Left+m.ContentWidth-S(12),S(288));
        private void DrawAbout(IntPtr hdc,Metrics m)
        {
            int left=m.Left, right=left+m.ContentWidth;
            var card=Rect(left,S(68),right,S(298));
            RoundFill(hdc,card,S(8),brushCard); RoundBorder(hdc,card,S(8),brushBorder);
            if(appIcon!=IntPtr.Zero) Win32.DrawIconEx(hdc,left+S(12),S(80),appIcon,S(32),S(32),0,IntPtr.Zero,3);
            Text(hdc,"蓝音频",fontTitle,Win32.Rgb(23,40,63),Rect(left+S(56),S(76),right-S(12),S(108)));
            Text(hdc,"BlueAudioSink · BAS · Windows x64",fontSmall,Win32.Rgb(88,102,122),Rect(left+S(12),S(114),right-S(12),S(136)));
            void Row(int top,string label,string value)
            {
                Text(hdc,label,fontBody,Win32.Rgb(88,102,122),Rect(left+S(12),S(top),left+S(94),S(top+28)));
                Text(hdc,value,fontBody,Win32.Rgb(23,40,63),Rect(left+S(100),S(top),right-S(12),S(top+28)));
            }
            Row(142,"软件版本",typeof(MainWindow).Assembly.GetName().Version.ToString(3));
            Row(176,"开发作者","liubaiqing");
            Row(210,"开源协议","MIT License");
            Text(hdc,"项目地址",fontSmall,Win32.Rgb(88,102,122),Rect(left+S(12),S(244),right-S(12),S(264)));
            Text(hdc,"github.com/liubaiqing/BlueAudioSink",fontSmall,Win32.Rgb(0,112,194),ProjectAddressRect(m));
            DrawButton(hdc,AboutButtonRect(m,false),"项目主页",AreaProjectPage,false);
            DrawButton(hdc,AboutButtonRect(m,true),"查看协议",AreaLicensePage,false);
            Text(hdc,"© 2026 liubaiqing",fontSmall,Win32.Rgb(111,124,141),Rect(left,S(358),right,S(380)),true);
        }
        private void OpenAboutLink(bool license)
        {
            try { Process.Start(new ProcessStartInfo { FileName=ProjectAddress+(license ? "/blob/HEAD/LICENSE" : ""), UseShellExecute=true }); }
            catch { ShowNotice("无法打开链接，请检查默认浏览器"); }
        }
    }
}
