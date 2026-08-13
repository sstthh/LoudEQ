using System;
using System.Threading;
using System.Windows.Forms;

namespace LoudEQ
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool selftest = false, logging = false, quit = false;
            foreach (var a in args)
            {
                if (a.Equals("/selftest", StringComparison.OrdinalIgnoreCase)) selftest = true;
                if (a.Equals("/log", StringComparison.OrdinalIgnoreCase)) logging = true;
                if (a.Equals("/quit", StringComparison.OrdinalIgnoreCase)) quit = true;
            }
            if (selftest) { SelfTest.Run(); return; }

            bool createdNew;
            using (var m = new Mutex(true, "LoudEQ_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 第二个实例：/quit 通知已运行的实例优雅退出（恢复默认设备）
                    if (quit)
                    {
                        try
                        {
                            using (var ev = EventWaitHandle.OpenExisting("LoudEQ_QuitRequest"))
                                ev.Set();
                        }
                        catch { }
                        return;
                    }
                    MessageBox.Show("响度均衡器已经在运行（见系统托盘图标）。", "响度均衡器",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                using (var quitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "LoudEQ_QuitRequest"))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var engine = new AudioEngine())
                    {
                        var cfg = Config.Load();
                        engine.TargetLufs = cfg.TargetLufs;
                        engine.Enabled = cfg.Enabled;
                        engine.MasterGain = (float)Math.Pow(cfg.MasterVolume / 100f, 2);
                        if (logging) engine.SetLogFile(System.IO.Path.Combine(Config.AppDir, "loudEQ.log"));
                        engine.Start();
                        Application.Run(new MainForm(engine, cfg, quitEvt));
                    }
                }
            }
        }
    }
}
