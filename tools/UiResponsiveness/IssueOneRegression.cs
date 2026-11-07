using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

internal static class IssueOneRegression
{
    private static readonly Assembly App=Assembly.Load("BlueAudioSink");
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private static object New(string name,params object[] args)=>Activator.CreateInstance(App.GetType(name),Flags,null,args,null);
    private static object Call(object value,string name,params object[] args)=>value.GetType().GetMethod(name,Flags).Invoke(value,args);
    private static object Get(object value,string name)=>value.GetType().GetField(name,Flags).GetValue(value);
    private static void Set(object value,string name,object next)=>value.GetType().GetField(name,Flags).SetValue(value,next);
    private static object Property(object value,string name)=>value.GetType().GetProperty(name).GetValue(value);
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    private static string[] Defaults()
    {
        using var devices=new MMDeviceEnumerator();
        return new[]{Role.Console,Role.Multimedia,Role.Communications}.Select(role=>{
            using var device=devices.GetDefaultAudioEndpoint(DataFlow.Render,role); return device.ID;
        }).ToArray();
    }
    public static int Run()
    {
        string path=Path.Combine(Path.GetTempPath(),"BAS-window-"+Guid.NewGuid()+".ini");
        var log=New("SinkLite.Platform.Log",Path.Combine(AppContext.BaseDirectory,"logs"),90);
        var controller=New("SinkLite.Core.SinkController",New("SinkLite.Core.Policy"),log);
        var watcher=New("SinkLite.Platform.EndpointWatcher");
        object window=null; IntPtr hwnd=IntPtr.Zero;
        try {
            Call(controller,"AttachRoute",New("SinkLite.Platform.RouteControl",watcher));
            var before=Defaults();
            var inventory=Call(controller,"ListPhysicalOutputs");
            var outputs=((IEnumerable)inventory).Cast<object>().ToList();
            Check(outputs.Count>0,"No physical output for real routing check");
            var selected=outputs.FirstOrDefault(o=>(string)Property(o,"Id")!=before[0]) ?? outputs[0];
            string id=(string)Property(selected,"Id");
            Check((bool)Call(controller,"SetPreferredRender",id,(string)Property(selected,"Name"),false),"Output selection failed");
            Check(before.SequenceEqual(Defaults()),"Selecting app output changed a Windows default role");
            Check(!(bool)Call(controller,"SetPreferredRender","invalid-endpoint","invalid",false),"Invalid output accepted");
            Check((string)Property(controller,"PreferredRenderId")==id,"Failed selection changed preference");

            object preferences=New("SinkLite.Platform.Settings");
            window=MakeWindow(preferences); hwnd=(IntPtr)Get(window,"hwnd");
            Set(window,"cachedOutputs",inventory); Set(window,"dropdownOpen",true);
            Call(window,"Activate",200+outputs.IndexOf(selected));
            Check(((Task)Get(window,"connectionOperation")).Wait(3000),"UI output operation timed out");
            SendMessage(hwnd,0x8063,IntPtr.Zero,IntPtr.Zero);
            Check((string)Property(Get(Get(window,"wifi"),"outputOptions"),"EndpointId")==id,"UI selection did not reach the Wi-Fi playback options");
            Check(before.SequenceEqual(Defaults()),"UI output selection changed a Windows default role");
            GetWindowRect(hwnd,out var initial);
            SetWindowPos(hwnd,IntPtr.Zero,initial.Left+40,initial.Top+30,initial.Right-initial.Left+20,initial.Bottom-initial.Top+20,0x14);
            Call(window,"CaptureWindowGeometry"); GetWindowRect(hwnd,out var expected);
            object settings=Get(window,"pageSettings");
            Check((int)Property(settings,"WindowX")==expected.Left && (int)Property(settings,"WindowY")==expected.Top,"Workspace/screen coordinates disagree");
            Check((int)Property(settings,"WindowWidth")==expected.Right-expected.Left,"Native window width lost");
            ShowWindow(hwnd,7); // Minimize without taking focus; must preserve normal bounds.
            Call(window,"CaptureWindowGeometry");
            Check((int)Property(Get(window,"pageSettings"),"WindowX")==expected.Left,"Minimize overwrote normal position");
            ShowWindow(hwnd,3); Call(window,"CaptureWindowGeometry");
            Check((bool)Property(Get(window,"pageSettings"),"WindowMaximized"),"Maximized state not saved");
            DestroyWindow(hwnd); hwnd=IntPtr.Zero; ((IDisposable)window).Dispose(); window=null;
            var stored=App.GetType("SinkLite.Platform.Settings").GetMethod("Load").Invoke(null,new object[]{path});
            window=MakeWindow(stored); hwnd=(IntPtr)Get(window,"hwnd"); GetWindowRect(hwnd,out var restored);
            Check(restored.Left==expected.Left && restored.Top==expected.Top && restored.Right==expected.Right && restored.Bottom==expected.Bottom,"Native window restart did not restore exact normal bounds");
            Call(window,"ShowWindow"); Check(IsZoomed(hwnd),"Restart did not restore maximized state");
            ShowWindow(hwnd,9); Call(window,"CaptureWindowGeometry"); GetWindowRect(hwnd,out var normalAgain);
            Check(normalAgain.Left==expected.Left && normalAgain.Top==expected.Top && normalAgain.Right==expected.Right && normalAgain.Bottom==expected.Bottom,"Maximize/restore lost normal bounds");
            Console.WriteLine(JsonSerializer.Serialize(new {uiSelectionReachesWifiEndpoint=true,maximizedRestart=true,outputDefaultRolesUnchanged=true,physicalOutputs=outputs.Count,selectedDifferentFromDefault=id!=before[0],invalidSelectionPreservesPreference=true,nativeWindowRestart=true,minimizePreservesBounds=true,isolatedSettings=true}));
            return 0;

            object MakeWindow(object value) {
                object next=New("SinkLite.UI.MainWindow",controller,log,true);
                ((IDisposable)Get(next,"settingsWriter")).Dispose();
                var settingType=App.GetType("SinkLite.Platform.Settings");
                var delegateType=typeof(Func<,>).MakeGenericType(settingType,typeof(bool));
                var argument=Expression.Parameter(settingType);
                var save=Expression.Lambda(delegateType,Expression.Call(argument,settingType.GetMethod("Save"),Expression.Constant(path)),argument).Compile();
                Set(next,"settingsWriter",New("SinkLite.Platform.SettingsWriter",save));
                Set(next,"pageSettings",value); Call(next,"Create"); return next;
            }
        } finally {
            if(hwnd!=IntPtr.Zero) DestroyWindow(hwnd);
            if(window!=null) ((IDisposable)window).Dispose();
            ((IDisposable)controller).Dispose(); ((IDisposable)watcher).Dispose(); ((IDisposable)log).Dispose(); File.Delete(path);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
}
