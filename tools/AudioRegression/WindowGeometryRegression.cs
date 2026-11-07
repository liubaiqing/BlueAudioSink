using SinkLite.Platform;
internal static class WindowGeometryRegression
{
    public static void Run(Action<string,Action> test)
    {
        test("Window restore clamps removed monitors, negative desktops, DPI and corrupt bounds",()=>{
            var s=new Settings {WindowX=-1700,WindowY=80,WindowWidth=700,WindowHeight=600,WindowDpi=96};
            var b=WindowGeometry.Restore(s,new(-1920,40,1920,1040),96);
            if(b!=new WindowBounds(-1700,80,700,600)) throw new Exception("Negative desktop lost");
            b=WindowGeometry.Restore(s,new(0,40,1920,1040),96);
            if(b.X!=0 || b.Y!=80) throw new Exception("Removed monitor not recovered");
            b=WindowGeometry.Restore(s,new(0,40,1920,1400),144);
            if(b.Width!=1050 || b.Height!=900) throw new Exception("DPI logical size changed");
            b=WindowGeometry.Restore(s,new(40,30,400,300),144);
            if(b!=new WindowBounds(40,30,400,300)) throw new Exception("Small workspace clipped");
            s.WindowX=int.MinValue; s.WindowWidth=int.MaxValue;
            if(WindowGeometry.Valid(s)) throw new Exception("Corrupt dimensions accepted");
            b=WindowGeometry.Restore(s,new(0,0,1920,1080),96);
            if(b.Width!=520 || b.Height!=500 || b.X<0) throw new Exception("Invalid configuration not reset");
        });
        test("Window geometry round trips independently without losing other preferences",()=>{
            string path=Path.Combine(Path.GetTempPath(),"BAS-window-"+Guid.NewGuid()+".ini");
            try {
                var s=new Settings {WindowX=-700,WindowY=80,WindowWidth=800,WindowHeight=700,WindowDpi=144,WindowMaximized=true,WifiVolume=21};
                if(!s.Save(path)) throw new Exception("Isolated settings save failed");
                var restored=Settings.Load(path);
                if(restored.WindowX!=-700 || restored.WindowWidth!=800 || restored.WindowDpi!=144 || !restored.WindowMaximized || restored.WifiVolume!=21) throw new Exception("Geometry or unrelated preference lost");
                var copy=restored.Copy(); copy.WindowX=300;
                if(restored.WindowX!=-700) throw new Exception("Snapshot mutated");
            } finally { File.Delete(path); }
        });
    }
}
