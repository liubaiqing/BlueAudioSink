using System;
using System.Threading;
using System.Threading.Tasks;
namespace SinkLite.Platform
{
    /// <summary>One background writer, latest immutable snapshot wins; UI never waits for disk.</summary>
    internal sealed class SettingsWriter : IDisposable
    {
        private readonly object gate=new();
        private readonly Func<Settings,bool> save;
        private Settings pending;
        private long version;
        private Task worker;
        private bool closing;
        public event Action<long,bool> Completed;
        public SettingsWriter(Func<Settings,bool> save=null) { this.save=save ?? (value=>value.Save()); }
        public long Submit(Settings value)
        {
            lock(gate) {
                if(closing) throw new ObjectDisposedException(nameof(SettingsWriter));
                pending=value.Copy(); long next=++version;
                if(worker==null) worker=Task.Run(WriteLoop);
                return next;
            }
        }
        private async Task WriteLoop()
        {
            while(true) {
                await Task.Delay(160).ConfigureAwait(false);
                Settings value; long next;
                lock(gate) { value=pending; next=version; pending=null; }
                bool ok=false; try { ok=save(value); } catch { }
                Completed?.Invoke(next,ok);
                lock(gate) { if(pending==null) {worker=null; return;} }
            }
        }
        public void Dispose()
        {
            Task final;
            lock(gate) { closing=true; final=worker; }
            // Only shutdown may wait for the final settings write; normal controls never do.
            final?.GetAwaiter().GetResult();
        }
    }
}
