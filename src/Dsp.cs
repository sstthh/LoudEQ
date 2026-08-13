using System;

namespace LoudEQ
{
    // ============ 双二阶滤波器（转置直接 II 型）============
    internal sealed class Biquad
    {
        readonly float b0, b1, b2, a1, a2;
        readonly float[] z1, z2;
        readonly int maxCh;

        public Biquad(float b0, float b1, float b2, float a1, float a2, int maxChannels)
        {
            this.b0 = b0; this.b1 = b1; this.b2 = b2; this.a1 = a1; this.a2 = a2;
            maxCh = maxChannels;
            z1 = new float[maxCh];
            z2 = new float[maxCh];
        }

        public void Process(float[] buf, int frames, int ch)
        {
            int n = ch < maxCh ? ch : maxCh;
            for (int i = 0; i < frames; i++)
            {
                int o = i * ch;
                for (int c = 0; c < n; c++)
                {
                    float x = buf[o + c];
                    float y = b0 * x + z1[c];
                    z1[c] = b1 * x - a1 * y + z2[c];
                    z2[c] = b2 * x - a2 * y;
                    buf[o + c] = y;
                }
            }
        }
    }

    // ============ ITU-R BS.1770 K 加权 ============
    // 两级：+3.999843dB 高频搁架 @1681.974Hz → 高通 @38.13547Hz (Q=0.500327)
    internal sealed class KWeighting
    {
        readonly Biquad stage1, stage2;

        public KWeighting(float fs, int maxChannels)
        {
            float[] shelf, hp;
            ComputeCoefficients(fs, out shelf, out hp);
            stage1 = new Biquad(shelf[0], shelf[1], shelf[2], shelf[3], shelf[4], maxChannels);
            stage2 = new Biquad(hp[0], hp[1], hp[2], hp[3], hp[4], maxChannels);
        }

        public void Process(float[] buf, int frames, int ch)
        {
            stage1.Process(buf, frames, ch);
            stage2.Process(buf, frames, ch);
        }

        // 与 ITU-R BS.1770-4 附录（libebur128 实现）完全一致的滤波器系数
        public static void ComputeCoefficients(float fs, out float[] shelf, out float[] hp)
        {
            float b0, b1, b2, a1, a2;
            ComputeShelf(fs, out b0, out b1, out b2, out a1, out a2);
            shelf = new float[] { b0, b1, b2, a1, a2 };
            ComputeHighPass(fs, out b0, out b1, out b2, out a1, out a2);
            hp = new float[] { b0, b1, b2, a1, a2 };
        }

        static void ComputeShelf(float fs, out float b0, out float b1, out float b2, out float a1, out float a2)
        {
            // 高频搁架：DC 0dB，高频 +4dB，转折 1681.974Hz
            const double G = 3.999843853973347;
            const double f0 = 1681.974450955533;
            double K = Math.Tan(Math.PI * f0 / fs);
            double Vh = Math.Pow(10.0, G / 20.0);
            double Vb = Math.Pow(Vh, 0.4996667741545416);
            double a0 = 1.0 + K / Vb + K * K;
            b0 = (float)((Vh + Vb * K + K * K) / a0);
            b1 = (float)(2.0 * (K * K - Vh) / a0);
            b2 = (float)((Vh - Vb * K + K * K) / a0);
            a1 = (float)(2.0 * (K * K - 1.0) / a0);
            a2 = (float)((1.0 - K / Vb + K * K) / a0);
        }

        static void ComputeHighPass(float fs, out float b0, out float b1, out float b2, out float a1, out float a2)
        {
            const double f0 = 38.13547087602444;
            const double Q = 0.5003270373238773;
            double K = Math.Tan(Math.PI * f0 / fs);
            double a0 = 1.0 + K / Q + K * K;
            b0 = (float)(1.0 / a0);
            b1 = (float)(-2.0 / a0);
            b2 = (float)(1.0 / a0);
            a1 = (float)(2.0 * (K * K - 1.0) / a0);
            a2 = (float)((1.0 - K / Q + K * K) / a0);
        }
    }

    // ============ 响度计：M 瞬时响度（400ms 窗、100ms 步进，LUFS）============
    internal sealed class LoudnessMeter
    {
        const int WINDOW_BLOCKS = 4;      // 4 × 100ms = 400ms
        readonly int ch, blockFrames;
        readonly double[] blockSum;       // 当前 100ms 块各声道平方和
        readonly double[] ring;           // WINDOW_BLOCKS × ch 均方历史
        readonly float[] weights;         // 声道加权（环绕 1.41，其余 1.0）
        int ringPos, blockPos;
        public double LastLufs = -100.0;
        public double LastBlockLufs = -100.0;   // 最近一个 100ms 块的瞬时响度（无窗）

