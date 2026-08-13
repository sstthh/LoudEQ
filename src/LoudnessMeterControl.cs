using System;
using System.Drawing;
using System.Windows.Forms;

namespace LoudEQ
{
    // 实时响度表：-45..0 LUFS 刻度 + 目标标记线 + 当前值/增益文字
    internal sealed class LoudnessMeterControl : Control
    {
        public float Lufs = -100f;        // 显示值 = 输出响度（输入+增益）
        public float InputLufs = -100f;
        public float TargetLufs = -16f;
        public float GainDb = 0f;
        public bool Active = true;
        const float MinL = -50f, MaxL = 0f;

        public LoudnessMeterControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        float X(float l, Rectangle r) { return r.Left + 8 + (l - MinL) / (MaxL - MinL) * (r.Width - 16); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = ClientRectangle;
            using (var bg = new SolidBrush(Color.FromArgb(0x1A, 0x1C, 0x28)))
                g.FillRectangle(bg, r);
            using (var border = new Pen(Color.FromArgb(0x3A, 0x3E, 0x52)))
                g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);

            using (var tickFont = new Font("Segoe UI", 7f))
            using (var tickBrush = new SolidBrush(Color.FromArgb(0x8A, 0x8F, 0xA3)))
            using (var tickPen = new Pen(Color.FromArgb(0x45, 0x49, 0x5E)))
            {
                for (int l = -40; l <= 0; l += 10)
                {
                    float x = X(l, r);
                    g.DrawLine(tickPen, x, r.Top + 4, x, r.Top + 9);
                    g.DrawString(l.ToString(), tickFont, tickBrush, x - 8, r.Top + 12);
                }
            }

            float cur = Active ? Lufs : -100f;
            float x0 = X(MinL, r);
            if (cur > MinL)
            {
                float curX = X(cur > MaxL ? MaxL : cur, r);
                Color fill;
                float d = cur - TargetLufs;
                if (d < -2f) fill = Color.FromArgb(0x4A, 0x9F, 0xE8);      // 低于目标（正在提升）
                else if (d <= 2f) fill = Color.FromArgb(0x3D, 0xBE, 0x7E);  // 接近目标
                else if (d <= 8f) fill = Color.FromArgb(0xE8, 0xA3, 0x3D);  // 高于目标（正在压低）
                else fill = Color.FromArgb(0xE0, 0x52, 0x4D);               // 严重超标
                using (var fb = new SolidBrush(fill))
                    g.FillRectangle(fb, x0, r.Top + 24, curX - x0, 18);
            }

            // 目标标记线
            float tx = X(TargetLufs, r);
            using (var tpen = new Pen(Color.White, 2f))
                g.DrawLine(tpen, tx, r.Top + 22, tx, r.Top + 44);
            using (var tf = new Font("Microsoft YaHei UI", 7.5f))
            using (var tb = new SolidBrush(Color.FromArgb(0xC8, 0xCC, 0xDD)))
            {
                string tstr = "目标 " + TargetLufs.ToString("0.0");
                float tw = tstr.Length * 7f;
                if (tx + tw + 10 < r.Right) g.DrawString(tstr, tf, tb, tx + 4, r.Top + 26);
                else g.DrawString(tstr, tf, tb, tx - tw - 6, r.Top + 26);
            }

            // 输出值 / 输入与增益
            using (var vf = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
            using (var vb = new SolidBrush(Color.White))
            {
                string curStr = cur <= MinL ? "输出 -∞ LUFS" : "输出 " + cur.ToString("0.0") + " LUFS";
                g.DrawString(curStr, vf, vb, x0 + 6, r.Top + 24);
            }
            if (Active)
            {
                string gs = "输入 " + (InputLufs <= MinL ? "-∞" : InputLufs.ToString("0.0"))
                          + " LUFS · 增益 " + (GainDb >= 0 ? "+" : "") + GainDb.ToString("0.0") + " dB";
                using (var gf = new Font("Microsoft YaHei UI", 8f))
                using (var gb = new SolidBrush(Color.FromArgb(0xC8, 0xCC, 0xDD)))
                    g.DrawString(gs, gf, gb, r.Right - gs.Length * 8f - 8, r.Top + 46);
            }
        }
    }
}
