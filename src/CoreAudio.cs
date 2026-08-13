using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LoudEQ
{
    // ============ GUID 常量 ============
    internal static class AudioGuids
    {
        public static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IMMDeviceEnumerator   = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        public static readonly Guid IID_IMMDeviceCollection   = new Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E");
        public static readonly Guid IID_IMMDevice             = new Guid("D666063F-1587-4E43-81F1-B948E807363F");
        public static readonly Guid IID_IAudioClient          = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient   = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioRenderClient    = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid IID_IPropertyStore        = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        public static readonly Guid IID_IMMNotificationClient = new Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
        public static readonly Guid PKEY_DeviceFriendlyName   = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
        // IPolicyConfig（未公开接口，用于切换系统默认播放设备；Win7~Win11 均可用）
        public static readonly Guid CLSID_PolicyConfigClient  = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");
        public static readonly Guid IID_IPolicyConfig         = new Guid("f8679f50-850a-41cf-9c72-430f290290c8");
    }

    // ============ 常量 ============
    internal static class AudioConst
    {
        public const uint CLSCTX_ALL = 0x17;
        public const uint COINIT_MULTITHREADED = 0;
        public const int E_RENDER = 0;
        public const int E_CAPTURE = 1;
        public const int ROLE_CONSOLE = 0;
        public const int ROLE_MULTIMEDIA = 1;
        public const uint DEVICE_STATEMASK_ALL = 7;
        public const uint DEVICE_STATE_ACTIVE = 1;
        public const int SHARE_SHARED = 0;
        public const int STREAMFLAGS_EVENTCALLBACK = 0x40000;
        public const int BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
        public const int BUFFERFLAGS_SILENT = 0x2;
        public const int BUFFERFLAGS_TIMESTAMP_ERROR = 0x8;
        public const int STGM_READ = 0;
        public const int AUDCLNT_S_BUFFER_EMPTY = unchecked((int)0x08890001);
        public const ushort WAVE_FORMAT_PCM = 1;
        public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
        public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
        public static readonly Guid SUBTYPE_PCM        = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid SUBTYPE_IEEE_FLOAT = new Guid("00000003-0000-0010-8000-00AA00389B71");
        // 声道掩码（BS.1770 环绕声道按 1.41 加权）
        public const uint SPEAKER_FRONT_LEFT   = 0x1;
        public const uint SPEAKER_FRONT_RIGHT  = 0x2;
        public const uint SPEAKER_FRONT_CENTER = 0x4;
        public const uint SPEAKER_LOW_FREQ     = 0x8;
        public const uint SPEAKER_BACK_LEFT    = 0x10;
        public const uint SPEAKER_BACK_RIGHT   = 0x20;
        public const uint SPEAKER_BACK_CENTER  = 0x100;
        public const uint SPEAKER_SIDE_LEFT    = 0x200;
        public const uint SPEAKER_SIDE_RIGHT   = 0x400;
        public const uint SPEAKER_MASK_SURROUND = SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT | SPEAKER_SIDE_LEFT | SPEAKER_SIDE_RIGHT | SPEAKER_BACK_CENTER;
    }

    // ============ 结构体 ============
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormatBase   // WAVEFORMATEX 前 18 字节
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    // 兼容 WAVEFORMATEX 与 WAVEFORMATEXTENSIBLE（40 字节，仅按需读取）
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr ptr;    // VT_LPWSTR(31) 时指向字符串
    }

    internal struct DeviceInfo { public string Id; public string Name; }

    // ============ COM 接口（vtable 顺序必须与原生一致）============
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IntPtr ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId(out IntPtr ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WaveFormatEx pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long hnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormatEx pFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr ppData);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
    }

    // 端点通知回调实现（在 UI 线程注册 → 回调被 COM 调度回 UI 线程）
    internal sealed class NotificationClient : IMMNotificationClient
    {
        public Action<int, int, string> DefaultDeviceChanged;   // flow, role, deviceId
        public Action DeviceListChanged;

        public int OnDeviceStateChanged(string id, uint newState) { if (DeviceListChanged != null) DeviceListChanged(); return 0; }
        public int OnDeviceAdded(string id) { if (DeviceListChanged != null) DeviceListChanged(); return 0; }
        public int OnDeviceRemoved(string id) { if (DeviceListChanged != null) DeviceListChanged(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string id) { if (DefaultDeviceChanged != null) DefaultDeviceChanged(flow, role, id); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) { return 0; }
    }

    // IPolicyConfig（未公开接口；vtable 顺序来自公开逆向资料）
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out long defaultPeriod, out long minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out DeviceShareMode mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref DeviceShareMode mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isVisible);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceShareMode { public int shareMode; public int role; }

    // ============ 原生函数 ============
    internal static class NativeMethods
    {
        [DllImport("ole32.dll")]
        internal static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsCtx, ref Guid riid, out IntPtr ppv);
        [DllImport("ole32.dll")]
        internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
        [DllImport("ole32.dll")]
        internal static extern void CoUninitialize();
        [DllImport("ole32.dll")]
        internal static extern void CoTaskMemFree(IntPtr pv);
        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref PropVariant pvar);
    }

    // ============ 设备辅助 ============
    internal static class CoreAudio
    {
        public static IntPtr CreateEnumerator()
        {
            Guid clsid = AudioGuids.CLSID_MMDeviceEnumerator, iid = AudioGuids.IID_IMMDeviceEnumerator;
            IntPtr p;
            int hr = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, AudioConst.CLSCTX_ALL, ref iid, out p);
            if (hr < 0) throw new COMException("创建 MMDeviceEnumerator 失败", hr);
            return p;
        }

        public static List<DeviceInfo> EnumerateDevices(int flow, bool onlyActive)
        {
            var list = new List<DeviceInfo>();
            IntPtr pE = CreateEnumerator();
            var e = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pE);
            IntPtr pColl;
            if (e.EnumAudioEndpoints(flow, AudioConst.DEVICE_STATEMASK_ALL, out pColl) >= 0)
            {
                var coll = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(pColl);
                uint n;
                coll.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    IntPtr pDev;
                    if (coll.Item(i, out pDev) < 0 || pDev == IntPtr.Zero) continue;
                    var dev = (IMMDevice)Marshal.GetObjectForIUnknown(pDev);
                    uint state = 0;
                    dev.GetState(out state);
                    if (!onlyActive || state == AudioConst.DEVICE_STATE_ACTIVE)
                    {
                        IntPtr pid;
                        string id = null;
                        if (dev.GetId(out pid) >= 0 && pid != IntPtr.Zero)
                        {
                            id = Marshal.PtrToStringUni(pid);
                            NativeMethods.CoTaskMemFree(pid);
                        }
                        list.Add(new DeviceInfo { Id = id, Name = GetFriendlyName(dev) });
                    }
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(coll);
            }
            Marshal.ReleaseComObject(e);
            return list;
        }

        public static string GetDefaultDeviceId(int flow, int role)
        {
            try
            {
                IntPtr pE = CreateEnumerator();
                var e = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pE);
                IntPtr pDev;
                string id = null;
                if (e.GetDefaultAudioEndpoint(flow, role, out pDev) >= 0 && pDev != IntPtr.Zero)
                {
                    var dev = (IMMDevice)Marshal.GetObjectForIUnknown(pDev);
                    IntPtr pid;
                    if (dev.GetId(out pid) >= 0 && pid != IntPtr.Zero)
                    {
                        id = Marshal.PtrToStringUni(pid);
                        NativeMethods.CoTaskMemFree(pid);
                    }
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(e);
                return id;
            }
            catch { return null; }
        }

        public static IMMDevice GetDeviceById(IMMDeviceEnumerator enu, string id)
        {
            IntPtr pDev;
            if (enu.GetDevice(id, out pDev) < 0 || pDev == IntPtr.Zero) return null;
            return (IMMDevice)Marshal.GetObjectForIUnknown(pDev);
        }

        public static string GetFriendlyName(IMMDevice dev)
        {
            try
            {
                IntPtr pStore;
                if (dev.OpenPropertyStore(AudioConst.STGM_READ, out pStore) < 0) return "(未知设备)";
                var store = (IPropertyStore)Marshal.GetObjectForIUnknown(pStore);
                var key = new PropertyKey { fmtid = AudioGuids.PKEY_DeviceFriendlyName, pid = 14 };
                PropVariant pv;
                string name = null;
                if (store.GetValue(ref key, out pv) >= 0 && pv.vt == 31 /*VT_LPWSTR*/ && pv.ptr != IntPtr.Zero)
                {
                    name = Marshal.PtrToStringUni(pv.ptr);
                    NativeMethods.PropVariantClear(ref pv);
                }
                Marshal.ReleaseComObject(store);
                return name ?? "(未知设备)";
            }
            catch { return "(未知设备)"; }
        }

        // 切换系统默认播放设备（console + multimedia 两个角色）
        public static bool SetDefaultDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            try
            {
                var cfg = (IPolicyConfig)new PolicyConfigClient();
                int hr = cfg.SetDefaultEndpoint(deviceId, AudioConst.ROLE_CONSOLE);
                if (hr >= 0) cfg.SetDefaultEndpoint(deviceId, AudioConst.ROLE_MULTIMEDIA);
                return hr >= 0;
            }
            catch { return false; }
        }
    }
}