        public LoudnessMeter(float fs, int channels, uint channelMask)
        {
            ch = Math.Min(channels, 8);
            blockFrames = Math.Max(1, (int)(fs * 0.1));
            blockSum = new double[ch];
            ring = new double[WINDOW_BLOCKS * ch];
            weights = new float[ch];
            for (int c = 0; c < ch; c++)
            {
                uint bit = 1u << c;
                bool surround = channelMask != 0 && (channelMask & bit & AudioConst.SPEAKER_MASK_SURROUND) != 0;
                weights[c] = surround ? 1.41f : 1.0f;
            }
        }

        // 输入必须是已经过 K 加权的信号；stride = 缓冲实际声道数
        // 逐帧推进块计数：任意大小的调用都能正确切分 100ms 块
        public void Process(float[] kWeighted, int frames, int stride)
        {
            for (int i = 0; i < frames; i++)
            {
                int o = i * stride;
                for (int c = 0; c < ch; c++)
                {
                    float x = kWeighted[o + c];
                    blockSum[c] += x * x;
                }
                blockPos++;
                if (blockPos >= blockFrames) ComputeBlock();
            }
        }

        void ComputeBlock()
        {
            blockPos -= blockFrames;
            double blockZ = 0;
            for (int c = 0; c < ch; c++)
            {
                double m = blockSum[c] / blockFrames;
                ring[ringPos * ch + c] = m;
                blockSum[c] = 0;
                blockZ += weights[c] * m;
            }
            ringPos = (ringPos + 1) % WINDOW_BLOCKS;
            double z = 0;
            for (int c = 0; c < ch; c++)
            {
                double m = 0;
                for (int k = 0; k < WINDOW_BLOCKS; k++) m += ring[k * ch + c];
                z += weights[c] * m / WINDOW_BLOCKS;
            }
            LastLufs = z > 1e-10 ? -0.691 + 10.0 * Math.Log10(z) : -100.0;
            LastBlockLufs = blockZ > 1e-12 ? -0.691 + 10.0 * Math.Log10(blockZ) : -100.0;
        }
    }

    // ============ 增益控制器（前馈 + 防振荡）============
    // 原理：输出响度 = 输入响度 + 增益，故增益目标 = 目标响度 - 实测输入响度。
    // 纯前馈结构没有反馈环路，原理上不可能持续振荡；再辅以：
    //   1) 死区（±0.75dB 内不动作）  2) 非对称平滑（压快抬慢，大误差加速）
    //   3) 增益上下限钳位            4) 静音保持（防止背景噪声抽吸）
    internal sealed class GainController
    {
        public const float MIN_GAIN_DB = -50f;    // 最多压 50dB（配合目标 -40 的深目标）
        public const float MAX_GAIN_DB = 12f;     // 最多提 12dB
        const float DEADBAND_DB = 0.75f;
        // 静音阈值：必须高于内容停止后滤波器衰减尾的读数（约 -52 LUFS），
        // 否则衰减尾会被当成"极安静内容"把保持增益毒化到 +12dB
        public const float SILENCE_LUFS = -48f;
        const float FAST_STEP_DB = 8f;
        const float ATTACK_S = 0.10f;    // 压（增益下降）快
        const float RELEASE_S = 0.60f;   // 抬（增益上升）慢

        public float TargetLufs = -16f;
        public float TargetDb;           // 测量阶段每 100ms 更新
        public float GainDb { get; private set; }

        readonly float kAttack, kRelease;
        float gainLin = 1f;
        // 目标历史环（20 × 100ms = 2 秒）：静音冻结时取 2 秒前的目标，
        // 避开内容停止后 400ms 滑窗衰减斜坡造成的"逐渐安静"误判
        const int TARGET_HISTORY = 20;
        readonly float[] targetHistory = new float[TARGET_HISTORY];
        int histPos;

        public GainController(float fs)
        {
            kAttack = (float)(1 - Math.Exp(-1.0 / (fs * ATTACK_S)));
            kRelease = (float)(1 - Math.Exp(-1.0 / (fs * RELEASE_S)));
        }

        public float ComputeTarget(double lufs, double lastBlockLufs)
        {
            // 最近一个 100ms 块低于静音阈值 → 冻结增益目标，取 1 秒前的目标值。
            // 内容停止后滑窗读数沿衰减斜坡回落（-24→-26→-29…），期间会被误判为
            // "逐渐安静的内容"而不断抬高目标；冻结若用当前值，会继承被斜坡污染的
            // 目标（响亮内容停止后增益一路释放，下一段内容在收敛前冲出来）
            if (lastBlockLufs < SILENCE_LUFS) return targetHistory[histPos];
            // 瞬态加速：100ms 块明显高于 400ms 窗（突然变响，如枪声）时用块值做压降决策，
            // 响应缩短到 100~200ms；普通音乐节拍的块间波动低于 6dB 阈值，不受影响
            double cutLufs = lastBlockLufs > lufs + 6.0 ? lastBlockLufs : lufs;
            float err = TargetLufs - (float)cutLufs;
            float t = Math.Abs(err) <= DEADBAND_DB ? 0f
                    : (err < MIN_GAIN_DB ? MIN_GAIN_DB : (err > MAX_GAIN_DB ? MAX_GAIN_DB : err));
            targetHistory[histPos] = t;
            histPos = (histPos + 1) % TARGET_HISTORY;
            return t;
        }

