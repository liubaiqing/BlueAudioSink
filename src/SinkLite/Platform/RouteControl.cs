using System;
using System.Collections.Generic;
using System.Linq;
using SinkLite.Core;
using SinkLite.Platform;

namespace SinkLite.Platform
{
    /// <summary>Read-only output inventory; never changes system defaults.</summary>
    internal sealed class RouteControl
    {
        private static readonly string[] VirtualMarkers = new[]
        {
            "VB-Audio", "CABLE Input", "CABLE Output", "Steam Streaming",
            "虚拟音频", "Nahimic", "WO Mic", "网易"
        };

        private readonly EndpointWatcher watcher;

        public RouteControl(EndpointWatcher watcher)
        {
            this.watcher = watcher;
        }

        /// <summary>是否存在处于活动状态的虚拟渲染端点。</summary>
        public EndpointInfo GetVirtualRender()
        {
            return watcher.GetDefaultRender() is EndpointInfo d && d.IsVirtual ? d : null;
        }

        /// <summary>挑选一个"真实"的渲染端点：优先物理设备，排除虚拟声卡。</summary>
        public EndpointInfo PickPhysicalRender()
        {
            List<EndpointInfo> renders = EndpointRegistry.EnumerateActivePhysicalRenders();

            EndpointInfo physical = renders.FirstOrDefault(e => !e.IsVirtual && !e.IsBluetoothA2dp);
            if (physical != null)
            {
                return physical;
            }

            return renders.FirstOrDefault(e => !e.IsVirtual);
        }

        /// <summary>列出可选的物理输出端点。走注册表读取，避免 NAudio 枚举的句柄泄漏。</summary>
        public IReadOnlyList<EndpointInfo> ListPhysicalRenders()
        {
            return EndpointRegistry.EnumerateActivePhysicalRenders();
        }

        public static bool IsVirtualName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (string marker in VirtualMarkers)
            {
                if (name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

}
