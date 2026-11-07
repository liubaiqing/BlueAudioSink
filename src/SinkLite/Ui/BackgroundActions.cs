using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SinkLite.Platform;
namespace SinkLite.UI
{
    internal sealed partial class MainWindow
    {
        private const int UiWorkMessage=0x8063;
        private readonly ConcurrentQueue<Action> uiWork=new();
        private readonly SettingsWriter settingsWriter=new();
        private long settingsVersion;
        private string settingsSuccess;
        private void PostUi(Action action)
        {
            if(windowDisposed) return;
            uiWork.Enqueue(action); Win32.PostMessage(hwnd,UiWorkMessage,IntPtr.Zero,IntPtr.Zero);
        }
        private void QueueSave(Settings settings,string success=null)
        {
            pageSettings=settings.Copy(); settingsSuccess=success;
            settingsVersion=settingsWriter.Submit(pageSettings);
        }
        private void SettingsWritten(long version,bool ok) => PostUi(()=>{
            if(version!=settingsVersion) return;
            if(!ok) ShowNotice("本次设置已应用，但未能保存；下次修改时重试",6);
            else if(settingsSuccess!=null) ShowNotice(settingsSuccess);
            RequestRepaint(true);
        });
        private void RunConnectionOperation(Func<string> operation,Action completed=null)
        {
            if(modeSwitching) return;
            modeSwitching=true; RequestRepaint(true); Win32.UpdateWindow(hwnd);
            connectionOperation=Task.Run(()=>{
                string message;
                try { message=operation(); } catch(Exception error) { message="操作失败："+error.Message; }
                PostUi(()=>{ modeSwitching=false; completed?.Invoke(); if(message!=null) ShowNotice(message); RefreshConnectionDevices(); RequestRepaint(true); UpdateTray(); });
            });
        }
        private Task connectionOperation;
        private void UiLog(string code,params (string Key,object Value)[] fields) => _=Task.Run(()=>log.Info(code,fields));
        private void UiError(string code,params (string Key,object Value)[] fields) => _=Task.Run(()=>log.Error(code,fields));
    }
}