        // 逐采样平滑（渲染线程调用），ch = 缓冲声道数
        public void ProcessBlock(float[] buf, int frames, int ch)
        {
            float targetLin = (float)Math.Pow(10.0, TargetDb / 20.0);
            float k = targetLin < gainLin ? kAttack : kRelease;
            if (Math.Abs(TargetDb - GainDb) > FAST_STEP_DB) k *= 3f;
            int n = frames * ch;
            for (int i = 0; i < n; i++)
            {
                gainLin += (targetLin - gainLin) * k;
                buf[i] *= gainLin;
            }
            GainDb = (float)(20.0 * Math.Log10(gainLin < 1e-6 ? 1e-6 : gainLin));
        }
    }

    // ============ 前瞻峰值限幅器 ============
    // 5ms 前瞻 + 瞬时启动/200ms 释放，上限 -1.5dBFS，防止提升增益后削波
    internal sealed class LookaheadLimiter
    {
        readonly float[] delayL, delayR;
        readonly int len;
        readonly float ceiling;
        readonly float releaseK;
        int pos;
        float gr = 1f;

        public LookaheadLimiter(float fs, float lookaheadMs, float releaseMs)
        {
            len = Math.Max(16, (int)(fs * lookaheadMs / 1000.0));
            delayL = new float[len];
            delayR = new float[len];
            ceiling = (float)Math.Pow(10.0, -1.5 / 20.0);
            releaseK = (float)(1 - Math.Exp(-1.0 / (fs * releaseMs / 1000.0)));
        }

        public void ProcessBlock(float[] buf, int frames, int ch)
        {
            for (int i = 0; i < frames; i++)
            {
                int o = i * ch;
                float l = buf[o];
                float r = ch > 1 ? buf[o + 1] : l;
                delayL[pos] = l;
                delayR[pos] = r;
                float peak = 0f;
                for (int k = 0; k < len; k++)
                {
                    float a = Math.Abs(delayL[k]);
                    if (a > peak) peak = a;
                    a = Math.Abs(delayR[k]);
                    if (a > peak) peak = a;
                }
                float target = peak > ceiling ? ceiling / peak : 1f;
                gr = target < gr ? target : gr + (target - gr) * releaseK;   // 瞬时压、慢恢复
                int outp = pos + 1; if (outp >= len) outp = 0;
                buf[o] = delayL[outp] * gr;
                if (ch > 1) buf[o + 1] = delayR[outp] * gr;
                pos = outp;
            }
        }
    }

    // ============ 瞬态抑制器 ============
    // 枪声等响度瞬时剧变：LUFS 测量窗（400ms）来不及反应，前几发会漏网。
    // 本模块用快速包络（5ms 启动）对比 2 秒环境参考：信号高出环境 10dB 以上时
    // 立即按比例削减（约 5~10ms 生效），200ms 释放——只抓"突然变响"，
    // 不碰正常音乐动态（慢速渐强、普通鼓点不受影响）。
    internal sealed class TransientSuppressor
    {
        readonly float envAttack, envRelease, slowK, grReleaseK;
        float fastEnv, slowRef;
        float gr = 1f;
        const float MARGIN_LIN = 3.16f;   // 10dB 触发阈值
        const float RATIO = 0.9f;         // 超出部分削减 90%

        public float LastGrDb { get; private set; }

        public TransientSuppressor(float fs)
        {
            envAttack = (float)(1 - Math.Exp(-1.0 / (fs * 0.005)));
            envRelease = (float)(1 - Math.Exp(-1.0 / (fs * 0.100)));
            slowK = (float)(1 - Math.Exp(-1.0 / (fs * 2.0)));
            grReleaseK = (float)(1 - Math.Exp(-1.0 / (fs * 0.200)));
        }

        // 内容开始时把环境参考跳到当前包络，避免慢参考追赶造成的启动期误削减
        public void Reset() { slowRef = fastEnv; gr = 1f; }

