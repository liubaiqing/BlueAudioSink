using SinkLite.Platform;
internal static class SettingsWriterRegression
{
    public static void Run(Action<string,Action> test)
    {
        test("Settings persistence coalesces snapshots without blocking input and reports failure",()=>{
            using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
            using var completed=new ManualResetEventSlim();
            var saved=new List<int>(); bool failed=false;
            using var writer=new SettingsWriter(settings=>{
                entered.Set(); release.Wait(3000); lock(saved) saved.Add(settings.WifiVolume); return settings.WifiVolume!=45;
            });
            writer.Completed+=(id,ok)=>{ if(!ok) failed=true; completed.Set(); };
            var preferences=new Settings { WifiVolume=19 };
            writer.Submit(preferences); preferences.WifiVolume=88;
            if(!entered.Wait(2000)) throw new Exception("Writer did not start");
            var clock=System.Diagnostics.Stopwatch.StartNew();
            for(int i=20;i<=45;i++) writer.Submit(new Settings {WifiVolume=i});
            if(clock.ElapsedMilliseconds>100) throw new Exception("Input waited for blocked storage");
            release.Set();
            if(!SpinWait.SpinUntil(()=>failed,3000)) throw new Exception("Save failure was not reported");
            lock(saved) if(saved[0]!=19 || saved[^1]!=45 || saved.Count>2) throw new Exception("Snapshot mutation or stale queue replay");
        });
    }
}
