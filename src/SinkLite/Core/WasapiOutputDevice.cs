using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace SinkLite.Core
{
    internal enum AudioOutputMode { None, Shared, Exclusive }
    internal sealed class WifiOutputOptions
    {
        public bool Exclusive { get; }
        public int VolumePercent { get; }
        public string EndpointId { get; }
        public WifiOutputOptions(bool exclusive = false, int volumePercent = 100, string endpointId = null)
        { Exclusive=exclusive;VolumePercent=Math.Clamp(volumePercent,0,100);EndpointId=endpointId ?? string.Empty; }
        public bool SameDeviceAndMode(WifiOutputOptions other) => Exclusive==other.Exclusive && EndpointId==other.EndpointId;
    }
    internal interface IWasapiOutputDevice : IDisposable
    {
        string Name { get; }
        bool SupportsExclusive(WaveFormat format);
        IWavePlayer CreatePlayer(AudioClientShareMode mode);
    }
    internal sealed class WasapiOutputDevice : IWasapiOutputDevice
    {
        public static int BufferMilliseconds(AudioClientShareMode mode) => mode==AudioClientShareMode.Exclusive ? 100 : 50;
        private readonly MMDevice device;
        private WasapiOutputDevice(MMDevice device) {this.device=device;}
        public string Name => device.FriendlyName;
        public static IWasapiOutputDevice Open(string id)
        {
            using var devices=new MMDeviceEnumerator();
            return new WasapiOutputDevice(string.IsNullOrEmpty(id) ? devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Console) : devices.GetDevice(id));
        }
        public bool SupportsExclusive(WaveFormat format)
        { using var client=device.AudioClient;return client.IsFormatSupported(AudioClientShareMode.Exclusive,format); }
        public IWavePlayer CreatePlayer(AudioClientShareMode mode) => new WasapiOut(device,mode,true,BufferMilliseconds(mode));
        public void Dispose()=>device.Dispose();
    }
}