        public void ProcessBlock(float[] buf, int frames, int ch)
        {
            for (int i = 0; i < frames; i++)
            {
                int o = i * ch;
                float x = Math.Abs(buf[o]);
                if (ch > 1) { float y = Math.Abs(buf[o + 1]); if (y > x) x = y; }
                fastEnv += (x - fastEnv) * (x > fastEnv ? envAttack : envRelease);
                slowRef += (fastEnv - slowRef) * slowK;
                float ratio = fastEnv / (slowRef * MARGIN_LIN + 1e-12f);
                float target = ratio > 1f ? (float)Math.Pow(1.0 / ratio, RATIO) : 1f;
                gr = target < gr ? target : gr + (target - gr) * grReleaseK;   // 瞬时压、慢恢复
                if (gr < 1f)
                {
                    buf[o] *= gr;
                    if (ch > 1) buf[o + 1] *= gr;
                }
            }
            LastGrDb = gr < 1f ? (float)(20.0 * Math.Log10(gr)) : 0f;
        }
    }

    // ============ 单生产者单消费者浮点环形缓冲（仅音频线程访问，无需锁）============
    internal sealed class FloatFifo
    {
        readonly float[] buf;
        int head, tail, count;

        public FloatFifo(int capacityFloats) { buf = new float[capacityFloats]; }

        public int Count { get { return count; } }

        public void Write(float[] src, int off, int n)
        {
            if (n > buf.Length) { off += n - buf.Length; n = buf.Length; }
            int free = buf.Length - count;
            if (n > free)                       // 满则丢最旧（保持低延迟，不积压）
            {
                int drop = n - free;
                head = (head + drop) % buf.Length;
                count -= drop;
            }
            int first = n < buf.Length - tail ? n : buf.Length - tail;
            Array.Copy(src, off, buf, tail, first);
            Array.Copy(src, off + first, buf, 0, n - first);
            tail = (tail + n) % buf.Length;
            count += n;
        }

        public int Read(float[] dst, int off, int n)
        {
            n = n < count ? n : count;
            if (n <= 0) return 0;
            int first = n < buf.Length - head ? n : buf.Length - head;
            Array.Copy(buf, head, dst, off, first);
            Array.Copy(buf, 0, dst, off + first, n - first);
            head = (head + n) % buf.Length;
            count -= n;
            return n;
        }

        public void Clear() { head = tail = count = 0; }
    }

    // ============ 流式线性重采样器 ============
    // 从 fifo 拉取源帧，按 Ratio 输出目标采样率帧；源不足补静音。
    // Ratio 由引擎的漂移补偿（离散 PI 控制，极点 0.9，单调收敛无振荡）缓慢微调，
    // 吸收虚拟声卡与真实声卡之间的时钟漂移，避免周期性爆音。
    internal sealed class Resampler
    {
        readonly int ch;
        readonly float[] win;       // 源帧窗口
        int winFrames, baseFrame;   // win[0] 对应的绝对源帧号、窗口内有效帧数
        double pos;                 // 当前输出对应的源位置（帧）
        public double Ratio = 1.0;  // 每输出 1 帧消耗的源帧数

        public Resampler(int channels)
        {
            ch = channels;
            win = new float[16384];
        }

        public void Pull(FloatFifo fifo, float[] dst, int dstFrames)
        {
            int produced = 0;
            while (produced < dstFrames)
            {
                int i0 = (int)pos;
                double frac = pos - i0;
                int need = i0 + 1;
                // 丢弃已消费帧
                int drop = i0 - baseFrame;
                if (drop > 0 && drop <= winFrames)
                {
                    Array.Copy(win, drop * ch, win, 0, (winFrames - drop) * ch);
                    winFrames -= drop;
                    baseFrame += drop;
                }
                else if (drop > winFrames) { winFrames = 0; baseFrame = i0; }
                // 补足到 need：线性插值需要 i0 与 i0+1 两个样本，
                // 因此窗口必须覆盖 [baseFrame, need] 共 need-baseFrame+1 帧
                int want = need - baseFrame + 1;
                if (winFrames < want)
                {
                    int missing = want - winFrames;
                    int space = win.Length / ch - winFrames;
                    if (space <= 0) { winFrames = 0; baseFrame = i0; space = win.Length / ch; missing = want; }
                    int read = fifo.Read(win, winFrames * ch, (missing < space ? missing : space) * ch) / ch;
                    winFrames += read;
                    if (read < missing)
                    {
                        Array.Clear(win, winFrames * ch, (missing - read) * ch);
                        winFrames = want;
                    }
                }
                // 线性插值输出
                if (Ratio == 1.0)
                {
                    int o = (i0 - baseFrame) * ch;
                    for (int c = 0; c < ch; c++) dst[produced * ch + c] = win[o + c];
                }
                else
                {
                    int oa = (i0 - baseFrame) * ch, ob = oa + ch;
                    for (int c = 0; c < ch; c++)
                    {
                        float a = win[oa + c], b = win[ob + c];
                        dst[produced * ch + c] = (float)(a + (b - a) * frac);
                    }
                }
                pos += Ratio;
                produced++;
            }
        }
    }
}
