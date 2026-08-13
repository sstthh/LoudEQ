using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LoudEQ
{
    // 设备失效（拔出/禁用等），由泵循环捕获后重建流
    internal sealed class AudioDeviceLostException : Exception { }

    // ============ 音频引擎 ============
    // 数据通路：CABLE Output（虚拟声卡录音端，WASAPI 捕获）
    //        → K 加权响度测量 → 前馈增益 + 限幅 + 重采样
    //        → 真实声卡（WASAPI 渲染）
    // 单音频线程 + 事件驱动（捕获事件/渲染事件/停止/重启）
    internal sealed class AudioEngine : IDisposable
    {
        // ---- 配置（UI 线程写，音频线程读）----
        volatile float targetLufs = -16f;
        volatile bool enabled = true;
        volatile float masterGain = 1f;          // 输出音量（线性）

        // ---- 状态（音频线程写，UI 读）----
        volatile float currentLufs = -100f;
        volatile float gainDb = 0f;
        volatile string status = "未启动";
        volatile string inputName = "";
        volatile string outputName = "";
        volatile int inputRate = 0;
        volatile int outputRate = 0;
        volatile string cableRenderId = "";      // CABLE Input 端点 ID（切默认设备用）

        public float TargetLufs { get { return targetLufs; } set { targetLufs = value; } }
        public bool Enabled { get { return enabled; } set { enabled = value; } }
        public float MasterGain { get { return masterGain; } set { masterGain = value; } }
        public float CurrentLufs { get { return currentLufs; } }
        public float GainDb { get { return gainDb; } }
        public string Status { get { return status; } }
        public string InputName { get { return inputName; } }
        public string OutputName { get { return outputName; } }
        public int InputRate { get { return inputRate; } }
        public int OutputRate { get { return outputRate; } }
        public string CableRenderId { get { return cableRenderId; } }

        Thread thread;
        readonly ManualResetEvent stopEvt = new ManualResetEvent(false);
        readonly ManualResetEvent restartEvt = new ManualResetEvent(false);
        StreamWriter logWriter;
        readonly object logLock = new object();

        // 流对象（仅音频线程访问）
        IMMDeviceEnumerator enu;
        IMMDevice capDevice, renDevice;
        IAudioClient capClient, renClient;
        IAudioCaptureClient cap;
        IAudioRenderClient ren;
        AutoResetEvent capEvt, renEvt;
        int capCh, renCh, capRate, renRate;
        uint renBufferFrames;
        bool capIsFloat, renIsFloat;
        ushort capBits, renBits;
        uint capMask;

        // DSP（随流格式重建）
        FloatFifo fifo;
        KWeighting kw;
        LoudnessMeter meter;
        GainController gc;
        LookaheadLimiter limiter;
        TransientSuppressor transient;
        Resampler resampler;
        float[] capBuf, kwBuf, renBuf, stBuf, outBuf, preBuf;

        // 缓冲管理状态：
        // 不做一次性时钟校准（虚拟声卡空闲期的数据包节拍比 48k 慢约 5%，
        // 有内容流入后才锁回真实速率——空闲期测量的是节拍假象，不是时钟偏差）。
        // 比率恒为 1.0（音调恒准）；真实硬件 <0.1% 的时钟偏差由每 5 秒一次、
        // ≤0.05% 的微步修正（每步 ≤0.9 音分，不可闻）；内容开始时若缓冲过浅，
        // 注入 100ms 静音恢复深度（发生在静音期，听感无损）。
        double ratioOffset;
        bool contentActive;
        long lastAdaptTick, lastLogTick;

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "LoudEQ-Audio";
            thread.Start();
        }

        public void Stop()
        {
            stopEvt.Set();
            if (thread != null) { thread.Join(3000); thread = null; }
        }

        public void RequestRestart() { restartEvt.Set(); }

        public void SetLogFile(string path)
        {
            try { logWriter = new StreamWriter(path, true, Encoding.UTF8) { AutoFlush = true }; }
            catch { logWriter = null; }
        }

        void Log(string msg)
        {
            if (logWriter == null) return;
            lock (logLock)
            {
                try { logWriter.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg); }
                catch { }
            }
        }

        void Run()
        {
            NativeMethods.CoInitializeEx(IntPtr.Zero, AudioConst.COINIT_MULTITHREADED);
            try
            {
                while (!stopEvt.WaitOne(0))
                {
                    restartEvt.Reset();
                    if (TryInitStreams())
                    {
                        PumpLoop();
                    }
                    else
                    {
                        Log("初始化失败，3 秒后重试: " + status);
                        WaitHandle.WaitAny(new WaitHandle[] { stopEvt, restartEvt }, 3000);
                    }
                    CleanupStreams();
                }
            }
            finally { NativeMethods.CoUninitialize(); }
        }

        bool TryInitStreams()
        {
            try
            {
                enu = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(CoreAudio.CreateEnumerator());

                // ---- 输入设备：CABLE Output（虚拟声卡录音端，优先精确匹配）----
                DeviceInfo? capInfo = null;
                foreach (var d in CoreAudio.EnumerateDevices(AudioConst.E_CAPTURE, true))
                    if (d.Name.ToUpperInvariant() == "CABLE OUTPUT") { capInfo = d; break; }
                if (capInfo == null)
                    foreach (var d in CoreAudio.EnumerateDevices(AudioConst.E_CAPTURE, true))
                        if (d.Name.ToUpperInvariant().Contains("CABLE")) { capInfo = d; break; }
                if (capInfo == null || capInfo.Value.Id == null)
                {
                    status = "未检测到虚拟声卡（CABLE Output）。请先安装 VB-CABLE 驱动（见安装包/README）。";
                    return false;
                }

                // 记录 CABLE Input（渲染端，用于切换系统默认设备；优先精确匹配，避免选到 16ch 端点）
                cableRenderId = "";
                foreach (var d in CoreAudio.EnumerateDevices(AudioConst.E_RENDER, true))
                    if (d.Name.ToUpperInvariant() == "CABLE INPUT") { cableRenderId = d.Id ?? ""; break; }
                if (cableRenderId == "")
                    foreach (var d in CoreAudio.EnumerateDevices(AudioConst.E_RENDER, true))
                        if (d.Name.ToUpperInvariant().Contains("CABLE")) { cableRenderId = d.Id ?? ""; break; }

                // ---- 输出设备：默认渲染设备；若默认就是 CABLE 则换下一个 ----
                DeviceInfo? renInfo = null;
                string defId = CoreAudio.GetDefaultDeviceId(AudioConst.E_RENDER, AudioConst.ROLE_CONSOLE);
                foreach (var d in CoreAudio.EnumerateDevices(AudioConst.E_RENDER, true))
                {
                    if (d.Id == null || d.Name.ToUpperInvariant().Contains("CABLE")) continue;
                    renInfo = d;
                    if (d.Id == defId) break;
                }
                if (renInfo == null || renInfo.Value.Id == null)
                {
                    status = "未找到可用的输出设备（扬声器/耳机）。";
                    return false;
                }

                capDevice = CoreAudio.GetDeviceById(enu, capInfo.Value.Id);
                renDevice = CoreAudio.GetDeviceById(enu, renInfo.Value.Id);
                if (capDevice == null || renDevice == null) { status = "无法打开音频端点。"; return false; }

                // ---- 捕获端 ----
                Guid iidClient = AudioGuids.IID_IAudioClient;
                Guid iidCap = AudioGuids.IID_IAudioCaptureClient;
                Guid iidRen = AudioGuids.IID_IAudioRenderClient;
                IntPtr pClient;
                if (capDevice.Activate(ref iidClient, AudioConst.CLSCTX_ALL, IntPtr.Zero, out pClient) < 0)
                { status = "激活捕获设备失败。"; return false; }
                capClient = (IAudioClient)Marshal.GetObjectForIUnknown(pClient);
                IntPtr pFmt;
                if (capClient.GetMixFormat(out pFmt) < 0) { status = "读取虚拟声卡格式失败。"; return false; }
                WaveFormatEx cf = ParseFormat(pFmt);
                if (!IsSupportedFormat(cf)) { status = "虚拟声卡格式不支持（" + cf.wBitsPerSample + "bit）。"; return false; }
                capRate = (int)cf.nSamplesPerSec; capCh = cf.nChannels; capMask = cf.dwChannelMask;
                capIsFloat = IsFloat(cf); capBits = cf.wBitsPerSample;
                long defPer, minPer;
                capClient.GetDevicePeriod(out defPer, out minPer);
                capEvt = new AutoResetEvent(false);
                // 用最小设备周期降低延迟；失败则退回默认周期
                long capPer = minPer;
                int hr = capClient.Initialize(AudioConst.SHARE_SHARED, AudioConst.STREAMFLAGS_EVENTCALLBACK, minPer, 0, ref cf, IntPtr.Zero);
                if (hr < 0)
                {
                    capPer = defPer;
                    hr = capClient.Initialize(AudioConst.SHARE_SHARED, AudioConst.STREAMFLAGS_EVENTCALLBACK, defPer, 0, ref cf, IntPtr.Zero);
                }
                if (hr < 0)
                {
                    // 0x80070005 = 访问被拒绝：Windows 麦克风隐私设置拦截了所有录音端点
                    // （含虚拟声卡 CABLE Output）。程序每 3 秒自动重试，权限打开后即恢复
                    if (hr == unchecked((int)0x80070005))
                        status = "捕获被拒绝：请到 Windows 设置→隐私→麦克风 打开\"允许桌面应用访问麦克风\"（每 3 秒自动重试）";
                    else
                        status = "捕获流初始化失败 (0x" + hr.ToString("X8") + ")。";
                    return false;
                }
                IntPtr pCap;
                if (capClient.GetService(ref iidCap, out pCap) < 0)
                { status = "获取捕获服务失败。"; return false; }
                cap = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(pCap);
                capClient.SetEventHandle(capEvt.SafeWaitHandle.DangerousGetHandle());
                capClient.Start();

                // ---- 渲染端 ----
                if (renDevice.Activate(ref iidClient, AudioConst.CLSCTX_ALL, IntPtr.Zero, out pClient) < 0)
                { status = "激活输出设备失败。"; return false; }
                renClient = (IAudioClient)Marshal.GetObjectForIUnknown(pClient);
                if (renClient.GetMixFormat(out pFmt) < 0) { status = "读取输出设备格式失败。"; return false; }
                WaveFormatEx rf = ParseFormat(pFmt);
                if (!IsSupportedFormat(rf)) { status = "输出设备格式不支持（" + rf.wBitsPerSample + "bit）。"; return false; }
                renRate = (int)rf.nSamplesPerSec; renCh = rf.nChannels;
                renIsFloat = IsFloat(rf); renBits = rf.wBitsPerSample;
                renClient.GetDevicePeriod(out defPer, out minPer);
                renEvt = new AutoResetEvent(false);
                long renPer = minPer;
                hr = renClient.Initialize(AudioConst.SHARE_SHARED, AudioConst.STREAMFLAGS_EVENTCALLBACK, minPer, 0, ref rf, IntPtr.Zero);
                if (hr < 0)
                {
                    renPer = defPer;
                    hr = renClient.Initialize(AudioConst.SHARE_SHARED, AudioConst.STREAMFLAGS_EVENTCALLBACK, defPer, 0, ref rf, IntPtr.Zero);
                }
                if (hr < 0) { status = "渲染流初始化失败 (0x" + hr.ToString("X8") + ")。"; return false; }
                renClient.GetBufferSize(out renBufferFrames);
                IntPtr pRen;
                if (renClient.GetService(ref iidRen, out pRen) < 0)
                { status = "获取渲染服务失败。"; return false; }
                ren = (IAudioRenderClient)Marshal.GetObjectForIUnknown(pRen);
                renClient.SetEventHandle(renEvt.SafeWaitHandle.DangerousGetHandle());
                renClient.Start();

                // ---- DSP ----
                int capChx = Math.Min(capCh, 8);
                fifo = new FloatFifo(2 * capRate * capCh);
                kw = new KWeighting(capRate, capChx);
                meter = new LoudnessMeter(capRate, capChx, capMask);
                gc = new GainController(renRate) { TargetLufs = targetLufs };
                limiter = new LookaheadLimiter(renRate, 3f, 200f);
                transient = new TransientSuppressor(renRate);
                resampler = new Resampler(capCh) { Ratio = capRate / (double)renRate };
                ratioOffset = 0; contentActive = false;
                lastAdaptTick = 0; lastLogTick = 0;
                // 预填充 30ms 静音，避免起步欠载（延迟与抗抖动平衡）
                preBuf = new float[(int)(capRate * 0.03) * capCh];
                fifo.Write(preBuf, 0, preBuf.Length);

                inputName = capInfo.Value.Name; outputName = renInfo.Value.Name;
                inputRate = capRate; outputRate = renRate;
                status = "运行中";
                Log("引擎启动: 输入 [" + inputName + "] " + capRate + "Hz " + capCh + "ch → 输出 [" + outputName + "] " + renRate + "Hz " + renCh + "ch, 流缓冲 " + (capPer / 10000) + "/" + (renPer / 10000) + "ms");
                return true;
            }
            catch (Exception ex)
            {
                status = "初始化异常: " + ex.Message;
                return false;
            }
        }

        void PumpLoop()
        {
            WaitHandle[] handles = new WaitHandle[] { capEvt, renEvt, stopEvt, restartEvt };
            while (true)
            {
                int r = WaitHandle.WaitAny(handles);
                if (r == 2 || r == 3 || r == WaitHandle.WaitTimeout) return;   // 停止 / 重启
                try
                {
                    if (r == 0) HandleCapture();
                    else HandleRender();
                }
                catch (AudioDeviceLostException) { return; }
                catch (COMException) { return; }
            }
        }

        void HandleCapture()
        {
            uint next;
            while (cap.GetNextPacketSize(out next) >= 0 && next > 0)
            {
                IntPtr data; uint frames, flags; ulong devPos, qpc;
                int hr = cap.GetBuffer(out data, out frames, out flags, out devPos, out qpc);
                if (hr < 0) throw new AudioDeviceLostException();
                if (hr == AudioConst.AUDCLNT_S_BUFFER_EMPTY) break;
                int n = (int)frames * capCh;
                if (capBuf == null || capBuf.Length < n) capBuf = new float[n];
                if ((flags & AudioConst.BUFFERFLAGS_DATA_DISCONTINUITY) != 0)
                {
                    // 流启动/设备故障后的断点：清空后重新预填充静音，比率复位
                    fifo.Clear();
                    if (preBuf != null) fifo.Write(preBuf, 0, preBuf.Length);
                    ratioOffset = 0;
                    resampler.Ratio = capRate / (double)renRate;
                    contentActive = false;
                }
                if ((flags & AudioConst.BUFFERFLAGS_SILENT) == 0)
                    ConvertToFloat(data, (int)frames, capCh, capIsFloat, capBits, capBuf);
                else
                    Array.Clear(capBuf, 0, n);
                // 响度测量：K 加权副本用于测量，原始数据进 FIFO（前馈：测的是输入）
                if (kwBuf == null || kwBuf.Length < n) kwBuf = new float[n];
                Array.Copy(capBuf, 0, kwBuf, 0, n);
                kw.Process(kwBuf, (int)frames, capCh);
                meter.Process(kwBuf, (int)frames, capCh);
                currentLufs = (float)meter.LastLufs;
                gc.TargetLufs = targetLufs;   // 每包同步 UI 目标响度（滑杆实时生效）
                gc.TargetDb = gc.ComputeTarget(meter.LastLufs, meter.LastBlockLufs);
                fifo.Write(capBuf, 0, n);
                // 内容开始且缓冲过浅（<25ms）：注入 30ms 静音恢复深度。
                // 虚拟声卡空闲期的数据包节拍偏慢会把缓冲耗干，注入发生在静音期，听感无损
                bool nowContent = meter.LastBlockLufs >= GainController.SILENCE_LUFS;
                if (nowContent && !contentActive)
                {
                    if (transient != null) transient.Reset();
                    if (fifo.Count < capRate * capCh / 40)
                    {
                        if (preBuf != null) fifo.Write(preBuf, 0, preBuf.Length);
                        Log("内容开始：注入 30ms 缓冲");
                    }
                }
                contentActive = nowContent;
                cap.ReleaseBuffer(frames);
            }
        }

        void HandleRender()
        {
            uint padding;
            if (renClient.GetCurrentPadding(out padding) < 0) throw new AudioDeviceLostException();
            int avail = (int)(renBufferFrames - padding);
            if (avail <= 0) return;

            int need = avail * capCh;
            if (renBuf == null || renBuf.Length < need) renBuf = new float[need];
            resampler.Pull(fifo, renBuf, avail);   // 输出 avail 帧（capCh 布局，renRate 采样率）

            // 缓冲微调：每 5 秒一次；仅在偏差较大时以 ≤0.05% 小步修正。
            // 持续改变重采样率会改变音调（人耳可感知），故不做一次性校准——
            // 真实硬件时钟偏差通常 <0.1%，几步即可收敛；每步 ≤0.9 音分，不可闻。
            long tick = Environment.TickCount;
            if (tick - lastAdaptTick > 5000)
            {
                lastAdaptTick = tick;
                double fillSec = fifo.Count / (double)(capCh * capRate);
                double e = fillSec - 0.03;
                if (Math.Abs(e) > 0.015)
                {
                    double step = Math.Abs(e) > 0.03 ? 0.0005 : 0.0001;
                    double u = ratioOffset + (e > 0 ? step : -step);
                    if (u > 0.05) u = 0.05; else if (u < -0.05) u = -0.05;
                    ratioOffset = u;
                    resampler.Ratio = (capRate / (double)renRate) * (1.0 + u);
                    Log(string.Format("缓冲微调: {0:+0.000;-0.000}%", u * 100));
                }
            }

            // 声道映射 → 立体声
            if (stBuf == null || stBuf.Length < avail * 2) stBuf = new float[avail * 2];
            MapToStereo(renBuf, stBuf, avail, capCh);
            // 增益（前馈） + 输出音量 + 限幅
            if (enabled) gc.ProcessBlock(stBuf, avail, 2);
            if (masterGain != 1f)
            {
                int n2 = avail * 2;
                for (int i = 0; i < n2; i++) stBuf[i] *= masterGain;
            }
            if (enabled) transient.ProcessBlock(stBuf, avail, 2);
            if (enabled) limiter.ProcessBlock(stBuf, avail, 2);
            gainDb = gc.GainDb;

            // 转输出格式并写入渲染缓冲
            WriteRender(stBuf, avail, renCh, renIsFloat, renBits);

            if (tick - lastLogTick > 2000)
            {
                lastLogTick = tick;
                Log(string.Format("lufs={0:0.0} 增益={1:+0.0;-0.0}dB 瞬态={2:-0.0}dB 缓冲={3:0.000}s",
                    currentLufs, gainDb, transient != null ? transient.LastGrDb : 0f,
                    fifo.Count / (double)(capCh * capRate)));
            }
        }

        void WriteRender(float[] stereo, int frames, int outCh, bool isFloat, ushort bits)
        {
            int n = frames * outCh;
            float[] outf;
            if (outCh == 2) outf = stereo;
            else
            {
                if (outBuf == null || outBuf.Length < n) outBuf = new float[n];
                outf = outBuf;
                if (outCh == 1)
                {
                    for (int i = 0; i < frames; i++) outf[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
                }
                else
                {
                    for (int i = 0; i < frames; i++) { outf[i * outCh] = stereo[i * 2]; outf[i * outCh + 1] = stereo[i * 2 + 1]; }
                    Array.Clear(outf, frames * 2, n - frames * 2);
                }
            }
            IntPtr p;
            if (ren.GetBuffer((uint)frames, out p) < 0) throw new AudioDeviceLostException();
            if (isFloat)
            {
                Marshal.Copy(outf, 0, p, n);
            }
            else if (bits == 16)
            {
                for (int i = 0; i < n; i++) Marshal.WriteInt16(p, i * 2, ClampToInt16(outf[i] * 32767f));
            }
            else if (bits == 24)
            {
                for (int i = 0; i < n; i++)
                {
                    int v = ClampToInt24(outf[i] * 8388607f);
                    Marshal.WriteByte(p, i * 3, (byte)(v & 0xFF));
                    Marshal.WriteByte(p, i * 3 + 1, (byte)((v >> 8) & 0xFF));
                    Marshal.WriteByte(p, i * 3 + 2, (byte)((v >> 16) & 0xFF));
                }
            }
            else if (bits == 32)
            {
                for (int i = 0; i < n; i++) Marshal.WriteInt32(p, i * 4, ClampToInt32(outf[i] * 2147483647f));
            }
            ren.ReleaseBuffer((uint)frames, 0);
        }

        void CleanupStreams()
        {
            try { if (capClient != null) capClient.Stop(); } catch { }
            try { if (renClient != null) renClient.Stop(); } catch { }
            if (capEvt != null) { capEvt.Dispose(); capEvt = null; }
            if (renEvt != null) { renEvt.Dispose(); renEvt = null; }
            if (cap != null) { Marshal.ReleaseComObject(cap); cap = null; }
            if (ren != null) { Marshal.ReleaseComObject(ren); ren = null; }
            if (capClient != null) { Marshal.ReleaseComObject(capClient); capClient = null; }
            if (renClient != null) { Marshal.ReleaseComObject(renClient); renClient = null; }
            if (capDevice != null) { Marshal.ReleaseComObject(capDevice); capDevice = null; }
            if (renDevice != null) { Marshal.ReleaseComObject(renDevice); renDevice = null; }
            if (enu != null) { Marshal.ReleaseComObject(enu); enu = null; }
            fifo = null; kw = null; meter = null; gc = null; limiter = null; transient = null; resampler = null;
            capBuf = kwBuf = renBuf = stBuf = outBuf = preBuf = null;
        }

        // ============ 静态工具 ============
        static WaveFormatEx ParseFormat(IntPtr pFmt)
        {
            try
            {
                WaveFormatBase b = (WaveFormatBase)Marshal.PtrToStructure(pFmt, typeof(WaveFormatBase));
                var f = new WaveFormatEx();
                f.wFormatTag = b.wFormatTag; f.nChannels = b.nChannels; f.nSamplesPerSec = b.nSamplesPerSec;
                f.nAvgBytesPerSec = b.nAvgBytesPerSec; f.nBlockAlign = b.nBlockAlign; f.wBitsPerSample = b.wBitsPerSample;
                f.cbSize = b.cbSize;
                if (b.wFormatTag == AudioConst.WAVE_FORMAT_EXTENSIBLE && b.cbSize >= 22)
                {
                    IntPtr p = new IntPtr(pFmt.ToInt64() + 18);
                    f.wValidBitsPerSample = (ushort)Marshal.ReadInt16(p);
                    f.dwChannelMask = (uint)Marshal.ReadInt32(p, 2);
                    byte[] g = new byte[16];
                    Marshal.Copy(new IntPtr(p.ToInt64() + 6), g, 0, 16);
                    f.SubFormat = new Guid(g);
                }
                return f;
            }
            finally { NativeMethods.CoTaskMemFree(pFmt); }
        }

        static bool IsSupportedFormat(WaveFormatEx f)
        {
            if (f.wFormatTag == AudioConst.WAVE_FORMAT_IEEE_FLOAT && f.wBitsPerSample == 32) return true;
            if (f.wFormatTag == AudioConst.WAVE_FORMAT_PCM && (f.wBitsPerSample == 16 || f.wBitsPerSample == 24 || f.wBitsPerSample == 32)) return true;
            if (f.wFormatTag == AudioConst.WAVE_FORMAT_EXTENSIBLE)
            {
                if (f.SubFormat == AudioConst.SUBTYPE_IEEE_FLOAT && f.wBitsPerSample == 32) return true;
                if (f.SubFormat == AudioConst.SUBTYPE_PCM && (f.wBitsPerSample == 16 || f.wBitsPerSample == 24 || f.wBitsPerSample == 32)) return true;
            }
            return false;
        }

        static bool IsFloat(WaveFormatEx f)
        {
            return f.wFormatTag == AudioConst.WAVE_FORMAT_IEEE_FLOAT ||
                   (f.wFormatTag == AudioConst.WAVE_FORMAT_EXTENSIBLE && f.SubFormat == AudioConst.SUBTYPE_IEEE_FLOAT);
        }

        static void ConvertToFloat(IntPtr data, int frames, int ch, bool isFloat, ushort bits, float[] dst)
        {
            int n = frames * ch;
            if (isFloat)
            {
                Marshal.Copy(data, dst, 0, n);
            }
            else if (bits == 16)
            {
                for (int i = 0; i < n; i++) dst[i] = Marshal.ReadInt16(data, i * 2) / 32768f;
            }
            else if (bits == 24)
            {
                for (int i = 0; i < n; i++)
                {
                    int p = i * 3;
                    int v = Marshal.ReadByte(data, p) | (Marshal.ReadByte(data, p + 1) << 8) | ((sbyte)Marshal.ReadByte(data, p + 2) << 16);
                    dst[i] = v / 8388608f;
                }
            }
            else if (bits == 32)
            {
                for (int i = 0; i < n; i++) dst[i] = Marshal.ReadInt32(data, i * 4) / 2147483648f;
            }
        }

        static void MapToStereo(float[] src, float[] dst, int frames, int ch)
        {
            if (ch == 2) { Array.Copy(src, dst, frames * 2); return; }
            if (ch == 1)
            {
                for (int i = 0; i < frames; i++) { dst[i * 2] = src[i]; dst[i * 2 + 1] = src[i]; }
                return;
            }
            for (int i = 0; i < frames; i++) { dst[i * 2] = src[i * ch]; dst[i * 2 + 1] = src[i * ch + 1]; }
        }

        static short ClampToInt16(float v) { return v > 32767f ? (short)32767 : (v < -32768f ? (short)-32768 : (short)v); }
        static int ClampToInt24(float v) { return v > 8388607f ? 8388607 : (v < -8388608f ? -8388608 : (int)v); }
        static int ClampToInt32(float v) { return v > 2147483647f ? 2147483647 : (v < -2147483648f ? -2147483648 : (int)v); }

        public void Dispose()
        {
            Stop();
            if (logWriter != null) { try { logWriter.Close(); } catch { } logWriter = null; }
        }
    }
}
