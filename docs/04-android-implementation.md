# Android 实现规格

包名 `com.blueaudiosink.sender`，Android 10+，Kotlin/Compose。MainActivity 提供电脑选择、手动 IP、缓冲档位、录音授权和开始/停止；底部显示统计和作者 liubaiqing。普通使用不需要开发者设置或 ADB。

## 采集与发送

LinkController 管理应用连接状态及唯一 UdpSender。AudioCaptureService 持有 mediaProjection 前台服务，使用 AudioPlaybackCapture / AudioRecord 获取允许被捕获的应用音频。权限包含录音、屏幕/音频捕获授权，通知权限按系统版本处理。停止/撤销授权必须释放录音和投屏资源。

PcmRecorder 将 PCM 放入环形缓冲并按 5 ms 打包。短读继续攒满 960 字节；环空等待真实采集，不伪造连续序号静音。真实静音照常发送。发送失败消耗源序号并计数；采样时间戳与控制时间戳统一为 BOOTTIME/elapsedRealtimeNanos 时基。缓冲档位用于发送端稳定性取舍，不改变线格式。

## 连接与限制

发现监听 OFFER 并按电脑 IP 去重，6 秒未见自动过期；可手动输入地址。开始前 HELLO 后 START，停止时 STOP；选定电脑时持续回应其 PING。切换电脑先停止当前发送。

部分应用禁止内部音频捕获；捕获授权成功不意味着所有内容都可收音。PONG 只能证明控制路径，不能证明 PC 已经收到 AUDIO 或实际出声。Wi-Fi/后台权限变化时应明确提示重试，不宣称无条件自动恢复。

## 构建与签名

工程位于 android/，使用仓库固定 Gradle wrapper、AGP/Kotlin 版本和 JDK 21，SDK 路径由本地环境提供。

```powershell
./android/gradlew.bat -p android assembleRelease --console=plain
```

复制 android/signing.properties.example 为本地忽略的 signing.properties 并配置独立发布密钥；源码无密码。未配置签名时只能产出未签名 Release，不能当作可安装正式包。当前正式版本 versionName=0.2.4、versionCode=7，产物 artifacts/BlueAudioSink-Sender-0.2.4.apk。更新必须沿用发布密钥并递增版本码；旧测试签名安装无法直接覆盖，需卸载后安装（会清空记忆设备）。

发布前运行 apksigner 验证证书与签名，检查无 debuggable。密钥及凭据仅由维护者离线备份，不提交到仓库。

## 电脑接收音量

缓冲档位下、开始发送按钮上提供0–100%连续滑条和百分比，控制电脑Wi-Fi软件增益，不调整手机媒体音量或电脑总音量。未确认连接时整块隐藏；最近PING确认连接后显示，无新鲜音量状态/旧电脑端时禁用并简短显示等待或需更新电脑端；收到权威值后才可编辑。手势中显示本地拖动值，单在途控制器合并/限频并在松手保留最终值；每秒GET纠正状态，切换目标/断线清空待发意图。电脑本地修改和独占置零同步回显，冲突时以电脑状态为准，不额外申请权限

手机连接确认单独依据既有PING新鲜度（5秒），即使采集仍在运行，控制链失联也隐藏音量区域；这不改变采集服务的后台运行机制
