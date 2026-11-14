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
    /// <summary>
    /// このツールで使う Win32 API（user32 / kernel32 / shcore）の定数・構造体・関数宣言。
    /// </summary>
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
    /// <summary>
    /// 1台のモニターの位置と物理DPI。ピクセル数を実際の長さに換算するために使う。
    /// </summary>
    class MonitorSpec
    {
        /// <summary>仮想スクリーン上でのモニターの範囲（物理ピクセル）。</summary>
        public Rectangle Bounds;

        /// <summary>横方向の物理DPI（1インチあたりのピクセル数）。</summary>
        public double DpiX;

        /// <summary>縦方向の物理DPI（1インチあたりのピクセル数）。</summary>
        public double DpiY;

        /// <summary>
        /// 物理DPIが取得できず、96dpi と仮定した値を使っている場合は <c>true</c>。
        /// </summary>
        public bool Estimated;

        /// <summary>
        /// モニターハンドルから位置と物理DPIを取得する。
        /// </summary>
        /// <param name="hMon"><c>MonitorFromPoint</c> などで得たモニターハンドル。</param>
        /// <returns>取得したモニター情報。DPIが取れない場合は 96dpi と仮定し、<see cref="Estimated"/> を <c>true</c> にする。</returns>
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
    /// <summary>
    /// 低レベルマウスフックでカーソルの移動を検知し、移動距離をメートル単位で通知する。
    /// </summary>
    /// <remarks>
    /// 他のプログラムが送った移動（injected）と、1回で <c>MaxJumpMeters</c> を超える移動（ワープ）は数えない。
    /// </remarks>
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

        /// <summary>
        /// カーソルが動いたときに発生する。引数は今回の移動距離（メートル）。
        /// </summary>
        /// <remarks>フックのコールバック内で呼ばれるため、ハンドラーでは重い処理をしないこと。</remarks>
        public event Action<double> Moved;

        /// <summary>
        /// マウスフックを登録して計測を開始する。
        /// </summary>
        /// <exception cref="Win32Exception">フックの登録に失敗した場合。</exception>
        public void Start()
        {
            proc = HookProc;
            hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        /// <summary>
        /// モニター情報のキャッシュと直前のカーソル位置を破棄する。
        /// ディスプレイ構成（解像度・配置・接続台数）が変わったときに呼ぶ。
        /// </summary>
        public void ResetCache()
        {
            cache.Clear();
            hasLast = false;
        }

        /// <summary>
        /// 指定した座標があるモニターの情報を返す。一度取得したモニターはキャッシュする。
        /// </summary>
        /// <param name="pt">仮想スクリーン上の座標（物理ピクセル）。</param>
        /// <returns>その座標に最も近いモニターの情報。</returns>
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

        /// <summary>
        /// マウスフックを解除して計測を止める。
        /// </summary>
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
    /// <summary>
    /// 移動距離を「日付 × 時（0〜23）」ごとに集計し、CSVファイルに保存する。
    /// </summary>
    /// <remarks>CSVの形式は <c>date,hour,meters</c>（例: <c>2026-11-14,9,12.345</c>）。</remarks>
    class DistanceLog
    {
        readonly string path;
        readonly SortedDictionary<string, double[]> days = new SortedDictionary<string, double[]>();

        /// <summary>前回の保存以降に記録が追加されていれば <c>true</c>。</summary>
        public bool Dirty { get; private set; }

        /// <summary>
        /// 保存先のファイルを指定して初期化する。ファイルの読み込みは <see cref="Load"/> で行う。
        /// </summary>
        /// <param name="path">CSVファイルのパス。</param>
        public DistanceLog(string path)
        {
            this.path = path;
        }

        /// <summary>CSVファイルのパス。</summary>
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

        /// <summary>
        /// 現在の日時の枠に移動距離を加算する。
        /// </summary>
        /// <param name="meters">加算する距離（メートル）。</param>
        public void Add(double meters)
        {
            DateTime now = DateTime.Now;
            Day(Key(now))[now.Hour] += meters;
            Dirty = true;
        }

        /// <summary>
        /// 今日（0時から現在まで）の合計距離を返す。
        /// </summary>
        /// <returns>距離（メートル）。</returns>
        public double Today()
        {
            double sum = 0;
            foreach (double h in Day(Key(DateTime.Now))) sum += h;
            return sum;
        }

        /// <summary>
        /// 現在の時間帯（例: 14:00〜14:59）の合計距離を返す。
        /// </summary>
        /// <returns>距離（メートル）。</returns>
        public double ThisHour()
        {
            DateTime now = DateTime.Now;
            return Day(Key(now))[now.Hour];
        }

        /// <summary>
        /// 指定した日の時間帯ごと（0〜23時）の距離を返す。
        /// </summary>
        /// <param name="date">対象の日付（時刻部分は無視する）。</param>
        /// <returns>長さ24の配列のコピー（メートル）。記録がない日はすべて0。</returns>
        public double[] HoursOf(DateTime date)
        {
            double[] hours;
            return days.TryGetValue(Key(date), out hours) ? (double[])hours.Clone() : new double[24];
        }

        /// <summary>
        /// CSVファイルから記録を読み込み、メモリ上の記録に加算する。
        /// ファイルがなければ何もしない。形式が合わない行（ヘッダー行など）は読み飛ばす。
        /// </summary>
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

        /// <summary>
        /// 全期間の記録をCSVファイルに書き出す。距離が0の時間帯は出力しない。
        /// </summary>
        /// <remarks>一時ファイルに書いてから置き換えるので、途中で落ちても元のファイルは壊れない。</remarks>
        /// <exception cref="IOException">ファイルが他のプロセスに使われているなどで書き込めない場合。</exception>
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
    /// <summary>
    /// メイン画面。1日・1時間ごとの移動距離とモニター情報を表示し、記録を定期的に保存する。
    /// </summary>
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
        readonly Button btnHourly = new Button();
        HourlyForm hourlyForm;

        /// <summary>
        /// 画面を組み立てる。計測の開始は <see cref="OnLoad"/> で行う。
        /// </summary>
        /// <param name="startMinimized"><c>true</c> なら最小化した状態で表示する（自動起動時）。</param>
        public MainForm(bool startMinimized)
        {
            if (startMinimized) WindowState = FormWindowState.Minimized;

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InputPedometer");
            log = new DistanceLog(Path.Combine(dir, "distance.csv"));

            Text = "Input Pedometer";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(360, 228);
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

            btnHourly.Text = "時間別の集計";
            btnHourly.SetBounds(214, 192, 134, 28);
            btnHourly.Click += delegate { ShowHourly(); };

            Controls.AddRange(new Control[] { lblToday, lblHour, lblMonitors, chkTopMost, chkStartup, btnFolder, btnHourly });

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
            lblToday.Text = "1日: " + Units.Format(log.Today());
            lblHour.Text = "1時間: " + Units.Format(log.ThisHour());
        }

        void ShowHourly()
        {
            // 開いていれば前面に出すだけ（複数開かない）
            if (hourlyForm != null && !hourlyForm.IsDisposed)
            {
                if (hourlyForm.WindowState == FormWindowState.Minimized) hourlyForm.WindowState = FormWindowState.Normal;
                hourlyForm.Activate();
                return;
            }
            hourlyForm = new HourlyForm(log);
            hourlyForm.Show(this);
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
    // 距離の表示用の書式
    // ------------------------------------------------------------
    /// <summary>
    /// 距離（メートル）を画面表示用の文字列にする。
    /// </summary>
    static class Units
    {
        /// <summary>
        /// 距離を「1km234m56cm7mm」のように単位を並べた文字列にする。0の単位は省略する。
        /// </summary>
        /// <param name="m">距離（メートル）。1mm未満は切り捨てる。</param>
        /// <returns>整形した文字列。1mm未満なら "0mm"。</returns>
        public static string Format(double m)
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

        /// <summary>
        /// グラフの目盛り用に、目盛り間隔に合った単位1つで表す（例: "50cm"、"1.5m"）。
        /// </summary>
        /// <param name="m">目盛りの値（メートル）。</param>
        /// <param name="step">目盛りの間隔（メートル）。単位の選択に使う。</param>
        public static string Axis(double m, double step)
        {
            if (m == 0) return "0";
            if (step >= 1000) return (m / 1000).ToString("0.##", CultureInfo.InvariantCulture) + "km";
            if (step >= 1) return m.ToString("0.##", CultureInfo.InvariantCulture) + "m";
            if (step >= 0.01) return (m * 100).ToString("0.##", CultureInfo.InvariantCulture) + "cm";
            return (m * 1000).ToString("0.##", CultureInfo.InvariantCulture) + "mm";
        }
    }

    // ------------------------------------------------------------
    // 時間別の棒グラフ
    // ------------------------------------------------------------
    /// <summary>
    /// 0〜23時の距離を棒グラフで描くコントロール。マウスを乗せた時間帯を <see cref="HoverChanged"/> で通知する。
    /// </summary>
    class HourlyChart : Control
    {
        static readonly Color BarColor = Color.FromArgb(70, 130, 200);
        static readonly Color NowColor = Color.FromArgb(240, 140, 40);
        static readonly Color HoverColor = Color.FromArgb(30, 80, 150);
        static readonly Color GridColor = Color.FromArgb(225, 225, 225);

        double[] values = new double[24];
        int highlight = -1;
        int hover = -1;

        /// <summary>マウスを乗せた時間帯が変わったときに発生する。引数は時（0〜23）、外れたら -1。</summary>
        public event Action<int> HoverChanged;

        public HourlyChart()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        /// <summary>表示する距離（長さ24、メートル）。</summary>
        public double[] Values
        {
            get { return values; }
            set { values = value; Invalidate(); }
        }

        /// <summary>強調表示する時間帯（現在の時刻）。強調しないなら -1。</summary>
        public int Highlight
        {
            get { return highlight; }
            set { if (highlight != value) { highlight = value; Invalidate(); } }
        }

        /// <summary>マウスを乗せている時間帯。なければ -1。</summary>
        public int Hover { get { return hover; } }

        Rectangle PlotRect()
        {
            return Rectangle.FromLTRB(52, 10, ClientSize.Width - 10, ClientSize.Height - 24);
        }

        // 目盛り間隔を 1, 2, 5 × 10^n のきりのいい値にする
        static double NiceStep(double raw)
        {
            if (raw <= 0) return 0.01;
            double exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / exp;
            double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
            return nice * exp;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle plot = PlotRect();
            if (plot.Width <= 0 || plot.Height <= 0) return;

            double max = 0;
            foreach (double v in values) max = Math.Max(max, v);
            double step = NiceStep(max / 4);
            int ticks = Math.Max(1, (int)Math.Ceiling(max / step - 1e-9));
            double top = step * ticks;

            // 横の目盛り線と値
            using (var gridPen = new Pen(GridColor))
            using (var textBrush = new SolidBrush(SystemColors.GrayText))
            {
                var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                for (int i = 0; i <= ticks; i++)
                {
                    float y = plot.Bottom - (float)i * plot.Height / ticks;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(Units.Axis(step * i, step), Font, textBrush, new RectangleF(0, y - 10, plot.Left - 6, 20), right);
                }

                // 時刻のラベル（狭いときは3時間おき）
                float slot = plot.Width / 24f;
                int every = slot >= 22 ? 1 : 3;
                var center = new StringFormat { Alignment = StringAlignment.Center };
                for (int h = 0; h < 24; h += every)
                {
                    g.DrawString(h.ToString(), Font, textBrush, new RectangleF(plot.Left + h * slot, plot.Bottom + 4, slot, 18), center);
                }

                // 棒
                float barW = Math.Max(1f, slot * 0.7f);
                for (int h = 0; h < 24; h++)
                {
                    if (values[h] <= 0) continue;
                    float bh = (float)(values[h] / top * plot.Height);
                    float x = plot.Left + h * slot + (slot - barW) / 2;
                    Color c = h == hover ? HoverColor : h == highlight ? NowColor : BarColor;
                    using (var b = new SolidBrush(c))
                    {
                        g.FillRectangle(b, x, plot.Bottom - bh, barW, Math.Max(1f, bh));
                    }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle plot = PlotRect();
            int h = -1;
            if (plot.Width > 0 && e.X >= plot.Left && e.X < plot.Right && e.Y >= plot.Top && e.Y < plot.Bottom + 20)
            {
                h = Math.Min(23, (int)((e.X - plot.Left) / (plot.Width / 24f)));
            }
            SetHover(h);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(-1);
        }

        void SetHover(int h)
        {
            if (hover == h) return;
            hover = h;
            Invalidate();
            if (HoverChanged != null) HoverChanged(h);
        }
    }

    // ------------------------------------------------------------
    // 時間別の集計画面
    // ------------------------------------------------------------
    /// <summary>
    /// 1日分の距離を時間帯（0〜23時）ごとに棒グラフで表示する画面。日付を切り替えて過去の記録も見られる。
    /// </summary>
    class HourlyForm : Form
    {
        static readonly CultureInfo Ja = new CultureInfo("ja-JP");

        readonly DistanceLog log;
        readonly HourlyChart chart = new HourlyChart();
        readonly Button btnPrev = new Button();
        readonly Button btnNext = new Button();
        readonly Button btnToday = new Button();
        readonly Label lblDate = new Label();
        readonly Label lblTotal = new Label();
        readonly Label lblInfo = new Label();
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        DateTime date = DateTime.Today;

        /// <summary>
        /// 画面を組み立てる。最初は今日の記録を表示する。
        /// </summary>
        /// <param name="log">表示する記録。</param>
        public HourlyForm(DistanceLog log)
        {
            this.log = log;

            Text = "時間別の集計 - Input Pedometer";
            Font = new Font("Yu Gothic UI", 9f);
            ClientSize = new Size(640, 330);
            MinimumSize = new Size(420, 260);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;

            btnPrev.Text = "◀";
            btnPrev.SetBounds(12, 10, 32, 28);
            btnPrev.Click += delegate { MoveDate(-1); };

            lblDate.SetBounds(46, 10, 150, 28);
            lblDate.TextAlign = ContentAlignment.MiddleCenter;
            lblDate.Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold);

            btnNext.Text = "▶";
            btnNext.SetBounds(198, 10, 32, 28);
            btnNext.Click += delegate { MoveDate(1); };

            btnToday.Text = "今日";
            btnToday.SetBounds(238, 10, 56, 28);
            btnToday.Click += delegate { date = DateTime.Today; RefreshView(); };

            lblTotal.SetBounds(300, 10, 328, 28);
            lblTotal.TextAlign = ContentAlignment.MiddleRight;
            lblTotal.Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold);
            lblTotal.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            chart.SetBounds(12, 46, 616, 250);
            chart.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            chart.HoverChanged += delegate { UpdateInfo(); };

            lblInfo.SetBounds(14, 302, 614, 20);
            lblInfo.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[] { btnPrev, lblDate, btnNext, btnToday, lblTotal, chart, lblInfo });

            // 表示中が今日なら、計測中の値を反映し続ける
            refreshTimer.Interval = 1000;
            refreshTimer.Tick += delegate { if (date == DateTime.Today || chart.Highlight >= 0) RefreshView(); };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RefreshView();
            refreshTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) { MoveDate(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { MoveDate(1); e.Handled = true; }
        }

        void MoveDate(int days)
        {
            DateTime d = date.AddDays(days);
            if (d > DateTime.Today) return; // 未来は表示しない
            date = d;
            RefreshView();
        }

        void RefreshView()
        {
            DateTime today = DateTime.Today;
            double[] hours = log.HoursOf(date);
            double sum = 0;
            foreach (double h in hours) sum += h;

            chart.Values = hours;
            chart.Highlight = date == today ? DateTime.Now.Hour : -1;
            lblDate.Text = date.ToString("yyyy/MM/dd (ddd)", Ja);
            lblTotal.Text = "合計: " + Units.Format(sum);
            btnNext.Enabled = date < today;
            btnToday.Enabled = date != today;
            UpdateInfo();
        }

        void UpdateInfo()
        {
            double[] hours = chart.Values;
            int h = chart.Hover;
            if (h >= 0)
            {
                lblInfo.Text = string.Format("{0}:00〜{0}:59　{1}", h, Units.Format(hours[h]));
                return;
            }

            // マウスを乗せていないときは、一番動いた時間帯を出す
            int peak = -1;
            for (int i = 0; i < 24; i++)
            {
                if (hours[i] > 0 && (peak < 0 || hours[i] > hours[peak])) peak = i;
            }
            lblInfo.Text = peak < 0
                ? "この日の記録はありません"
                : string.Format("最も動いた時間帯: {0}:00〜{0}:59（{1}）　※棒にマウスを乗せると値を表示", peak, Units.Format(hours[peak]));
        }
    }

    // ------------------------------------------------------------
    // Windows起動時の自動起動（HKCU\...\Run に登録。管理者権限は不要）
    // ------------------------------------------------------------
    /// <summary>
    /// Windows起動時の自動起動の設定。
    /// <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c> に登録するので、管理者権限は不要。
    /// </summary>
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "InputPedometer";
        /// <summary>自動起動時に付ける引数。これが付いていると最小化して起動する。</summary>
        public const string MinimizedArg = "--minimized";

        static string Command()
        {
            return "\"" + Application.ExecutablePath + "\" " + MinimizedArg;
        }

        /// <summary>
        /// 自動起動が登録されているかを返す。
        /// </summary>
        /// <returns>登録されていれば <c>true</c>。</returns>
        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(ValueName) != null;
            }
        }

        /// <summary>
        /// 自動起動を登録または解除する。登録時は現在の exe の場所を書き込む。
        /// </summary>
        /// <param name="enabled"><c>true</c> で登録、<c>false</c> で解除。</param>
        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command());
                else key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>
        /// 自動起動が登録済みなら、登録されている exe の場所を現在の場所に更新する。
        /// exe を移動した場合に備えて起動時に呼ぶ。失敗しても例外は投げない。
        /// </summary>
        public static void RefreshPath()
        {
            try { if (IsEnabled()) SetEnabled(true); }
            catch (Exception) { /* 失敗しても動作には影響しない */ }
        }
    }

    /// <summary>
    /// エントリーポイント。
    /// </summary>
    static class Program
    {
        /// <summary>
        /// DPI対応を設定し、二重起動を防いだうえでメイン画面を起動する。
        /// </summary>
        /// <param name="args">コマンドライン引数。<see cref="Startup.MinimizedArg"/> があれば最小化して起動する。</param>
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
