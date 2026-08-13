using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LoudEQ
{
    // 无音频设备的数值自检（LoudEQ.exe /selftest）：
    // 验证 K 加权校准、增益收敛、防振荡、死区、静音保持、限幅不削波
    internal static class SelfTest
    {
        static int passCount, failCount;
        static MultiWriter w;
        static StreamWriter resultFile;

        // 同时输出到控制台与 exe 旁的 selftest-result.txt
        // （GUI 子系统程序的控制台输出可能被宿主丢弃，文件保证结果可查）
        sealed class MultiWriter
        {
            readonly StreamWriter console, file;
            public MultiWriter(StreamWriter console, StreamWriter file) { this.console = console; this.file = file; }
            public void WriteLine(string s)
            {
                try { if (console != null) console.WriteLine(s); } catch { }
                try { if (file != null) file.WriteLine(s); } catch { }
            }
        }

        public static void Run()
        {
            StreamWriter console = null;
            try { console = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }; } catch { }
            try { resultFile = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-result.txt"), false, Encoding.UTF8); } catch { }
            w = new MultiWriter(console, resultFile);
            w.WriteLine("== LoudEQ DSP 自检 ==");
            RunTests();
            w.WriteLine("");
            w.WriteLine(passCount + " 项通过, " + failCount + " 项失败");
            try { if (resultFile != null) resultFile.Flush(); } catch { }
            Environment.Exit(failCount == 0 ? 0 : 1);
        }

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { passCount++; w.WriteLine("[通过] " + name + (detail != "" ? "  (" + detail + ")" : "")); }
            else { failCount++; w.WriteLine("[失败] " + name + (detail != "" ? "  (" + detail + ")" : "")); }
        }

        static double RmsOf(float[] buf, int frames, int stride)
        {
            double s = 0;
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < stride; c++)
                {
                    double v = buf[i * stride + c];
                    s += v * v;
                }
            return Math.Sqrt(s / (frames * stride));
        }

        // 诊断：滤波器系数 + 逐级 RMS，便于定位问题
        static void RunDiagnostics()
        {
            float[] s, h;
            KWeighting.ComputeCoefficients(48000f, out s, out h);
            w.WriteLine("[诊断] shelf: b0=" + s[0].ToString("0.000000") + " b1=" + s[1].ToString("0.000000") + " b2=" + s[2].ToString("0.000000") + " a1=" + s[3].ToString("0.000000") + " a2=" + s[4].ToString("0.000000"));
            w.WriteLine("[诊断] hp:    b0=" + h[0].ToString("0.000000") + " b1=" + h[1].ToString("0.000000") + " b2=" + h[2].ToString("0.000000") + " a1=" + h[3].ToString("0.000000") + " a2=" + h[4].ToString("0.000000"));
            var bq1 = new Biquad(s[0], s[1], s[2], s[3], s[4], 2);
            var bq2 = new Biquad(h[0], h[1], h[2], h[3], h[4], 2);
            float[] dbg = SineBuf(997, 0.5f, 1.0f);
            int fr = dbg.Length / 2;
            w.WriteLine("[诊断] 997Hz 输入 RMS = " + RmsOf(dbg, fr, 2).ToString("0.000000"));
            bq1.Process(dbg, fr, 2);
            w.WriteLine("[诊断] 997Hz shelf后 RMS = " + RmsOf(dbg, fr, 2).ToString("0.000000"));
            bq2.Process(dbg, fr, 2);
            w.WriteLine("[诊断] 997Hz hp后 RMS = " + RmsOf(dbg, fr, 2).ToString("0.000000"));
            var m = new LoudnessMeter(48000f, 2, 0x3);
            m.Process(dbg, fr, 2);
            w.WriteLine("[诊断] 同一缓冲经响度计 = " + m.LastLufs.ToString("0.000") + " LUFS");
        }

        // 模拟引擎管线：K加权 → 测量 → 更新目标 →（可选）施加增益 + 限幅
        sealed class Sim
        {
            public KWeighting kw;
            public LoudnessMeter meter;
            public GainController gc;
            public LookaheadLimiter lim;
            float[] tmp;
            const float FS = 48000f;

            public Sim(float target)
            {
                kw = new KWeighting(FS, 2);
                meter = new LoudnessMeter(FS, 2, AudioConst.SPEAKER_FRONT_LEFT | AudioConst.SPEAKER_FRONT_RIGHT);
                gc = new GainController(FS) { TargetLufs = target };
                lim = new LookaheadLimiter(FS, 5f, 200f);
            }

            public void Feed(float[] buf, int frames, bool apply)
            {
                if (tmp == null || tmp.Length < buf.Length) tmp = new float[buf.Length];
                Array.Copy(buf, tmp, buf.Length);
                kw.Process(tmp, frames, 2);
                meter.Process(tmp, frames, 2);
                gc.TargetDb = gc.ComputeTarget(meter.LastLufs, meter.LastBlockLufs);
                if (apply)
                {
                    gc.ProcessBlock(buf, frames, 2);
                    lim.ProcessBlock(buf, frames, 2);
                }
            }
        }

        const float FS = 48000f;
        const int STEP = 4800;   // 100ms

        static float[] SineBuf(double freq, float amp, float seconds)
        {
            int frames = (int)(FS * seconds);
            var buf = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                float x = (float)(amp * Math.Sin(2 * Math.PI * freq * i / FS));
                buf[i * 2] = x; buf[i * 2 + 1] = x;
            }
            return buf;
        }

        static float[] NoiseBuf(float amp, float seconds, int seed)
        {
            var rnd = new Random(seed);
            int frames = (int)(FS * seconds);
            var buf = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                float x = (float)((rnd.NextDouble() * 2 - 1) * amp);
                buf[i * 2] = x; buf[i * 2 + 1] = x;
            }
            return buf;
        }

        // 均匀白噪声：K 加权高频搁架使读数比 RMS 高约 4.1dB；立体声求和 +3.01dB
        static float NoiseAmpForLufs(float lufs) { return (float)(Math.Pow(10, (lufs - 6.4) / 20.0) * Math.Sqrt(3)); }

        static void RunTests()
        {
            RunDiagnostics();

            // ---- 1. K 加权校准（解析值：997Hz 处加权 +0.67dB；50Hz 处高通 -3.85dB；立体声求和 +3.01dB）----
            {
                var sim = new Sim(-16f);
                float[] buf = SineBuf(997, 0.5f, 2.0f);
                sim.Feed(buf, buf.Length / 2, false);
                double got = sim.meter.LastLufs;
                Check("K加权 997Hz 校准 (期望 -6.05 ±0.5)", Math.Abs(got - (-6.05)) < 0.5, "实测 " + got.ToString("0.00") + " LUFS");

                sim = new Sim(-16f);
                buf = SineBuf(50, 0.5f, 2.0f);
                sim.Feed(buf, buf.Length / 2, false);
                got = sim.meter.LastLufs;
                Check("K加权 50Hz 校准 (期望 -10.6 ±0.8)", Math.Abs(got - (-10.6)) < 0.8, "实测 " + got.ToString("0.00") + " LUFS");
            }

            // ---- 2. 收敛与无振荡：约 -23 LUFS 噪声输入，目标 -16 ----
            {
                var sim = new Sim(-16f);
                float[] buf = NoiseBuf(NoiseAmpForLufs(-23f), 8.0f, 12345);
                var gains = new List<float>();
                for (int off = 0; off + STEP <= buf.Length / 2; off += STEP)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(buf, off * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                    gains.Add(sim.gc.GainDb);
                }
                double measured = sim.meter.LastLufs;
                float expected = -16f - (float)measured;
                int n = gains.Count, half = n / 2;
                float mean = 0;
                for (int i = half; i < n; i++) mean += gains[i];
                mean /= (n - half);
                float var = 0;
                for (int i = half; i < n; i++) var += (gains[i] - mean) * (gains[i] - mean);
                var /= (n - half);
                float std = (float)Math.Sqrt(var);
                Check("增益收敛 (实测 " + mean.ToString("0.00") + "dB, 期望 " + expected.ToString("0.00") + " ±0.8)", Math.Abs(mean - expected) < 0.8, "");
                Check("无振荡 (后半段 std " + std.ToString("0.000") + "dB < 0.25)", std < 0.25, "");
            }

            // ---- 3. 安静内容上限：约 -40 LUFS → 增益钳位 +12dB ----
            {
                var sim = new Sim(-16f);
                float[] buf = NoiseBuf(NoiseAmpForLufs(-40f), 5.0f, 7);
                for (int off = 0; off + STEP <= buf.Length / 2; off += STEP)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(buf, off * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                }
                Check("安静内容增益钳位 +12dB (实测 " + sim.gc.GainDb.ToString("0.00") + ")", sim.gc.GainDb >= 11.5f, "");
            }

            // ---- 4. 响亮内容：约 -6 LUFS → 增益约 -10 ----
            {
                var sim = new Sim(-16f);
                float[] buf = NoiseBuf(NoiseAmpForLufs(-6f), 5.0f, 9);
                for (int off = 0; off + STEP <= buf.Length / 2; off += STEP)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(buf, off * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                }
                double measured = sim.meter.LastLufs;
                float expected = -16f - (float)measured;
                Check("响亮内容增益 (实测 " + sim.gc.GainDb.ToString("0.00") + "dB, 期望 " + expected.ToString("0.00") + " ±0.8)", Math.Abs(sim.gc.GainDb - expected) < 0.8, "");
            }

            // ---- 5. 死区：先抬到 +12dB，再喂 -15.5 LUFS 正弦（误差 -0.5dB 在死区内）→ 增益必须回零 ----
            {
                var sim = new Sim(-16f);
                float[] loud = NoiseBuf(NoiseAmpForLufs(-40f), 3.0f, 21);
                for (int off = 0; off + STEP <= loud.Length / 2; off += STEP)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(loud, off * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                }
                float gBefore = sim.gc.GainDb;
                // 997Hz 正弦，测量值 ≈ -15.5 LUFS（误差 -0.5dB 落在死区内）
                // 立体声 z = amp²：-0.691 + 20log10(amp) + 0.67 = -15.5
                float amp = (float)Math.Pow(10, (-15.5 + 0.691 - 0.67) / 20.0);
                float[] near = SineBuf(997, amp, 5.0f);
                for (int off = 0; off + STEP <= near.Length / 2; off += STEP)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(near, off * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                }
                double measured = sim.meter.LastLufs;
                Check("死区回零 (起始 " + gBefore.ToString("0.00") + "dB → 输入 " + measured.ToString("0.00") + " LUFS, 最终增益 " + sim.gc.GainDb.ToString("0.00") + "dB, |g|≤0.15)", Math.Abs(sim.gc.GainDb) <= 0.15f, "");
            }

            // ---- 6. 静音保持：收敛到 g0 后静音 3s，增益不变 ----
            {
                var sim = new Sim(-16f);
                float[] buf = NoiseBuf(NoiseAmpForLufs(-23f), 4.0f, 13);
                for (int off = 0; off + STEP <= buf.Length / 2; off += STEP)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(buf, off * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                }
                float g0 = sim.gc.GainDb;
                w.WriteLine("[诊断] 静音前: 增益=" + g0.ToString("0.00") + "dB 测量=" + sim.meter.LastLufs.ToString("0.00") + " LUFS");
                for (int k = 0; k < 30; k++)
                {
                    float[] seg = new float[STEP * 2];
                    sim.Feed(seg, STEP, true);
                    if (k <= 3 || k == 9 || k == 29)
                        w.WriteLine("[诊断] 静音块" + k + ": 增益=" + sim.gc.GainDb.ToString("0.00") + "dB 测量=" + sim.meter.LastLufs.ToString("0.00") + " LUFS");
                }
                Check("静音保持 (g0=" + g0.ToString("0.00") + ", 静音后 " + sim.gc.GainDb.ToString("0.00") + ", 差 <0.1)", Math.Abs(sim.gc.GainDb - g0) < 0.1f, "");
            }

            // ---- 7. 阶跃响应：约 -30 → -14 LUFS，无深过冲、2.5s 内收敛到 -2dB ----
            {
                var sim = new Sim(-16f);
                float[] a = NoiseBuf(NoiseAmpForLufs(-30f), 4.0f, 17);
                float[] b = NoiseBuf(NoiseAmpForLufs(-14f), 4.0f, 19);
                var after = new List<float>();
                int stepIdx = (a.Length / 2) / STEP;
                for (int i = 0; i < stepIdx; i++)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(a, i * STEP * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                }
                int framesB = b.Length / 2;
                for (int i = 0; i + STEP <= framesB; i += STEP)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(b, i * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                    after.Add(sim.gc.GainDb);
                }
                float expected = -16f - (float)sim.meter.LastLufs;
                float minAfter = float.MaxValue;
                foreach (var gd in after) if (gd < minAfter) minAfter = gd;
                float final = sim.gc.GainDb;
                Check("阶跃无深过冲 (阶跃后最低 " + minAfter.ToString("0.00") + "dB ≥ " + (expected - 2.5).ToString("0.00") + ")", minAfter >= expected - 2.5f, "");
                Check("阶跃收敛 (终值 " + final.ToString("0.00") + "dB, 期望 " + expected.ToString("0.00") + " ±0.8)", Math.Abs(final - expected) < 0.8f, "");
            }

            // ---- 8. 限幅器：满幅正弦 + 强制 +12dB → 输出峰值 ≤ -1.5dBFS ----
            {
                var sim = new Sim(-16f);
                float[] buf = SineBuf(997, 0.99f, 2.0f);
                sim.Feed(buf, buf.Length / 2, false);
                sim.gc.TargetDb = 12f;   // 强制 +12dB 增益
                float[] proc = SineBuf(997, 0.99f, 1.0f);
                sim.gc.ProcessBlock(proc, proc.Length / 2, 2);
                sim.lim.ProcessBlock(proc, proc.Length / 2, 2);
                float peak = 0f;
                for (int i = 0; i < proc.Length; i++)
                {
                    float ax = Math.Abs(proc[i]);
                    if (ax > peak) peak = ax;
                }
                Check("限幅不削波 (峰值 " + peak.ToString("0.000") + " ≤ 0.842)", peak <= 0.842f, "");
            }

            // ---- 9. 响亮内容停止后静音：增益保持深度（防下一段内容冲出）----
            {
                var sim = new Sim(-16f);
                float[] buf = NoiseBuf(NoiseAmpForLufs(-6f), 4.0f, 31);
                for (int off = 0; off + STEP <= buf.Length / 2; off += STEP)
                {
                    float[] seg = new float[STEP * 2];
                    Array.Copy(buf, off * 2, seg, 0, STEP * 2);
                    sim.Feed(seg, STEP, true);
                }
                float g0 = sim.gc.GainDb;
                for (int k = 0; k < 20; k++)
                {
                    float[] seg = new float[STEP * 2];
                    sim.Feed(seg, STEP, true);
                }
                Check("响亮停止后保持 (g0=" + g0.ToString("0.00") + ", 静音后 " + sim.gc.GainDb.ToString("0.00") + ", 差 <0.5)", Math.Abs(sim.gc.GainDb - g0) < 0.5f, "");
            }

            // ---- 10. 重采样器保真度：ratio 0.98 下 10kHz 正弦，与解析线性插值对照 ----
            // 高频成分对插值错误最敏感；曾因窗口差一帧导致插值退化为保持采样（失真约 -1dB）
            {
                float[] src = SineBuf(10000, 0.9f, 2.0f);
                var fifo = new FloatFifo(src.Length);
                fifo.Write(src, 0, src.Length);
                var res = new Resampler(2) { Ratio = 0.98 };
                int outFrames = 48000;
                float[] outp = new float[outFrames * 2];
                res.Pull(fifo, outp, outFrames);
                double err = 0, sig = 0;
                int srcFrames = src.Length / 2;
                for (int j = 0; j < outFrames; j++)
                {
                    double p = j * 0.98;
                    int i0 = (int)p;
                    double frac = p - i0;
                    int i1 = i0 + 1;
                    if (i1 >= srcFrames) i1 = i0;
                    double refL = src[i0 * 2] + (src[i1 * 2] - src[i0 * 2]) * frac;
                    double d = outp[j * 2] - refL;
                    err += d * d;
                    sig += refL * refL;
                }
                double thd = 10 * Math.Log10(err / sig);
                Check("重采样保真度 (ratio 0.98, 10kHz, 失真 " + thd.ToString("0.0") + "dB < -40)", thd < -40, "");
            }

            // ---- 11. 瞬态抑制：环境噪声后突然 +30dB 爆发，爆发主体被快速削减 ----
            {
                var sup = new TransientSuppressor(48000f);
                // 环境：低电平噪声 4 秒（收敛环境参考），随后模拟内容开始
                float[] amb = NoiseBuf(0.01f, 4.0f, 41);
                sup.ProcessBlock(amb, amb.Length / 2, 2);
                sup.Reset();
                float ambPeak = 0;
                for (int i = 0; i < amb.Length; i++) { float a = Math.Abs(amb[i]); if (a > ambPeak) ambPeak = a; }
                // 爆发：满幅正弦 300ms。起始 10~25ms 被强抑制（环境参考尚未追平）；
                // 之后随环境参考适应逐渐放松（交给 LUFS 主路径接手），但全程仍有界
                float[] burst = SineBuf(997, 0.9f, 0.3f);
                sup.ProcessBlock(burst, burst.Length / 2, 2);
                float onsetPeak = 0, wholePeak = 0;
                for (int i = 480 * 2; i < burst.Length; i++)
                {
                    float a = Math.Abs(burst[i]);
                    if (a > wholePeak) wholePeak = a;
                    if (i < 1200 * 2 && a > onsetPeak) onsetPeak = a;
                }
                Check("瞬态抑制-起始强削减 (10~25ms 峰 " + onsetPeak.ToString("0.000") + " < 0.12, 环境峰 " + ambPeak.ToString("0.000") + ")", onsetPeak < 0.12f, "");
                Check("瞬态抑制-全程有界 (主体峰 " + wholePeak.ToString("0.000") + " < 0.35)", wholePeak < 0.35f, "");
            }
        }
    }
}
