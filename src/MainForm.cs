using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LoudEQ
{
    internal sealed class MainForm : Form
    {
        readonly AudioEngine engine;
        readonly Config cfg;
        readonly EventWaitHandle quitRequest;

        readonly LoudnessMeterControl meter;
        readonly TrackBar targetSlider, volSlider;
        readonly NumericUpDown targetInput;
        readonly Label volValue, statusLabel, inputLabel, outputLabel;
        readonly CheckBox enableBox, autostartBox;
        readonly Button fixButton, openSiteButton;
        readonly Panel dotPanel;
        readonly System.Windows.Forms.Timer timer;

        NotifyIcon tray;
        bool reallyExit, trayNotified, syncing, autoSwitchDone;
        string prevDefaultId;
        bool didSwitchDefault, switching;
        NotificationClient notify;
        IMMDeviceEnumerator notifyEnum;
        int tickCount;

        public MainForm(AudioEngine engine, Config cfg, EventWaitHandle quitRequest)
        {
            this.engine = engine;
            this.cfg = cfg;
            this.quitRequest = quitRequest;

            Text = "响度均衡器 LoudEQ";
            ClientSize = new Size(400, 300);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(0x24, 0x26, 0x33);
            ForeColor = Color.FromArgb(0xE8, 0xE8, 0xEE);
            Font = new Font("Microsoft YaHei UI", 9f);
            Icon = AppIcon.Create();
            StartPosition = FormStartPosition.CenterScreen;

            var titleLabel = new Label { Text = "目标响度", AutoSize = true, Location = new Point(14, 20) };
            targetSlider = new TrackBar
            {
                Minimum = 0,
                Maximum = 80,     // -40 ~ 0 LUFS，步进 0.5
                Value = (int)Math.Round((cfg.TargetLufs + 40f) / 0.5f),
                Location = new Point(84, 12),
                Width = 168,
                TickStyle = TickStyle.None,
                BackColor = BackColor
            };
            targetInput = new NumericUpDown
            {
                Minimum = -40,
                Maximum = 0,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = (decimal)Math.Max(-40f, Math.Min(0f, cfg.TargetLufs)),
                Location = new Point(258, 17),
                Size = new Size(82, 24),
                BackColor = Color.FromArgb(0x33, 0x36, 0x48),
                ForeColor = ForeColor
            };
            var lufsSuffix = new Label { Text = "LUFS", AutoSize = true, Location = new Point(344, 20) };

            var presetLabel = new Label { Text = "预设", AutoSize = true, Location = new Point(14, 64), ForeColor = Color.FromArgb(0x9A, 0x9F, 0xB3) };
            string[] presetTexts = { "-23 广播", "-18 电影", "-16 默认", "-14 流媒体" };
            float[] presetValues = { -23f, -18f, -16f, -14f };

            meter = new LoudnessMeterControl { Location = new Point(14, 96), Size = new Size(372, 66) };

            enableBox = new CheckBox { Text = "启用响度均衡", AutoSize = true, Location = new Point(14, 172) };
            autostartBox = new CheckBox { Text = "开机自启", AutoSize = true, Location = new Point(158, 172) };
            var volLabel = new Label { Text = "输出音量", AutoSize = true, Location = new Point(14, 204) };
            volSlider = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = cfg.MasterVolume,
                Location = new Point(84, 195),
                Width = 130,
                AutoSize = false,
                Height = 30,
                TickStyle = TickStyle.None,
                BackColor = BackColor
            };
            volValue = new Label { AutoSize = true, Location = new Point(218, 201) };

            dotPanel = new Panel { Location = new Point(14, 234), Size = new Size(12, 12) };
            statusLabel = new Label { AutoSize = false, Location = new Point(34, 230), Size = new Size(252, 20), AutoEllipsis = true };
            fixButton = new Button
            {
                Text = "恢复路由",
                Location = new Point(296, 227),
                Size = new Size(90, 26),
                Visible = false,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0x33, 0x36, 0x48),
                ForeColor = ForeColor
            };
            fixButton.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4E, 0x62);
            openSiteButton = new Button
            {
                Text = "下载驱动",
                Location = new Point(296, 227),
                Size = new Size(90, 26),
                Visible = false,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0x33, 0x36, 0x48),
                ForeColor = ForeColor
            };
            openSiteButton.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4E, 0x62);

            inputLabel = new Label
            {
                AutoSize = true,
                Location = new Point(14, 258),
                ForeColor = Color.FromArgb(0x9A, 0x9F, 0xB3),
                Font = new Font("Microsoft YaHei UI", 8f)
            };
            outputLabel = new Label
            {
                AutoSize = true,
                Location = new Point(14, 276),
                ForeColor = Color.FromArgb(0x9A, 0x9F, 0xB3),
                Font = new Font("Microsoft YaHei UI", 8f)
            };

            Controls.Add(titleLabel);
            Controls.Add(targetSlider);
            Controls.Add(targetInput);
            Controls.Add(lufsSuffix);
            Controls.Add(presetLabel);
            for (int i = 0; i < 4; i++)
            {
                var b = new Button
                {
                    Text = presetTexts[i],
                    Location = new Point(56 + i * 82, 58),
                    Size = new Size(76, 24),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(0x33, 0x36, 0x48),
                    ForeColor = ForeColor
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4E, 0x62);
                float v = presetValues[i];
                b.Click += (s, e) => targetSlider.Value = (int)Math.Round((v + 40f) / 0.5f);
                Controls.Add(b);
            }
            Controls.Add(meter);
            Controls.Add(enableBox);
            Controls.Add(autostartBox);
            Controls.Add(volLabel);
            Controls.Add(volSlider);
            Controls.Add(volValue);
            Controls.Add(dotPanel);
            Controls.Add(statusLabel);
            Controls.Add(fixButton);
            Controls.Add(openSiteButton);
            Controls.Add(inputLabel);
            Controls.Add(outputLabel);

            targetSlider.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                float v = -40f + targetSlider.Value * 0.5f;
                engine.TargetLufs = v;
                cfg.TargetLufs = v;
                cfg.Save();
                syncing = true;
                targetInput.Value = (decimal)v;
                syncing = false;
                meter.TargetLufs = v;
            };
            targetInput.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                float v = (float)targetInput.Value;
                syncing = true;
                targetSlider.Value = (int)Math.Round((v + 40f) / 0.5f);
                syncing = false;
                engine.TargetLufs = v;
                cfg.TargetLufs = v;
                cfg.Save();
                meter.TargetLufs = v;
            };
            volSlider.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                cfg.MasterVolume = volSlider.Value;
                cfg.Save();
                engine.MasterGain = (float)Math.Pow(volSlider.Value / 100f, 2);
                volValue.Text = volSlider.Value + "%";
            };
            enableBox.CheckedChanged += (s, e) => { if (!syncing) SetEnabled(enableBox.Checked); };
            autostartBox.CheckedChanged += (s, e) => { if (!syncing) ApplyAutostart(autostartBox.Checked); };
            fixButton.Click += (s, e) => FixRoute();
            openSiteButton.Click += (s, e) => { try { Process.Start("https://vb-audio.com/Cable/"); } catch { } };
            FormClosing += OnFormClosing;
            Load += OnFormLoad;

            syncing = true;
            enableBox.Checked = cfg.Enabled;
            autostartBox.Checked = cfg.Autostart;
            targetInput.Value = (decimal)cfg.TargetLufs;
            volValue.Text = cfg.MasterVolume + "%";
            syncing = false;
            meter.TargetLufs = cfg.TargetLufs;

            SetupTray();
            timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += TimerTick;
        }

        void OnFormLoad(object sender, EventArgs e)
        {
            SetupNotifications();
            SetEnabled(cfg.Enabled);
            timer.Start();
        }

        // ============ 启用/停用与默认设备路由 ============
        void SetEnabled(bool on)
        {
            if (on)
            {
                prevDefaultId = CoreAudio.GetDefaultDeviceId(AudioConst.E_RENDER, AudioConst.ROLE_CONSOLE);
                didSwitchDefault = false;
                string cable = engine.CableRenderId;
                if (!string.IsNullOrEmpty(cable) && cable != prevDefaultId)
                {
                    switching = true;
                    if (CoreAudio.SetDefaultDevice(cable)) didSwitchDefault = true;
                    switching = false;
                }
            }
            engine.Enabled = on;
            if (!on && didSwitchDefault)
            {
                switching = true;
                CoreAudio.SetDefaultDevice(prevDefaultId);
                switching = false;
                didSwitchDefault = false;
            }
            cfg.Enabled = on;
            cfg.Save();
        }

        void RestoreDefault()
        {
            if (didSwitchDefault && !string.IsNullOrEmpty(prevDefaultId))
            {
                CoreAudio.SetDefaultDevice(prevDefaultId);
                didSwitchDefault = false;
            }
        }

        void FixRoute()
        {
            if (!string.IsNullOrEmpty(engine.CableRenderId))
            {
                switching = true;
                CoreAudio.SetDefaultDevice(engine.CableRenderId);
                switching = false;
                autoSwitchDone = true;
            }
        }

        void ApplyAutostart(bool on)
        {
            cfg.Autostart = on;
            cfg.Save();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (on) key.SetValue("LoudEQ", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("LoudEQ", false);
                }
            }
            catch { }
        }

        // ============ 设备变更通知 ============
        void SetupNotifications()
        {
            try
            {
                notify = new NotificationClient();
                notify.DefaultDeviceChanged += OnDefaultDeviceChanged;
                notify.DeviceListChanged += () => RequestRestartDebounced();
                notifyEnum = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(CoreAudio.CreateEnumerator());
                notifyEnum.RegisterEndpointNotificationCallback(notify);
            }
            catch { }
        }

        long lastRestartTick;

        void RequestRestartDebounced()
        {
            // 启动瞬间的默认设备切换会引发一串设备通知 → 多次重建流；
            // 1 秒内合并为一次
            long t = Environment.TickCount;
            if (t - lastRestartTick > 1000) { lastRestartTick = t; engine.RequestRestart(); }
        }

        void OnDefaultDeviceChanged(int flow, int role, string id)
        {
            if (flow != AudioConst.E_RENDER || role != AudioConst.ROLE_CONSOLE) return;
            if (switching) return;               // 自己触发的切换
            RequestRestartDebounced();           // 输出设备可能变了，重建流
        }

        // ============ 定时刷新 ============
        void TimerTick(object sender, EventArgs e)
        {
            if (quitRequest != null && quitRequest.WaitOne(0))
            {
                reallyExit = true;
                Close();
                return;
            }
            // 表头显示输出响度（输入+增益 = 均衡结果）；另附输入与增益读数
            meter.InputLufs = engine.CurrentLufs;
            meter.Lufs = engine.Enabled ? engine.CurrentLufs + engine.GainDb : engine.CurrentLufs;
            meter.TargetLufs = engine.TargetLufs;
            meter.GainDb = engine.GainDb;
            meter.Active = engine.Enabled;
            meter.Invalidate();

            string st = engine.Status;
            bool noCable = st.Contains("虚拟声卡");
            openSiteButton.Visible = noCable && !fixButton.Visible;
            if (!engine.Enabled)
            {
                statusLabel.Text = "已暂停（旁路直通，声音不处理）";
                dotPanel.BackColor = Color.FromArgb(0x8A, 0x8F, 0xA3);
            }
            else if (st == "运行中")
            {
                statusLabel.Text = "运行中";
                dotPanel.BackColor = Color.FromArgb(0x3D, 0xBE, 0x7E);
            }
            else if (noCable)
            {
                statusLabel.Text = "未检测到虚拟声卡，请安装驱动";
                dotPanel.BackColor = Color.FromArgb(0xE8, 0xA3, 0x3D);
            }
            else
            {
                statusLabel.Text = st;
                dotPanel.BackColor = Color.FromArgb(0xE0, 0x52, 0x4D);
            }

            inputLabel.Text = "输入: " + (engine.InputName != "" ? engine.InputName : "—")
                + (engine.InputRate > 0 ? "  " + engine.InputRate + " Hz" : "");
            outputLabel.Text = "输出: " + (engine.OutputName != "" ? engine.OutputName : "—")
                + (engine.OutputRate > 0 ? "  " + engine.OutputRate + " Hz" : "");

            if (++tickCount % 20 == 0) UpdateRouteStatus();
        }

        void UpdateRouteStatus()
        {
            bool misrouted = false;
            if (cfg.Enabled && !string.IsNullOrEmpty(engine.CableRenderId))
            {
                string def = CoreAudio.GetDefaultDeviceId(AudioConst.E_RENDER, AudioConst.ROLE_CONSOLE);
                if (def != null && def != engine.CableRenderId)
                {
                    misrouted = true;
                    if (!autoSwitchDone)
                    {
                        // 启动后首次自动把默认设备切到 CABLE
                        switching = true;
                        didSwitchDefault = CoreAudio.SetDefaultDevice(engine.CableRenderId);
                        switching = false;
                        autoSwitchDone = true;
                        misrouted = false;
                    }
                }
                else autoSwitchDone = true;
            }
            if (misrouted)
            {
                fixButton.Visible = true;
                statusLabel.Text = "警告：默认输出已切到其他设备，声音未经过处理";
                dotPanel.BackColor = Color.FromArgb(0xE0, 0x52, 0x4D);
            }
            else fixButton.Visible = false;
        }

        // ============ 托盘 ============
        void SetupTray()
        {
            tray = new NotifyIcon
            {
                Icon = AppIcon.Create(),
                Text = "响度均衡器 LoudEQ",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (s, e) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => { reallyExit = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowWindow();
        }

        void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!reallyExit)
            {
                e.Cancel = true;
                Hide();
                if (!trayNotified)
                {
                    trayNotified = true;
                    tray.ShowBalloonTip(3000, "响度均衡器", "程序仍在后台运行，双击托盘图标打开窗口。", ToolTipIcon.Info);
                }
                return;
            }
            RestoreDefault();
            cfg.Save();
            if (notifyEnum != null && notify != null)
            {
                try { notifyEnum.UnregisterEndpointNotificationCallback(notify); } catch { }
                Marshal.ReleaseComObject(notifyEnum);
                notifyEnum = null;
            }
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        }
    }
}
