using SinkLite.Platform;
using SinkLite.Core;
using System.Net;
using System.Net.Sockets;

static class HotkeyRegression
{
    sealed class Registry : IHotkeyRegistry
    {
        public Dictionary<int,HotkeyBinding> Active = new();
        public HashSet<HotkeyBinding> Busy = new();
        public int Attempts;
        public bool Register(int id,HotkeyBinding value){Attempts++;if(Busy.Contains(value)||Active.Values.Contains(value))return false;Active.Add(id,value);return true;}
        public void Unregister(int id)=>Active.Remove(id);
    }
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run(Log log,Action<string,Action> Test)
    {
        Test("Hotkey defaults do not intercept keys and valid bindings round trip",()=>{
            var defaults=new Settings();var registry=new Registry();using var keys=new GlobalHotkeys(registry,defaults.Hotkeys);
            Check(registry.Attempts==0,"default registered a key");
            var value=new HotkeyBinding(7,0x87);Check(value.Valid&&HotkeyBinding.Parse(value.Encode())==value&&value.ToString()=="Ctrl + Alt + Shift + F24","format");
            Check(new HotkeyBinding(3,0x26).Valid&&new HotkeyBinding(3,0x26).ToString()=="Ctrl + Alt + ↑","arrow binding");
            foreach(var invalid in new[]{"2:123","8:65","4:65","2:0","bad","999:65"})Check(HotkeyBinding.Parse(invalid).Empty,"invalid binding accepted: "+invalid);
        });
        Test("Duplicate hotkey is rejected without altering another action",()=>{
            var registry=new Registry();using var keys=new GlobalHotkeys(registry,new HotkeyBinding[4]);var value=new HotkeyBinding(7,0x86);
            Check(keys.Set(0,value,out _),"initial bind");Check(!keys.Set(1,value,out _)&&keys[1].Empty&&keys.Active(0),"duplicate changed action");
        });
        Test("Conflict prompts for manual change, preserves original and chooses no alternate",()=>{
            var registry=new Registry();using var keys=new GlobalHotkeys(registry,new HotkeyBinding[4]);var old=new HotkeyBinding(7,0x85);var busy=new HotkeyBinding(7,0x86);
            keys.Set(0,old,out _);registry.Busy.Add(busy);Check(!keys.Set(0,busy,out var error)&&error.Contains("请更换"),"conflict not reported");
            Check(keys[0]==old&&keys.Active(0)&&registry.Active.Count==1&&registry.Active.Values.Single()==old,"original changed or alternate chosen");
        });
        Test("Unavailable startup binding stays unchanged and clear releases registration",()=>{
            var registry=new Registry();var value=new HotkeyBinding(7,0x86);registry.Busy.Add(value);
            using var keys=new GlobalHotkeys(registry,new[]{value,default,default,default});Check(keys[0]==value&&!keys.Active(0)&&registry.Attempts==1,"startup adjusted binding");
            Check(keys.Set(0,default,out _)&&keys[0].Empty,"clear failed");
            registry.Busy.Clear();keys.Set(0,value,out _);keys.Set(0,default,out _);Check(registry.Active.Count==0,"clear leaked key");
        });
        Test("Stale hotkey messages cannot invoke a newly bound action and exit releases keys",()=>{
            var registry=new Registry();var keys=new GlobalHotkeys(registry,new HotkeyBinding[4]);keys.Set(2,new HotkeyBinding(7,0x85),out _);
            keys.Set(2,new HotkeyBinding(7,0x86),out _);Check(!keys.Matches(GlobalHotkeys.FirstId+2,7,0x85)&&keys.Matches(GlobalHotkeys.FirstId+2,7,0x86),"stale message accepted");
            keys.Dispose();keys.Dispose();Check(registry.Active.Count==0,"exit leaked registration");
        });
        Test("Native hotkey conflict is rejected and clearing allows reuse by another window",()=>{
            IntPtr a=Win32.CreateWindowEx(0,"STATIC","hotkey-test-a",0,0,0,1,1,IntPtr.Zero,IntPtr.Zero,Win32.GetModuleHandle(null),IntPtr.Zero);
            IntPtr b=Win32.CreateWindowEx(0,"STATIC","hotkey-test-b",0,0,0,1,1,IntPtr.Zero,IntPtr.Zero,Win32.GetModuleHandle(null),IntPtr.Zero);
            try{
                using var first=new GlobalHotkeys(new NativeHotkeyRegistry(a),new HotkeyBinding[4]);using var second=new GlobalHotkeys(new NativeHotkeyRegistry(b),new HotkeyBinding[4]);var value=new HotkeyBinding(7,0x87);
                Check(first.Set(0,value,out _),"native test chord already occupied");Check(!second.Set(0,value,out _)&&second[0].Empty,"native conflict not rejected");
                first.Set(0,default,out _);Check(second.Set(0,value,out _),"clear did not release native key");
            }finally{Win32.DestroyWindow(a);Win32.DestroyWindow(b);}
        });
        Test("Wi-Fi reconnect probes remembered phone without opening playback or accepting another source",()=>{
            using var rx=new WifiReceiver(log,0,0,80,200,"reconnect-test");Check(rx.Start(),"bind");
            using var phone=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));phone.Client.ReceiveTimeout=3000;
            var endpoint=(IPEndPoint)phone.Client.LocalEndPoint;rx.PrepareReconnect(endpoint,"remembered");
            IPEndPoint from=null;var packet=phone.Receive(ref from);WifiTransport.TryReadHeader(packet,packet.Length,out var header);
            Check(header.Type==4&&rx.GetSnapshot().Remote==endpoint.ToString()&&!rx.GetSnapshot().OutputActive,"reconnect failed to probe remembered peer");
            var pong=WifiTransport.BuildPong(header.TimestampUs,123);phone.Send(pong,pong.Length,from);
            Check(SpinWait.SpinUntil(()=>!double.IsNaN(rx.GetSnapshot().RttMs),1000),"phone response not accepted");
            using var intruder=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));var hello=WifiTransport.BuildPacket(7,0,0,0,System.Text.Encoding.UTF8.GetBytes("other"));intruder.Send(hello,hello.Length,from);
            Check(SpinWait.SpinUntil(()=>rx.ListPeers().Count>=2,1000)&&rx.GetSnapshot().Remote==endpoint.ToString(),"another source took over remembered peer");
        });
    }
}
