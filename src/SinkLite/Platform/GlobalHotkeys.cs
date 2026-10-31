using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SinkLite.Platform
{
    internal readonly record struct HotkeyBinding(int Modifiers, int Key)
    {
        public bool Empty => Key == 0 && Modifiers == 0;
        public bool Valid => Empty || ((Modifiers & ~7) == 0 && (Modifiers & 3) != 0 &&
            ((Key >= 0x20 && Key <= 0x28) || (Key >= 0x30 && Key <= 0x39) || (Key >= 0x41 && Key <= 0x5A) || (Key >= 0x70 && Key <= 0x87 && Key != 0x7B)));
        public string Encode() => Empty ? "" : Modifiers.ToString(CultureInfo.InvariantCulture)+":"+Key.ToString(CultureInfo.InvariantCulture);
        public static HotkeyBinding Parse(string value)
        {
            var parts = (value ?? "").Split(':');
            if(parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mod) &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var key))
            { var binding = new HotkeyBinding(mod, key); if(binding.Valid) return binding; }
            return default;
        }
        public override string ToString() => Empty ? "未设置" :
            ((Modifiers & 2) != 0 ? "Ctrl + " : "") + ((Modifiers & 1) != 0 ? "Alt + " : "") +
            ((Modifiers & 4) != 0 ? "Shift + " : "") + (Key switch {
                0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
                0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
                _ => Key >= 0x70 ? "F"+(Key-0x6F) : ((char)Key).ToString()
            });
    }

    internal interface IHotkeyRegistry
    {
        bool Register(int id, HotkeyBinding binding);
        void Unregister(int id);
    }
    internal sealed class NativeHotkeyRegistry : IHotkeyRegistry
    {
        private readonly IntPtr window;
        public NativeHotkeyRegistry(IntPtr window) { this.window = window; }
        [DllImport("user32.dll", SetLastError=true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        public bool Register(int id, HotkeyBinding binding) => RegisterHotKey(window, id, (uint)(binding.Modifiers | 0x4000), (uint)binding.Key);
        public void Unregister(int id) => UnregisterHotKey(window, id);
    }

    internal sealed class GlobalHotkeys : IDisposable
    {
        public const int FirstId = 0x5100;
        public static readonly string[] SettingNames = { "hotkeydisconnect", "hotkeyreconnect", "hotkeyvolumeup", "hotkeyvolumedown" };
        private readonly IHotkeyRegistry registry;
        private readonly HotkeyBinding[] bindings = new HotkeyBinding[4];
        private readonly bool[] active = new bool[4];
        public HotkeyBinding this[int index] => bindings[index];
        public bool Active(int index) => active[index];
        public GlobalHotkeys(IHotkeyRegistry registry, HotkeyBinding[] saved)
        {
            this.registry = registry;
            for(int i=0;i<4;i++)
            {
                var value = i < saved.Length && saved[i].Valid ? saved[i] : default;
                bindings[i] = value;
                if(value.Empty) continue;
                bool duplicate = false;
                for(int j=0;j<i;j++) if(bindings[j] == value) duplicate = true;
                active[i] = !duplicate && registry.Register(FirstId+i,value);
            }
        }
        public bool Set(int index, HotkeyBinding value, out string error)
        {
            error = null;
            if(!value.Valid) { error = "请用 Ctrl/Alt 搭配字母、数字、方向键或功能键"; return false; }
            if(!value.Empty) for(int i=0;i<4;i++) if(i!=index && bindings[i]==value)
            { error = "快捷键已被占用，请更换"; return false; }
            if(bindings[index] == value && (value.Empty || active[index])) return true;
            var old = bindings[index]; bool wasActive = active[index];
            if(wasActive) registry.Unregister(FirstId+index);
            active[index] = false;
            if(!value.Empty && !registry.Register(FirstId+index,value))
            {
                if(wasActive) active[index] = registry.Register(FirstId+index,old);
                error = "快捷键已被占用或不可用，请更换"; return false;
            }
            bindings[index] = value; active[index] = !value.Empty; return true;
        }
        public bool Matches(int id, int modifiers, int key)
        {
            int i = id-FirstId;
            return i>=0 && i<4 && active[i] && bindings[i] == new HotkeyBinding(modifiers & 7,key);
        }
        public void Dispose()
        { for(int i=0;i<4;i++) { if(active[i]) registry.Unregister(FirstId+i); active[i]=false; } }
    }
}
