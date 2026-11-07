using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SinkLite.Platform;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SinkLite.Core
{
    /// <summary>
    /// 四段状态机。核心设计针对原实现的两个已观测故障：
    ///   1) 连接失败：连接对象持久化，绝不为重试而销毁重建；失败按可重试性分类并退避。
    ///   2) 连上没声音：显式区分控制层 / 链路层 / 音频层，界面不再把控制层成功当成功。
    /// 所有状态变更只在主循环线程发生，外部命令经队列串行化。
    /// </summary>
    internal sealed class SinkController : IDisposable
    {
        private sealed class Command
        {
            public string Kind;
            public string DeviceId;
        }

        private readonly Policy policy;
        private readonly Log log;
        private readonly FlowMonitor flow;
        private Platform.RouteControl route;

        private readonly Channel<Command> commands =
            Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });

        private readonly object stateSync = new object();

        private SinkSnapshot current = new SinkSnapshot();
        private IReadOnlyList<SinkDevice> devices = new List<SinkDevice>();
        private CancellationTokenSource loopCancellation;
        private Task loopTask;

        // 仅在主循环线程访问
        private AudioPlaybackConnection connection;
        private string armedDeviceId;
        private bool wantConnected;
        private int retryCount;
        private DateTime? retryAt;
        private DateTime? armedAt;
        private DateTime? linkReadyAt;
        private bool linkHinted;
        private bool flowHinted;
        private bool faultReported;
        private bool devicesEnumerated;
        private DateTime lastDeviceScan;
        private readonly EndpointWatcher renderWatcher = new EndpointWatcher();

        private string lastEndpointName;
        private string lastRenderName;

        public SinkController(Policy policy, Log log)
        {
            this.policy = policy;
            this.log = log;
            flow = new FlowMonitor(policy);
        }

        public event EventHandler<SinkSnapshot> SnapshotChanged;

        public SinkSnapshot Current
        {
            get
            {
                lock (stateSync)
                {
                    return current.Clone();
                }
            }
        }

        public IReadOnlyList<SinkDevice> Devices
        {
            get
            {
                lock (stateSync)
                {
                    return devices;
                }
            }
        }

        /// <summary>Attach read-only output inventory for the Wi-Fi endpoint preference.</summary>
        public void AttachRoute(Platform.RouteControl routeControl)
        {
            route = routeControl;
            PreferredRenderId = Platform.Settings.Load().PreferredRenderId;
        }

        public string PreferredRenderId { get; set; }

        /// <summary>Select the app's Wi-Fi endpoint without changing Windows defaults.</summary>
        public bool SetPreferredRender(string endpointId, string endpointName, bool persist = true)
        {
            if(route==null || string.IsNullOrEmpty(endpointId) || !route.ListPhysicalRenders().Any(e=>e.Id==endpointId)) return false;
            if(persist) {
                var settings=Platform.Settings.Load(); settings.PreferredRenderId=endpointId;
                if(!settings.Save()) return false;
            }
            PreferredRenderId=endpointId;
            log.Info("Wifi.OutputPreferred",("endpoint",endpointName));
            return true;
        }

        /// <summary>按名称片段设置首选输出，返回实际命中的端点名称；未命中返回 null。</summary>
        public string SetPreferredRenderByName(string nameFragment)
        {
            if (string.IsNullOrWhiteSpace(nameFragment))
            {
                return null;
            }

            foreach (EndpointInfo e in ListPhysicalOutputs())
            {
                if (!string.IsNullOrEmpty(e.Name) &&
                    e.Name.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return SetPreferredRender(e.Id, e.Name) ? e.Name : null;
                }
            }
            return null;
        }

        /// <summary>当前所有可选的物理（非虚拟）输出端点。</summary>
        public System.Collections.Generic.IReadOnlyList<EndpointInfo> ListPhysicalOutputs()
        {
            if (route == null)
            {
                return new System.Collections.Generic.List<EndpointInfo>();
            }
            return route.ListPhysicalRenders();
        }

        public void Start()
        {
            loopCancellation = new CancellationTokenSource();
            loopTask = Task.Run(() => RunLoopAsync(loopCancellation.Token));
            log.Info("App.Start", ("version", typeof(SinkController).Assembly.GetName().Version?.ToString(3)),
                ("windowsVersion", Environment.OSVersion.Version.ToString()));
        }

        public void RequestConnect(string deviceId)
        {
            commands.Writer.TryWrite(new Command { Kind = "connect", DeviceId = deviceId });
        }

        public void RequestDisconnect()
        {
            commands.Writer.TryWrite(new Command { Kind = "disconnect" });
        }

        public void RequestRefreshDevices()
        {
            commands.Writer.TryWrite(new Command { Kind = "refresh" });
        }

        public void RequestShutdown()
        {
            commands.Writer.TryWrite(new Command { Kind = "shutdown" });
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            try
            {
                InitializeWinRt();
                await RefreshDevicesAsync();
            }
            catch (Exception ex)
            {
                log.Error("Loop.InitFailed", ("error", ex.Message));
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await DrainCommandsAsync();

                    // 设备列表变化很慢，不必频繁枚举：每次枚举都会产生大量 WinRT COM 对象。
                    if (DateTime.Now - lastDeviceScan > TimeSpan.FromSeconds(120))
                    {
                        lastDeviceScan = DateTime.Now;
                        await RefreshDevicesAsync();
                    }

                    await StepAsync();
                }
                catch (Exception ex)
                {
                    log.Error("Loop.IterationFailed", ("error", ex.Message));
                }

                try
                {
                    await Task.Delay(policy.SampleInterval, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Cleanup();
        }

        private async Task DrainCommandsAsync()
        {
            while (commands.Reader.TryRead(out Command command))
            {
                switch (command.Kind)
                {
                    case "connect":
                        await HandleConnectAsync(command.DeviceId);
                        break;
                    case "disconnect":
                        await HandleDisconnectAsync();
                        break;
                    case "refresh":
                        await RefreshDevicesAsync();
                        break;
                    case "shutdown":
                        HandleDisconnectAsync().GetAwaiter().GetResult();
                        return;
                }
            }
        }

        private void InitializeWinRt()
        {
            log.Info("WinRt.Ready");
        }

        private async Task RefreshDevicesAsync()
        {
            try
            {
                // 使用官方设备枚举：它给出的 ID 与名称最可靠。
                // 曾尝试改为注册表构造 ID 以省掉 windows.storage.dll，
                // 实测该 DLL 由 Windows.Media.Audio 本身固有加载，改用注册表并无收益，
                // 且注册表里的名称是间接字符串（会丢失自定义前缀），故保留官方方式。
                string selector = AudioPlaybackConnection.GetDeviceSelector();
                DeviceInformationCollection found = await DeviceInformation.FindAllAsync(selector);

                List<SinkDevice> list = new List<SinkDevice>();
                foreach (DeviceInformation info in found)
                {
                    list.Add(new SinkDevice(info.Id, info.Name));
                }

                lock (stateSync)
                {
                    devices = list;
                }

                devicesEnumerated = list.Count > 0;

                if (list.Count > 0 && string.IsNullOrEmpty(SnapshotDeviceId))
                {
                    SetSnapshot(s =>
                    {
                        s.DeviceId = list[0].Id;
                        s.DeviceName = list[0].Name;
                        s.Detail = "已发现设备，可连接";
                    });
                }

                log.Info("Devices.Enumerated", ("count", list.Count));
            }
            catch (Exception ex)
            {
                log.Error("Devices.EnumerateFailed", ("error", ex.Message));
            }
        }

        private string SnapshotDeviceId
        {
            get
            {
                lock (stateSync)
                {
                    return current.DeviceId;
                }
            }
        }

        private async Task HandleConnectAsync(string deviceId)
        {
            SinkDevice target = null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                target = Devices.FirstOrDefault(d => d.Id == deviceId);
            }
            if (target == null)
            {
                target = Devices.FirstOrDefault();
            }
            if (target == null)
            {
                await RefreshDevicesAsync();
                target = Devices.FirstOrDefault();
            }

            if (target == null)
            {
                SetSnapshot(s =>
                {
                    s.State = SinkState.Faulted;
                    s.Detail = "没有可用的音频接收设备";
                    s.Hint = "请先在 Windows 蓝牙设置中配对手机。";
                });
                log.Warn("Connect.NoDevice");
                return;
            }

            if (connection != null && armedDeviceId != target.Id)
            {
                await ReleaseAsync("switch device");
            }
            wantConnected = true;
            retryCount = 0;
            retryAt = null;
            faultReported = false;
            SetTarget(target);
            log.Info("Connect.Requested", ("device", target.Name));
            await ConnectInternalAsync(target);
        }

        private void SetTarget(SinkDevice target)
        {
            SetSnapshot(s =>
            {
                s.DeviceId = target.Id;
                s.DeviceName = target.Name;
            });
        }

        private async Task HandleDisconnectAsync()
        {
            wantConnected = false;
            retryAt = null;
            retryCount = 0;
            await ReleaseAsync("user disconnect");
            SetSnapshot(s =>
            {
                s.State = SinkState.Idle;
                s.Detail = "未连接";
                s.Hint = null;
                s.Peak = 0f;
            });
        }

        private async Task ConnectInternalAsync(SinkDevice target)
        {
            SetSnapshot(s =>
            {
                s.State = SinkState.Busy;
                s.Attempt = retryCount + 1;
                s.Detail = "正在连接…";
                s.Hint = null;
                s.LastErrorCode = null;
            });

            // 1) 获取或复用连接对象：整个进程生命周期内每个设备只创建一个。
            AudioPlaybackConnection conn = connection;
            bool freshlyCreated = false;
            if (conn == null || armedDeviceId != target.Id)
            {
                try
                {
                    conn = AudioPlaybackConnection.TryCreateFromId(target.Id);
                }
                catch (Exception ex)
                {
                    Fail("CreateException", ex.Message);
                    return;
                }

                if (conn == null)
                {
                    Fail("NotSupported", "设备不支持蓝牙音频接收");
                    return;
                }

                connection = conn;
                armedDeviceId = target.Id;
                freshlyCreated = true;
            }

            // 2) 仅在首次创建时武装（StartAsync）。重试不重复武装，避免与手机端协商互相打断。
            if (freshlyCreated)
            {
                try
                {
                    Task armTask = conn.StartAsync().AsTask();
                    Task completed = await Task.WhenAny(armTask, Task.Delay(policy.ArmTimeout));
                    if (completed != armTask)
                    {
                        await ReleaseAsync("arm timeout");
                        Fail("ArmTimeout", "武装接收通道超时");
                        return;
                    }
                    await armTask;
                    log.Info("Connect.Armed", ("device", target.Name));
                }
                catch (Exception ex)
                {
                    await ReleaseAsync("arm exception");
                    Fail("ArmException", ex.Message);
                    return;
                }
            }

            // 3) 打开连接。失败按可重试性分类。
            AudioPlaybackConnectionOpenResultStatus status;
            try
            {
                var openOperation = conn.OpenAsync();
                Task<AudioPlaybackConnectionOpenResult> openTask = openOperation.AsTask();
                Task done = await Task.WhenAny(openTask, Task.Delay(policy.OpenTimeout));
                if (done != openTask)
                {
                    try
                    {
                        openOperation.Cancel();
                    }
                    catch
                    {
                    }

                    // 关键修复：被取消的 OpenAsync 会让连接对象进入不可恢复状态，
                    // 继续复用它重试必然一直失败。必须丢弃，让下次重试重新创建并武装。
                    await ReleaseAsync("open timeout");
                    Fail("OpenTimeout", "打开连接超时（手机可能未响应）");
                    return;
                }

                status = (await openTask).Status;
            }
            catch (Exception ex)
            {
                await ReleaseAsync("open exception");
                Fail("OpenException", ex.Message);
                return;
            }

            if (status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                // 关键：这类失败不是超时，连接对象没有被 Cancel 污染，仍可复用。
                // 若在这里 Dispose 并重建，就会形成每 2~8 秒一次的“拆-建”循环，
                // 反而持续把手机侧的 A2DP 链路搞乱，导致每次都立刻失败。
                Fail("OpenStatus." + status, "打开连接失败：" + status, true);
                return;
            }

            // 4) 稳定观察：给状态迁移留出余量，避免把"稍慢"误判为"秒断"。
            await WaitForOpenedAsync(conn, policy.OpenStabilityDelay + TimeSpan.FromMilliseconds(1200));
            if (conn.State != AudioPlaybackConnectionState.Opened)
            {
                // 同上：状态未就绪不等于对象损坏，保留对象稍后重试
                Fail("NotOpened", "连接未能保持打开状态", true);
                return;
            }

            retryCount = 0;
            retryAt = null;
            armedAt = DateTime.Now;
            linkReadyAt = null;
            linkHinted = false;
            flowHinted = false;
            lastEndpointName = null;
            lastRenderName = null;

            // 复用共享的端点观察器：之前每次 new EndpointWatcher() 都不释放，
            // 是长时间运行后句柄/内存膨胀的主要来源之一。
            bool virtualWarning = false;
            EndpointInfo render = null;
            try
            {
                render = renderWatcher.GetDefaultRender();
            }
            catch
            {
            }
            if (render != null && render.IsVirtual)
            {
                virtualWarning = true;
            }

            SetSnapshot(s =>
            {
                s.State = SinkState.Armed;
                s.Detail = "已就绪，等待手机发送音频";
                s.Peak = 0f;
                s.Attempt = 0;
                s.LastErrorCode = null;
                s.Hint = virtualWarning
                    ? "注意：当前默认输出是虚拟声卡（" + render.Name + "），手机声音可能听不到。"
                    : "请在手机上播放音乐以开始传输。";
            });
            log.Info("Connect.Armed", ("device", target.Name), ("fresh", freshlyCreated));
        }

        /// <summary>在超时内轮询等待连接进入 Opened，避免把稍慢的状态迁移误判为失败。</summary>
        private static async Task WaitForOpenedAsync(AudioPlaybackConnection conn, TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                try
                {
                    if (conn.State == AudioPlaybackConnectionState.Opened)
                    {
                        return;
                    }
                }
                catch
                {
                    return;
                }
                await Task.Delay(100);
            }
        }

        /// <summary>每个采样周期执行一次：端点归因、峰值判定、退避推进。</summary>
        private async Task StepAsync()
        {
            if (!wantConnected)
            {
                return;
            }

            // 必须先处理退避重试：失败时连接对象已被释放（connection == null），
            // 如果把 connection 判空放在前面，重试就永远不会执行，程序会卡在失败态。
            if (retryAt.HasValue && DateTime.Now >= retryAt.Value)
            {
                retryAt = null;

                // 注意：失败时 ReleaseAsync 会把 armedDeviceId 清空（以便重建连接对象），
                // 因此重试目标必须取自当前快照，否则会永远找不到设备、重试静默失效。
                string retryId = !string.IsNullOrEmpty(armedDeviceId)
                    ? armedDeviceId
                    : Current.DeviceId;
                SinkDevice target = string.IsNullOrEmpty(retryId)
                    ? Devices.FirstOrDefault()
                    : Devices.FirstOrDefault(d => d.Id == retryId);
                if (target != null)
                {
                    log.Info("Connect.Retrying", ("device", target.Name), ("attempt", retryCount + 1));
                    await ConnectInternalAsync(target);
                    return;
                }
            }

            // 没有连接对象且不在退避中：无事可做
            if (connection == null)
            {
                return;
            }

            FlowFinding finding = flow.Sample();

            if (!string.IsNullOrEmpty(finding.RenderEndpointName) &&
                finding.RenderEndpointName != lastRenderName)
            {
                lastRenderName = finding.RenderEndpointName;
                log.Info("Audio.DefaultRender", ("name", finding.RenderEndpointName), ("virtual", finding.RenderIsVirtual));
            }

            bool hasLink = finding.HasBluetoothEndpoint;
            bool flowing = finding.AudioFlowing;

            if (hasLink && lastEndpointName != finding.BluetoothEndpointName)
            {
                lastEndpointName = finding.BluetoothEndpointName;
                log.Info("Audio.LinkEndpointAppeared", ("name", finding.BluetoothEndpointName));
            }
            else if (!hasLink && !string.IsNullOrEmpty(lastEndpointName))
            {
                log.Info("Audio.LinkEndpointGone", ("name", lastEndpointName));
                lastEndpointName = null;
            }

            if (flowing)
            {
                if (Current.State != SinkState.Flowing)
                {
                    linkReadyAt = linkReadyAt ?? DateTime.Now;
                    SetSnapshot(s =>
                    {
                        s.State = SinkState.Flowing;
                        s.Detail = "音频传输中";
                        s.Hint = null;
                        s.Peak = finding.Peak;
                    });
                    log.Info("Audio.Flowing", ("peak", finding.Peak), ("render", finding.RenderEndpointName));
                }
                else
                {
                    SetSnapshot(s => s.Peak = finding.Peak);
                }
                flowHinted = false;
                return;
            }

            SetSnapshot(s => s.Peak = 0f);

            if (hasLink)
            {
                linkReadyAt = linkReadyAt ?? DateTime.Now;
                if (Current.State != SinkState.LinkReady && Current.State != SinkState.Flowing)
                {
                    SetSnapshot(s =>
                    {
                        s.State = SinkState.LinkReady;
                        s.Detail = "已建立音频链路，等待播放";
                        s.Hint = "若手机正在播放却听不到，请检查是否被其他输出设备抢占。";
                    });
                    log.Info("Link.Ready", ("endpoint", finding.BluetoothEndpointName));
                }

                if (!flowHinted &&
                    linkReadyAt.HasValue &&
                    DateTime.Now - linkReadyAt.Value > policy.FlowWaitHintDelay &&
                    finding.RenderIsVirtual)
                {
                    flowHinted = true;
                    SetSnapshot(s =>
                    {
                        s.State = SinkState.LinkReady;
                        s.Detail = "音频链路已建立，但输出指向虚拟声卡";
                        s.Hint = "当前默认输出是 " + finding.RenderEndpointName + "，请切换到真实扬声器或耳机。";
                    });
                    log.Warn("Link.VirtualRender", ("render", finding.RenderEndpointName));
                }
                return;
            }

            // 没有激活的音频端点：手机尚未真正开始发送音频。
            // 注意这里必须区分“设备存在但未激活”与“完全没有设备”，否则会误导用户。
            bool presentButInactive = finding.BluetoothEndpointPresent;

            if (Current.State == SinkState.LinkReady)
            {
                // 之前判定为链路就绪，但端点已不再 Active，退回等待状态
                SetSnapshot(s =>
                {
                    s.State = SinkState.Armed;
                    s.Detail = "等待手机发送音频";
                    s.Hint = "检测到手机音频设备，但音频通道未激活。请在手机上播放音乐。";
                });
                log.Info("Link.Inactive", ("endpoint", finding.BluetoothEndpointName));
                linkHinted = false;
                return;
            }

            if (!linkHinted &&
                armedAt.HasValue &&
                DateTime.Now - armedAt.Value > policy.LinkWaitHintDelay)
            {
                linkHinted = true;
                SetSnapshot(s =>
                {
                    s.State = SinkState.Armed;
                    s.Detail = presentButInactive
                        ? "已检测到手机音频设备，但未激活"
                        : "手机尚未发送音频";
                    s.Hint = presentButInactive
                        ? "请在手机上播放音乐，并确认手机的音频输出指向本机（而不是手机扬声器或其他耳机）。"
                        : "请确认：① 手机正在播放；② 手机蓝牙输出指向本机；③ Windows 蓝牙设置中允许该设备用于音频。";
                });
                log.Warn("Link.WaitingForPhone",
                    ("armedFor", (DateTime.Now - armedAt.Value).TotalSeconds),
                    ("present", presentButInactive));
            }
        }

        /// <param name="keepConnection">
        /// 为 true 时保留已武装的连接对象，仅退避后重试 OpenAsync；
        /// 用于非超时类失败（对象未被 Cancel 污染），避免拆-建循环。
        /// </param>
        private void Fail(string code, string message, bool keepConnection = false)
        {
            retryCount++;
            log.Warn("Connect.Failed", ("code", code), ("message", message), ("attempt", retryCount));

            // 只有真正不可能成功的错误才限制重试次数。
            // OpenStatus.UnknownFailure 等属于瞬时失败（手机忙、链路抖动），应当继续重试。
            bool fatal = code == "NotSupported" || code == "CreateException";
            int maxRetries = fatal ? 1 : policy.MaxRetries;

            if (retryCount <= maxRetries)
            {
                int index = Math.Min(retryCount - 1, policy.BackoffSteps.Length - 1);
                TimeSpan delay = policy.BackoffSteps[index];

                // 若 keepConnection 为 true，连接对象保持非空且 armedDeviceId 未清空，
                // 重试时 ConnectInternalAsync 会走“复用已有对象”分支，不再重新武装。
                retryAt = DateTime.Now + delay;
                SetSnapshot(s =>
                {
                    s.State = SinkState.Backoff;
                    s.Attempt = retryCount;
                    s.LastErrorCode = code;
                    s.Detail = "连接失败，正在重试（第 " + retryCount + " 次）";
                    s.Hint = message;
                });
                return;
            }

            // 重试耗尽：转为待机，把发起权交还手机与 Windows 自动连接，而不是死循环重连。
            // 此时才真正释放连接对象，避免留下僵死链路。
            try
            {
                if (connection != null)
                {
                    connection.Dispose();
                    connection = null;
                    armedDeviceId = null;
                }
            }
            catch
            {
            }

            wantConnected = false;
            retryAt = null;
            SetSnapshot(s =>
            {
                s.State = SinkState.Faulted;
                s.Attempt = retryCount;
                s.LastErrorCode = code;
                s.Detail = "连接失败，已转入待机";
                s.Hint = BuildHint(code, message);
            });
            if (!faultReported)
            {
                faultReported = true;
                log.Error("Connect.GaveUp", ("code", code), ("attempts", retryCount));
            }
        }

        private static string BuildHint(string code, string message)
        {
            if (code.StartsWith("OpenStatus.", StringComparison.Ordinal))
            {
                // 实测：UnknownFailure 会在数毫秒内由本地立即返回，
                // 此时电脑侧蓝牙正常、手机也已配对，问题在手机没有接受音频连接请求。
                return "电脑侧正常，是手机没有接受音频连接。请在手机上：① 关闭再打开蓝牙；" +
                       "② 在已配对列表中点一下本电脑重新连接；③ 仍不行则删除配对后重新配对。";
            }

            switch (code)
            {
                case "OpenTimeout":
                    return "手机可能未响应。请把手机靠近电脑、唤醒屏幕后重试。";
                case "ArmTimeout":
                    return "蓝牙栈无响应。可尝试重新开关电脑蓝牙后重试。";
                case "NotSupported":
                    return "该设备不支持蓝牙音频接收，请换用已配对的手机。";
                case "NotOpened":
                    return "音频通道未建立。请确认手机正在播放，并在手机上把音频输出指向本电脑。";
                default:
                    return "请确认手机在附近且蓝牙已开启。错误：" + code;
            }
        }

        private async Task ReleaseAsync(string reason)
        {
            AudioPlaybackConnection conn = connection;
            connection = null;
            armedDeviceId = null;
            armedAt = null;
            linkReadyAt = null;
            linkHinted = false;
            flowHinted = false;

            if (conn == null)
            {
                return;
            }

            try
            {
                Task disposeTask = Task.Run(() =>
                {
                    try
                    {
                        conn.Dispose();
                    }
                    catch
                    {
                    }
                });
                Task done = await Task.WhenAny(disposeTask, Task.Delay(policy.ReleaseTimeout));
                log.Info("Connect.Released", ("reason", reason), ("clean", done == disposeTask));
            }
            catch (Exception ex)
            {
                log.Warn("Connect.ReleaseFailed", ("reason", reason), ("error", ex.Message));
            }
        }

        private void Cleanup()
        {
            try
            {
                if (connection != null)
                {
                    connection.Dispose();
                    connection = null;
                }
            }
            catch
            {
            }

            try
            {
                flow.Dispose();
            }
            catch
            {
            }

            try
            {
                renderWatcher.Dispose();
            }
            catch
            {
            }
        }

        private void SetSnapshot(Action<SinkSnapshot> mutate)
        {
            SinkSnapshot updated;
            lock (stateSync)
            {
                SinkSnapshot next = current.Clone();
                mutate(next);
                next.ChangedAt = DateTime.Now;
                current = next;
                updated = next.Clone();
            }

            try
            {
                SnapshotChanged?.Invoke(this, updated);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                loopCancellation?.Cancel();
            }
            catch
            {
            }

            try
            {
                loopTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            try
            {
                commands.Writer.TryComplete();
            }
            catch
            {
            }

            try
            {
                loopCancellation?.Dispose();
            }
            catch
            {
            }
        }
    }
}
