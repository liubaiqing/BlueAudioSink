using System.Net;
using System.Reflection;
using SinkLite.Core;
using SinkLite.Platform;

static class VolumeRegression
{
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    static byte[] Payload(params uint[] values)=>values.SelectMany(BitConverter.GetBytes).ToArray();
    static byte[] Request(byte type,uint seq,byte[] body)=>WifiTransport.BuildPacket(type,0,seq,0,body);
    static byte[] Handle(WifiVolumeControl c,byte[] packet,string peer,out int? value)
    {WifiTransport.TryReadHeader(packet,packet.Length,out var h);return c.Handle(packet,packet.Length,h,peer,out value);}
    static byte[] Set(byte[] state,uint id,int volume,uint token=123)
    {return Request(9,id,Payload(token,WifiTransport.ReadUInt32(state,20),WifiTransport.ReadUInt32(state,24)).Concat(new[]{(byte)volume}).ToArray());}
    public static void Run(Log log,Action<string,Action> Test)
    {
        Test("Remote volume query reads PC value without writing",()=>{
            var c=new WifiVolumeControl();c.Update(25,false);
            var state=Handle(c,Request(8,0,Payload(123)),"phone",out var v);
            Check(v==null&&state.Length==30&&state[28]==25&&WifiTransport.ReadUInt32(state,16)==123,"query altered value or wire format");
        });
        Test("Remote volume applies once and duplicate retry is idempotent",()=>{
            var c=new WifiVolumeControl();c.Update(25,false);var initial=Handle(c,Request(8,0,Payload(123)),"phone",out _);
            var set=Set(initial,1,30);var first=Handle(c,set,"phone",out var v);Check(v==30&&first[28]==30,"set failed");
            var retry=Handle(c,set,"phone",out v);Check(v==null&&retry.SequenceEqual(first),"retry applied twice");
        });
        Test("PC exclusive mute rejects a delayed volume raise",()=>{
            var c=new WifiVolumeControl();c.Update(25,false);var initial=Handle(c,Request(8,0,Payload(123)),"phone",out _);
            var delayed=Set(initial,1,80);c.Update(0,true);var reply=Handle(c,delayed,"phone",out var v);
            Check(v==null&&reply[28]==0&&reply[29]==1,"stale drag undid exclusive mute");
        });
        Test("PC edits invalidate a prior SET success flag even when volume matches",()=>{
            var c=new WifiVolumeControl();c.Update(25,false);var initial=Handle(c,Request(8,0,Payload(123)),"phone",out _);
            var set=Set(initial,1,0);var ack=Handle(c,set,"phone",out _);Check((ack[29]&2)!=0,"successful SET not marked");
            c.Update(0,true);var retry=Handle(c,set,"phone",out var v);Check(v==null&&(retry[29]&2)==0&&retry[28]==0,"old success undid PC mute intent");
        });
        Test("Remote volume rejects malformed, foreign, old token and reordered writes",()=>{
            var c=new WifiVolumeControl();c.Update(25,false);var initial=Handle(c,Request(8,0,Payload(123)),"phone",out _);
            Check(Handle(c,Set(initial,1,101),"phone",out _)==null,"out of range");
            Check(Handle(c,Set(initial,1,20),"other",out _)==null,"foreign peer");
            Check(Handle(c,Set(initial,1,20).Take(28).ToArray(),"phone",out _)==null,"short payload");
            var current=Handle(c,Set(initial,2,20),"phone",out _);
            var reply=Handle(c,Set(current,1,80),"phone",out var v);Check(v==null&&reply[28]==20,"reordered write won");
            Handle(c,Request(8,0,Payload(456)),"phone",out _);
            Check(Handle(c,Set(current,3,80),"phone",out _)==null,"old connection token");
        });
        Test("Receiver gates volume by current source and fresh PONG without extending liveness",()=>{
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;var type=typeof(WifiReceiver);
            using var rx=new WifiReceiver(log,0,0,80,200,"test",new WifiOutputOptions(false,25));
            void Put(string name,object value)=>type.GetField(name,flags).SetValue(rx,value);
            var from=new IPEndPoint(IPAddress.Loopback,45678);Put("remote",from);Put("lastPongTicks",1L);Put("lastAnyTicks",7L);
            var initial=Handle((WifiVolumeControl)type.GetField("remoteVolume",flags).GetValue(rx),Request(8,0,Payload(123)),from.ToString(),out _);
            var set=Set(initial,1,30);
            void Deliver(IPEndPoint source,long now)=>type.GetMethod("HandlePacket",flags).Invoke(rx,new object[]{set,set.Length,source,now,true});
            Deliver(new IPEndPoint(IPAddress.Loopback,45677),2);Check(rx.GetSnapshot().VolumePercent==25,"foreign accepted");
            Deliver(from,6*System.Diagnostics.Stopwatch.Frequency);Check(rx.GetSnapshot().VolumePercent==25,"stale heartbeat accepted");
            int changes=0;rx.RemoteVolumeChanged+=()=>changes++;Deliver(from,2);
            Check(rx.GetSnapshot().VolumePercent==30&&changes==1&&(long)type.GetField("lastAnyTicks",flags).GetValue(rx)==7,"gain not updated or volume kept audio alive");
        });
        Test("Real UDP volume query, set and PC notification round trip without audio playback",()=>{
            using var rx=new WifiReceiver(log,0,0,80,200,"volume-test",new WifiOutputOptions(false,25));
            Check(rx.Start(),"receiver bind");
            var socket=(System.Net.Sockets.Socket)typeof(WifiReceiver).GetField("controlSocket",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(rx);
            var target=new IPEndPoint(IPAddress.Loopback,((IPEndPoint)socket.LocalEndPoint).Port);
            using var phone=new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback,0));phone.Client.ReceiveTimeout=200;
            void Send(byte[] bytes)=>phone.Send(bytes,bytes.Length,target);
            Send(Request(7,0,System.Text.Encoding.UTF8.GetBytes("test-phone")));
            byte[] ReceiveState(Func<byte[],bool> accept,byte[] resend=null){
                var time=System.Diagnostics.Stopwatch.StartNew();
                while(time.ElapsedMilliseconds<6000){
                    if(resend!=null)Send(resend);
                    try{
                        IPEndPoint from=null;var b=phone.Receive(ref from);WifiTransport.TryReadHeader(b,b.Length,out var h);
                        if(h.Type==4)Send(WifiTransport.BuildPong(h.TimestampUs,123));
                        if(h.Type==10&&accept(b))return b;
                    }catch(System.Net.Sockets.SocketException ex)when(ex.SocketErrorCode==System.Net.Sockets.SocketError.TimedOut){}
                }
                throw new Exception("UDP volume state timed out");
            }
            var initial=ReceiveState(_=>true,Request(8,0,Payload(123)));Check(initial[28]==25,"query did not read PC value");
            var changed=ReceiveState(b=>b[28]==20,Set(initial,1,20));Check(rx.GetSnapshot().VolumePercent==20,"UDP SET not applied");
            rx.SetOutputOptions(new WifiOutputOptions(false,0));var muted=ReceiveState(b=>b[28]==0);
            Check(WifiTransport.ReadUInt32(muted,24)!=WifiTransport.ReadUInt32(changed,24),"PC notification revision unchanged");
            Check(!rx.GetSnapshot().OutputActive,"volume control opened audio device");
        });
    }
}
