using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

// An isolated off-screen real window exercises hit testing, GDI paint and slow inventories.
// No playback, device connection, preference writes, pointer movement or foreground activation.
internal static class Program
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Assembly App = Assembly.Load("BlueAudioSink");
    private static object New(string name, params object[] args) => Activator.CreateInstance(App.GetType(name), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
    private static object Get(object owner,string field) => owner.GetType().GetField(field,Fields).GetValue(owner);
    private static void Set(object owner,string field,object value) => owner.GetType().GetField(field,Fields).SetValue(owner,value);
    private static object Call(object owner,string name,params object[] args) => owner.GetType().GetMethod(name,Fields | BindingFlags.Public).Invoke(owner,args);
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    public static readonly ManualResetEventSlim NetworkEntered = new(), OutputEntered = new(), Release = new();
    public static void PauseNetwork() { NetworkEntered.Set(); Release.Wait(5000); }
    public static void PauseOutput() { OutputEntered.Set(); Release.Wait(5000); }
    private static Delegate BlockedFactory(Type delegateType, object value, string pause)
        => Expression.Lambda(delegateType,Expression.Block(Expression.Call(typeof(Program).GetMethod(pause)),Expression.Constant(value,delegateType.GetMethod("Invoke").ReturnType))).Compile();

    [STAThread]
    private static int Main(string[] args)
    {
        var log = New("SinkLite.Platform.Log",Path.Combine(AppContext.BaseDirectory,"logs"),90);
        var policy = New("SinkLite.Core.Policy");
        var controller = New("SinkLite.Core.SinkController",policy,log);
        var window = New("SinkLite.UI.MainWindow",controller,log,true);
        IntPtr hwnd = IntPtr.Zero;
        Task pending = null;
        try
        {
            object preferences = Get(window,"pageSettings");
            var theme = preferences.GetType().GetProperty("Theme");
            theme.SetValue(preferences,Enum.Parse(theme.PropertyType,args.Contains("--dark") ? "Dark" : "Light"));
            preferences.GetType().GetProperty("CloseToTray").SetValue(preferences,false);
            var keys = (Array)preferences.GetType().GetProperty("Hotkeys").GetValue(preferences);
            for(int i=0;i<keys.Length;i++) keys.SetValue(Activator.CreateInstance(keys.GetType().GetElementType()),i);
            Call(window,"Create"); hwnd = (IntPtr)Get(window,"hwnd");
            KillTimer(hwnd,new IntPtr(1));
            SetWindowPos(hwnd,IntPtr.Zero,-12000,-12000,0,0,0x0001|0x0010|0x0004);
            ShowWindow(hwnd,4); // SW_SHOWNOACTIVATE
            Set(window,"settingsMode",true);
            var points = new Dictionary<int,(int X,int Y)>();
            GetClientRect(hwnd,out var client);
            for(int y=60;y<client.Bottom-20;y+=12) for(int x=10;x<client.Right-10;x+=12) {
                int area = (int)Call(window,"HitTest",x,y);
                if(area>=0) points.TryAdd(area,(x,y));
            }
            Check(points.Count>=8,"Settings hit regions missing");
            var ordered = points.OrderBy(pair=>pair.Key).Select(pair=>pair.Value).ToArray();
            SendMessage(hwnd,0x000F,IntPtr.Zero,IntPtr.Zero);
            for(int i=0;i<ordered.Length*2;i++) Move(ordered[i%ordered.Length]);

            object wifi = Get(window,"wifi");
            var provider = wifi.GetType().GetField("networkProvider",Fields);
            provider.SetValue(wifi,BlockedFactory(provider.FieldType,Get(wifi,"lanAddresses"),nameof(PauseNetwork)));
            pending = Task.Run(()=>Call(wifi,"RefreshNetwork",true));
            Check(NetworkEntered.Wait(2000),"No background network enumeration");

            var readOutputs = window.GetType().GetField("readOutputs",Fields);
            readOutputs.SetValue(window,BlockedFactory(readOutputs.FieldType,Get(window,"cachedOutputs"),nameof(PauseOutput)));
            var start = Stopwatch.StartNew(); Call(window,"RefreshOutputCache",true);
            Check(start.ElapsedMilliseconds<200,"Output enumeration blocked the UI thread");
            Check(OutputEntered.Wait(2000),"No background output enumeration");

            var elapsed = new List<double>();
            int deferredPaints = 0;
            for(int i=0;i<300;i++) {
                var timer = Stopwatch.StartNew(); Move(ordered[i%ordered.Length]); timer.Stop();
                elapsed.Add(timer.Elapsed.TotalMilliseconds);
                if(GetUpdateRect(hwnd,IntPtr.Zero,false)) deferredPaints++;
            }
            elapsed.Sort();
            var report = new { theme=args.Contains("--dark") ? "dark" : "light", sampleCount=elapsed.Count,hitRegions=points.Count,medianMs=elapsed[elapsed.Count/2],
                p95Ms=elapsed[(int)(elapsed.Count*.95)],maxMs=elapsed[^1],deferredPaints,
                blockedNetworkEnumeration=true,blockedOutputEnumeration=true };
            Console.WriteLine(JsonSerializer.Serialize(report));
            Check(deferredPaints==0,"Hover paint remained deferred behind queued input");
            Check(report.p95Ms<100,"Hover response stalled during background enumeration");
            if(args.Length>0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))); File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true })); }
            return 0;
            void Move((int X,int Y) point) => SendMessage(hwnd,0x0200,IntPtr.Zero,new IntPtr(point.X | point.Y<<16));
        }
        finally
        {
            Release.Set(); pending?.Wait(2000);
            if(hwnd!=IntPtr.Zero) DestroyWindow(hwnd);
            ((IDisposable)window).Dispose(); ((IDisposable)controller).Dispose(); ((IDisposable)log).Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr hwnd,IntPtr rect,bool erase);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd,IntPtr timer);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
}
