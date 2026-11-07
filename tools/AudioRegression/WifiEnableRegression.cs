using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SinkLite.Core;
using SinkLite.Platform;
using SinkLite.UI;

static class WifiEnableRegression
{
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    public static void Run(Log log,Action<string,Action> Test)
    {
        Test("Disabled Wi-Fi tells user to enable and never presents stale links as connected",()=>{
            var view=ConnectionPresentation.Wifi(new WifiSnapshot{Enabled=false,State=SinkState.Idle,Remote="192.168.0.2:45678",ControlAgeMs=10,LastAudioAgeMs=10,OutputActive=true});
            Check(view.Title=="Wi-Fi 已断开"&&view.Description.Contains("启用")&&!view.PhoneReady&&!view.StreamReady&&!view.OutputReady,"disabled UI claims a live link");
            view=ConnectionPresentation.Wifi(new WifiSnapshot{Enabled=true,State=SinkState.Armed});
            Check(view.Title=="等待手机响应"&&!view.PhoneReady,"enabling claimed a confirmed connection");
            using var controller=new WifiController(log,0,80,200,legacyTestTransport:true);
            Check(!controller.Current.Enabled&&controller.Current.TargetMs==80,"disabled state lost configured prefill");
        });
        Test("Wi-Fi disconnect closes both sockets and discovery; explicit enable can listen again",()=>{
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            using var rx=new WifiReceiver(log,0,0,80,200,"enable-test");Check(rx.Start(),"initial bind");
            Socket Get(string name)=>(Socket)typeof(WifiReceiver).GetField(name,flags).GetValue(rx);
            int control=((IPEndPoint)Get("controlSocket").LocalEndPoint).Port;
            int audio=((IPEndPoint)Get("audioSocket").LocalEndPoint).Port;
            rx.Stop();rx.Stop();
            Check(Get("controlSocket")==null&&Get("audioSocket")==null&&!rx.SendOffer()&&!rx.GetSnapshot().OutputActive,"disconnect still has network/output resources");
            using(var first=new UdpClient(new IPEndPoint(IPAddress.Any,control)))
            using(var second=new UdpClient(new IPEndPoint(IPAddress.Any,audio))) { Check(first.Client.IsBound&&second.Client.IsBound,"ports not released"); }
            Check(rx.Start()&&Get("controlSocket").IsBound&&Get("audioSocket").IsBound,"explicit re-enable failed");
        });
    }
}
