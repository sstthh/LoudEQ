using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace LoudEQ
{
    // 简单 ini 格式配置（%APPDATA%\LoudEQ\config.ini）
    internal sealed class Config
    {
        public float TargetLufs = -16f;
        public bool Enabled = true;
        public int MasterVolume = 100;   // 0..100（映射为增益的平方，更符合音量感知）
        public bool Autostart = false;

        public static string AppDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoudEQ");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string FilePath { get { return Path.Combine(AppDir, "config.ini"); } }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    float f; bool b; int n;
                    if (k == "TargetLufs" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) c.TargetLufs = f;
                    else if (k == "Enabled" && bool.TryParse(v, out b)) c.Enabled = b;
                    else if (k == "MasterVolume" && int.TryParse(v, out n)) c.MasterVolume = Math.Max(0, Math.Min(100, n));
                    else if (k == "Autostart" && bool.TryParse(v, out b)) c.Autostart = b;
                }
            }
            catch { }
            if (c.TargetLufs < -40f) c.TargetLufs = -40f;
            if (c.TargetLufs > 0f) c.TargetLufs = 0f;
            return c;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("TargetLufs=" + TargetLufs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Enabled=" + (Enabled ? "1" : "0"));
                sb.AppendLine("MasterVolume=" + MasterVolume);
                sb.AppendLine("Autostart=" + (Autostart ? "1" : "0"));
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
