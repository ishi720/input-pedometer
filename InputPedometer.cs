// InputPedometer.cs
// マウスカーソルの移動距離を「画面上の実際の長さ（メートル）」で記録するツール
//
// ビルド（Windowsに標準で入っている.NET Frameworkのコンパイラで可）:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:InputPedometer.exe InputPedometer.cs
//
// 記録先: %APPDATA%\InputPedometer\distance.csv  （date,hour,meters）

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InputPedometer
{
    // ------------------------------------------------------------
    // WinAPI定義
    // ------------------------------------------------------------
    static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_MOUSEMOVE = 0x0200;
        public const uint LLMHF_INJECTED = 0x00000001;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int MDT_RAW_DPI = 2;
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // モニターの物理DPI（EDIDから取得した実寸ベース）
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    }

    // ------------------------------------------------------------
    // モニター情報（位置と物理DPI）
    // ------------------------------------------------------------
    class MonitorSpec
    {
        public Rectangle Bounds;
        public double DpiX;
        public double DpiY;
        public bool Estimated; // 物理DPIが取れず96dpiで仮定した場合 true

        public static MonitorSpec FromHandle(IntPtr hMon)
        {
            var spec = new MonitorSpec();

            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(hMon, ref mi);
            spec.Bounds = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);

            uint dx, dy;
            int hr = Native.GetDpiForMonitor(hMon, Native.MDT_RAW_DPI, out dx, out dy);
            if (hr == 0 && dx > 0 && dy > 0)
            {
                spec.DpiX = dx;
                spec.DpiY = dy;
            }
            else
            {
                // プロジェクターや一部の変換アダプタ経由だと実寸が取れない
                spec.DpiX = 96;
                spec.DpiY = 96;
                spec.Estimated = true;
            }
            return spec;
        }
    }

    // ------------------------------------------------------------
    // カーソル移動の検知と距離計算
    // ------------------------------------------------------------
    class MouseTracker : IDisposable
    {
        const double MetersPerInch = 0.0254;

        // 1回のイベントでこれ以上動いたら「ワープ」とみなして数えない
        // （SetCursorPosで飛ばすアプリ、リモートデスクトップ復帰など）
        const double MaxJumpMeters = 0.10;

        IntPtr hook = IntPtr.Zero;
        Native.LowLevelMouseProc proc; // GCに回収されないようフィールドで保持
        readonly Dictionary<IntPtr, MonitorSpec> cache = new Dictionary<IntPtr, MonitorSpec>();
        bool hasLast;
        Native.POINT last;

        public event Action<double> Moved;

        public void Start()
        {
            proc = HookProc;
            hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        // ディスプレイ構成が変わったら呼ぶ
        public void ResetCache()
        {
            cache.Clear();
            hasLast = false;
        }

        public MonitorSpec GetSpec(Native.POINT pt)
        {
            IntPtr hMon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
            MonitorSpec spec;
            if (!cache.TryGetValue(hMon, out spec))
            {
                spec = MonitorSpec.FromHandle(hMon);
                cache[hMon] = spec;
            }
            return spec;
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt64() == Native.WM_MOUSEMOVE)
            {
                var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                bool injected = (info.flags & Native.LLMHF_INJECTED) != 0;
                Handle(info.pt, injected);
            }
            // フック内は重い処理をしない（遅いとWindowsにフックを外される）
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void Handle(Native.POINT pt, bool injected)
        {
            MonitorSpec spec = GetSpec(pt);

            // フックの座標は画面端でクリップされていないことがあるので、モニター内に収める
            pt.X = Math.Max(spec.Bounds.Left, Math.Min(spec.Bounds.Right - 1, pt.X));
            pt.Y = Math.Max(spec.Bounds.Top, Math.Min(spec.Bounds.Bottom - 1, pt.Y));

            if (hasLast && !injected)
            {
                double ix = (pt.X - last.X) / spec.DpiX; // インチ
                double iy = (pt.Y - last.Y) / spec.DpiY;
                double meters = Math.Sqrt(ix * ix + iy * iy) * MetersPerInch;

                if (meters > 0 && meters <= MaxJumpMeters && Moved != null)
                {
                    Moved(meters);
                }
            }

            // 他のプログラムが送った移動（injected）は数えないが、位置は更新する
            last = pt;
            hasLast = true;
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
        }
    }

    // ------------------------------------------------------------
    // 日付×時間ごとの距離を保存
    // ------------------------------------------------------------
    class DistanceLog
    {
        readonly string path;
        readonly SortedDictionary<string, double[]> days = new SortedDictionary<string, double[]>();
        public bool Dirty { get; private set; }

        public DistanceLog(string path)
        {
            this.path = path;
        }

        public string FilePath { get { return path; } }

        static string Key(DateTime t)
        {
            return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        double[] Day(string key)
        {
            double[] hours;
            if (!days.TryGetValue(key, out hours))
            {
                hours = new double[24];
                days[key] = hours;
            }
            return hours;
        }

        public void Add(double meters)
        {
            DateTime now = DateTime.Now;
            Day(Key(now))[now.Hour] += meters;
            Dirty = true;
        }

        public double Today()
        {
            double sum = 0;
            foreach (double h in Day(Key(DateTime.Now))) sum += h;
            return sum;
        }

        public double ThisHour()
        {
            DateTime now = DateTime.Now;
            return Day(Key(now))[now.Hour];
        }

        public void Load()
        {
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                string[] p = line.Split(',');
                if (p.Length != 3) continue;
                int hour;
                double meters;
                if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) continue; // ヘッダー行など
                if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out meters)) continue;
                if (hour < 0 || hour > 23) continue;
                Day(p[0])[hour] += meters;
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            sb.AppendLine("date,hour,meters");
            foreach (KeyValuePair<string, double[]> d in days)
            {
                for (int h = 0; h < 24; h++)
                {
                    if (d.Value[h] <= 0) continue;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:0.000}", d.Key, h, d.Value[h]));
                }
            }
            // 書き込み途中で落ちても壊れないよう、一時ファイル経由で置き換える
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            Dirty = false;
        }
    }

    // ------------------------------------------------------------
    // 画面
    // ------------------------------------------------------------
    class MainForm : Form
    {
        readonly MouseTracker tracker = new MouseTracker();
        readonly DistanceLog log;
        readonly System.Windows.Forms.Timer viewTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();

        readonly Label lblToday = new Label();
        readonly Label lblHour = new Label();
        readonly Label lblMonitors = new Label();
        readonly CheckBox chkTopMost = new CheckBox();
        readonly CheckBox chkStartup = new CheckBox();
        readonly Button btnFolder = new Button();

        public MainForm(bool startMinimized)
        {
            if (startMinimized) WindowState = FormWindowState.Minimized;

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InputPedometer");
            log = new DistanceLog(Path.Combine(dir, "distance.csv"));

            Text = "Input Pedometer";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(360, 216);
            Font = new Font("Yu Gothic UI", 9f);

            lblToday.Font = new Font("Yu Gothic UI", 20f, FontStyle.Bold);
            lblToday.SetBounds(12, 10, 336, 50);
            lblHour.SetBounds(14, 62, 334, 20);
            lblMonitors.SetBounds(14, 86, 334, 70);
            lblMonitors.ForeColor = SystemColors.GrayText;

            chkTopMost.Text = "最前面に表示";
            chkTopMost.SetBounds(14, 162, 120, 24);
            chkTopMost.CheckedChanged += delegate { TopMost = chkTopMost.Checked; };

            chkStartup.Text = "Windows起動時に自動で起動";
            chkStartup.SetBounds(14, 186, 200, 24);
            chkStartup.Checked = Startup.IsEnabled();
            chkStartup.CheckedChanged += delegate
            {
                try { Startup.SetEnabled(chkStartup.Checked); }
                catch (Exception ex)
                {
                    MessageBox.Show("自動起動の設定に失敗しました。\n" + ex.Message, Text);
                    chkStartup.Checked = Startup.IsEnabled();
                }
            };

            btnFolder.Text = "記録フォルダを開く";
            btnFolder.SetBounds(214, 160, 134, 28);
            btnFolder.Click += delegate
            {
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            };

            Controls.AddRange(new Control[] { lblToday, lblHour, lblMonitors, chkTopMost, chkStartup, btnFolder });

            viewTimer.Interval = 100; // mmまで出すので更新を細かくする
            viewTimer.Tick += delegate { UpdateView(); };
            saveTimer.Interval = 60 * 1000;
            saveTimer.Tick += delegate { SaveIfDirty(); };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { log.Load(); }
            catch (Exception ex) { MessageBox.Show("記録の読み込みに失敗しました。\n" + ex.Message, Text); }

            tracker.Moved += log.Add;
            tracker.Start();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            UpdateMonitorInfo();
            UpdateView();
            viewTimer.Start();
            saveTimer.Start();
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            tracker.ResetCache();
            UpdateMonitorInfo();
        }

        void UpdateView()
        {
            lblToday.Text = "今日 " + FormatDistance(log.Today());
            lblHour.Text = string.Format("この1時間: {0}", FormatDistance(log.ThisHour()));
        }

        void UpdateMonitorInfo()
        {
            var sb = new StringBuilder();
            foreach (Screen s in Screen.AllScreens)
            {
                var center = new Native.POINT();
                center.X = s.Bounds.Left + s.Bounds.Width / 2;
                center.Y = s.Bounds.Top + s.Bounds.Height / 2;
                MonitorSpec spec = tracker.GetSpec(center);
                double wcm = spec.Bounds.Width / spec.DpiX * 2.54;
                double hcm = spec.Bounds.Height / spec.DpiY * 2.54;
                sb.AppendLine(string.Format("{0}: {1}x{2} / {3:0}dpi（{4:0.0}×{5:0.0}cm）{6}",
                    s.DeviceName.Replace(@"\\.\", ""), spec.Bounds.Width, spec.Bounds.Height,
                    spec.DpiX, wcm, hcm, spec.Estimated ? "※推定" : ""));
            }
            lblMonitors.Text = sb.ToString();
        }

        // 「1km234m56cm7mm」のように単位を並べて表示する（0の単位は省略）
        static string FormatDistance(double m)
        {
            long mm = (long)Math.Floor(m * 1000);
            long km = mm / 1000000;
            long mt = mm / 1000 % 1000;
            long cm = mm / 10 % 100;
            long rest = mm % 10;

            var sb = new StringBuilder();
            if (km > 0) sb.Append(km).Append("km");
            if (mt > 0) sb.Append(mt).Append("m");
            if (cm > 0) sb.Append(cm).Append("cm");
            if (rest > 0) sb.Append(rest).Append("mm");
            return sb.Length > 0 ? sb.ToString() : "0mm";
        }

        void SaveIfDirty()
        {
            if (!log.Dirty) return;
            try { log.Save(); }
            catch (IOException) { /* 次回リトライ */ }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            viewTimer.Stop();
            saveTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; // static イベントなので必ず解除
            tracker.Dispose();
            SaveIfDirty();
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------
    // Windows起動時の自動起動（HKCU\...\Run に登録。管理者権限は不要）
    // ------------------------------------------------------------
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "InputPedometer";
        public const string MinimizedArg = "--minimized";

        static string Command()
        {
            return "\"" + Application.ExecutablePath + "\" " + MinimizedArg;
        }

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(ValueName) != null;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command());
                else key.DeleteValue(ValueName, false);
            }
        }

        // exeを移動した場合に備えて、登録済みならパスを今の場所に合わせる
        public static void RefreshPath()
        {
            try { if (IsEnabled()) SetEnabled(true); }
            catch (Exception) { /* 失敗しても動作には影響しない */ }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 座標を物理ピクセルで受け取るため、モニターごとのDPI対応にする
            try { Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch (EntryPointNotFoundException) { /* Windows 10 1703 より前 */ }

            // 二重起動すると二重に数えてしまうので防ぐ
            bool created;
            using (var mutex = new Mutex(true, "InputPedometer_SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Startup.RefreshPath();
                bool minimized = Array.IndexOf(args, Startup.MinimizedArg) >= 0;
                Application.Run(new MainForm(minimized));
            }
        }
    }
}
