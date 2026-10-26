// CopyNotify - 剪貼簿成功更新時，在畫面中央顯示提示與複製內容。
// 編譯：執行 build.bat（使用 Windows 內建的 .NET Framework csc.exe）
using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CopyNotify
{
    static class Native
    {
        public const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "CopyNotify_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("複製提示已經在執行中（請看右下角系統匣圖示）。", "CopyNotify");
                    return;
                }
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
                GC.KeepAlive(mutex);
            }
        }
    }

    // 提示位置：X/Y 為 0~1 的對齊比例（0 = 靠左/上、0.5 = 置中、1 = 靠右/下），或跟隨滑鼠游標
    class PositionSettings
    {
        const string KeyPath = @"Software\CopyNotify";
        public bool FollowCursor;
        public double X = 0.5, Y = 0.5;

        public static PositionSettings Load()
        {
            var pos = new PositionSettings();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    string v = key == null ? null : key.GetValue("Position") as string;
                    if (v == "cursor") pos.FollowCursor = true;
                    else if (v != null)
                    {
                        string[] parts = v.Split(',');
                        double x, y;
                        if (parts.Length == 2
                            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                        {
                            pos.X = Math.Max(0, Math.Min(1, x));
                            pos.Y = Math.Max(0, Math.Min(1, y));
                        }
                    }
                }
            }
            catch { }
            return pos;
        }

        public void Save()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue("Position", FollowCursor ? "cursor"
                    : X.ToString("0.####", CultureInfo.InvariantCulture) + "," + Y.ToString("0.####", CultureInfo.InvariantCulture));
        }
    }

    enum ClipKind { Text, Image, Files, Other, Info }

    class ClipInfo
    {
        public ClipKind Kind;
        public string Title = "已複製";
        public string Meta = "";
        public string Body = "";
        public Image Thumb;
        public string Signature;
    }

    // 接收 WM_CLIPBOARDUPDATE 的隱藏視窗
    class ClipboardWatcher : NativeWindow, IDisposable
    {
        public event EventHandler ClipboardChanged;

        public ClipboardWatcher()
        {
            CreateHandle(new CreateParams());
            Native.AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_CLIPBOARDUPDATE && ClipboardChanged != null)
                ClipboardChanged(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                Native.RemoveClipboardFormatListener(Handle);
                DestroyHandle();
            }
        }
    }

    class TrayContext : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "CopyNotify";

        readonly NotifyIcon tray;
        readonly ClipboardWatcher watcher;
        readonly Overlay overlay;
        readonly System.Windows.Forms.Timer debounce;
        readonly ToolStripMenuItem pauseItem, startupItem, cursorItem, customItem;
        readonly ToolStripMenuItem[] presetItems = new ToolStripMenuItem[9];
        readonly PositionSettings pos;
        string lastSignature;

        static readonly string[] PresetNames =
            { "左上", "上方中央", "右上", "左側中央", "正中央", "右側中央", "左下", "下方中央", "右下" };

        public TrayContext()
        {
            overlay = new Overlay();
            pos = PositionSettings.Load();

            var posMenu = new ToolStripMenuItem("提示位置");
            for (int i = 0; i < 9; i++)
            {
                double px = (i % 3) / 2.0, py = (i / 3) / 2.0;
                presetItems[i] = new ToolStripMenuItem(PresetNames[i]);
                presetItems[i].Click += delegate
                {
                    pos.FollowCursor = false; pos.X = px; pos.Y = py;
                    ApplyPosition();
                };
                posMenu.DropDownItems.Add(presetItems[i]);
            }
            posMenu.DropDownItems.Add(new ToolStripSeparator());
            cursorItem = new ToolStripMenuItem("跟隨滑鼠游標");
            cursorItem.Click += delegate { pos.FollowCursor = true; ApplyPosition(); };
            posMenu.DropDownItems.Add(cursorItem);
            customItem = new ToolStripMenuItem("自訂位置（拖曳調整）…");
            customItem.Click += delegate
            {
                overlay.BeginPlacement(Screen.FromPoint(Cursor.Position), pos, delegate(bool saved)
                {
                    UpdatePositionChecks();
                    if (saved) ShowPositionPreview();
                });
            };
            posMenu.DropDownItems.Add(customItem);
            UpdatePositionChecks();

            pauseItem = new ToolStripMenuItem("暫停提示");
            pauseItem.CheckOnClick = true;
            startupItem = new ToolStripMenuItem("開機時自動啟動");
            startupItem.Checked = IsStartupEnabled();
            startupItem.Click += delegate { SetStartup(!startupItem.Checked); startupItem.Checked = IsStartupEnabled(); };
            var exitItem = new ToolStripMenuItem("結束");
            exitItem.Click += delegate { ExitThread(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(pauseItem);
            menu.Items.Add(posMenu);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            tray = new NotifyIcon();
            tray.Icon = MakeTrayIcon();
            tray.Text = "複製提示 CopyNotify";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            // 有些程式一次複製會觸發多次更新，稍等一下再讀取
            debounce = new System.Windows.Forms.Timer();
            debounce.Interval = 80;
            debounce.Tick += delegate { debounce.Stop(); OnClipboardSettled(); };

            watcher = new ClipboardWatcher();
            watcher.ClipboardChanged += delegate
            {
                if (pauseItem.Checked || overlay.IsPlacing) return;
                debounce.Stop();
                debounce.Start();
            };

            var hello = new ClipInfo();
            hello.Kind = ClipKind.Info;
            hello.Title = "複製提示已啟動";
            hello.Body = "按 Ctrl+C 成功複製時會在這裡顯示內容。\n右下角系統匣圖示可調整位置、暫停或結束。";
            overlay.ShowInfo(hello, Screen.PrimaryScreen, pos);
        }

        void ApplyPosition()
        {
            overlay.CancelPlacement();
            pos.Save();
            UpdatePositionChecks();
            ShowPositionPreview();
        }

        void UpdatePositionChecks()
        {
            bool anyPreset = false;
            for (int i = 0; i < 9; i++)
            {
                bool match = !pos.FollowCursor
                    && Math.Abs(pos.X - (i % 3) / 2.0) < 1e-4 && Math.Abs(pos.Y - (i / 3) / 2.0) < 1e-4;
                presetItems[i].Checked = match;
                anyPreset |= match;
            }
            cursorItem.Checked = pos.FollowCursor;
            customItem.Checked = !pos.FollowCursor && !anyPreset;
        }

        void ShowPositionPreview()
        {
            var info = new ClipInfo();
            info.Kind = ClipKind.Info;
            info.Title = "提示位置已設定";
            info.Body = "之後的複製提示會出現在這個位置。";
            overlay.ShowInfo(info, Screen.FromPoint(Cursor.Position), pos);
        }

        void OnClipboardSettled()
        {
            if (overlay.IsPlacing) return;
            ClipInfo info = ReadClipboard();
            if (info == null) return;
            if (info.Signature != null && info.Signature == lastSignature)
                info.Meta += (info.Meta.Length > 0 ? " · " : "") + "與上次相同";
            lastSignature = info.Signature;

            Screen screen;
            IntPtr fg = Native.GetForegroundWindow();
            screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.FromPoint(Cursor.Position);
            overlay.ShowInfo(info, screen, pos);
        }

        static ClipInfo ReadClipboard()
        {
            // 剪貼簿可能暫時被其他程式鎖住，重試幾次
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try { return ReadClipboardOnce(); }
                catch (ExternalException) { Thread.Sleep(50); }
                catch (Exception) { return null; }
            }
            return null;
        }

        static ClipInfo ReadClipboardOnce()
        {
            var info = new ClipInfo();
            if (Clipboard.ContainsFileDropList())
            {
                StringCollection files = Clipboard.GetFileDropList();
                info.Kind = ClipKind.Files;
                info.Meta = "檔案 · " + files.Count + " 個";
                var sb = new StringBuilder();
                var sig = new StringBuilder("F:");
                for (int i = 0; i < files.Count; i++)
                {
                    sig.Append(files[i]).Append('|');
                    if (i < 8)
                    {
                        string name = Path.GetFileName(files[i].TrimEnd('\\'));
                        sb.Append(name.Length > 0 ? name : files[i]).Append('\n');
                    }
                }
                if (files.Count > 8) sb.Append("…及其他 " + (files.Count - 8) + " 個");
                info.Body = sb.ToString().TrimEnd('\n');
                info.Signature = sig.ToString();
            }
            else if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                info.Kind = ClipKind.Text;
                info.Meta = "文字 · " + text.Length + " 字";
                info.Signature = "T:" + text;
                string preview = text.Length > 600 ? text.Substring(0, 600) : text;
                preview = preview.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Trim('\n');
                info.Body = preview.Trim().Length == 0 ? "（只有空白字元）" : preview;
            }
            else if (Clipboard.ContainsImage())
            {
                using (Image img = Clipboard.GetImage())
                {
                    if (img == null) return null;
                    info.Kind = ClipKind.Image;
                    info.Meta = "圖片 · " + img.Width + "×" + img.Height;
                    info.Thumb = MakeThumb(img, 360, 220);
                    info.Signature = "I:" + img.Width + "x" + img.Height;
                }
            }
            else
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return null;
                string[] formats = data.GetFormats();
                if (formats.Length == 0) return null; // 剪貼簿被清空，不提示
                info.Kind = ClipKind.Other;
                info.Meta = "其他格式";
                info.Body = string.Join(", ", formats, 0, Math.Min(formats.Length, 6));
            }
            return info;
        }

        static Image MakeThumb(Image src, int maxW, int maxH)
        {
            float s;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;
            double ratio = Math.Min(1.0, Math.Min(maxW * s / src.Width, maxH * s / src.Height));
            int w = Math.Max(1, (int)(src.Width * ratio)), h = Math.Max(1, (int)(src.Height * ratio));
            var bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            return bmp;
        }

        static Icon MakeTrayIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = Overlay.RoundRect(new RectangleF(2, 2, 28, 28), 7))
                    using (var bg = new SolidBrush(Color.FromArgb(46, 160, 67)))
                        g.FillPath(bg, path);
                    using (var pen = new Pen(Color.White, 4f))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        g.DrawLines(pen, new[] { new PointF(9, 16.5f), new PointF(14, 21.5f), new PointF(23, 11) });
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunName) != null;
        }

        static void SetStartup(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunName, false);
            }
        }

        protected override void ExitThreadCore()
        {
            watcher.Dispose();
            tray.Visible = false;
            tray.Dispose();
            overlay.Dispose();
            base.ExitThreadCore();
        }
    }

    // 置中、不搶焦點、滑鼠可穿透的提示視窗
    class Overlay : Form
    {
        const int HoldMs = 1400, FadeMs = 400;
        const double MaxOpacity = 0.96;
        const int MaxBodyLines = 8;
        const int EdgeMargin = 24;     // 與螢幕邊緣的距離
        const int CursorOffset = 18;   // 跟隨游標時與游標的距離

        static readonly Color BgColor = Color.FromArgb(32, 33, 36);
        static readonly Color BorderColor = Color.FromArgb(70, 72, 78);
        static readonly Color AccentColor = Color.FromArgb(46, 160, 67);
        static readonly Color TitleColor = Color.White;
        static readonly Color MetaColor = Color.FromArgb(160, 164, 172);
        static readonly Color BodyColor = Color.FromArgb(225, 227, 230);

        readonly System.Windows.Forms.Timer fadeTimer;
        readonly Font titleFont, metaFont, bodyFont;
        readonly float scale;
        DateTime shownAt;
        ClipInfo info;
        RectangleF badgeRect, titleRect, metaRect, bodyRect;

        // 拖曳調整位置模式
        bool placing, dragging;
        Point dragOffset;
        PositionSettings placementPos;
        Action<bool> placementDone;

        public bool IsPlacing { get { return placing; } }

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AllowTransparency = true;
            Opacity = MaxOpacity;
            BackColor = BgColor;
            DoubleBuffered = true;

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            titleFont = new Font("Microsoft JhengHei UI", 13f, FontStyle.Bold);
            metaFont = new Font("Microsoft JhengHei UI", 9.5f);
            bodyFont = new Font("Microsoft JhengHei UI", 11f);

            fadeTimer = new System.Windows.Forms.Timer();
            fadeTimer.Interval = 25;
            fadeTimer.Tick += OnFadeTick;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // TOOLWINDOW | NOACTIVATE | TOPMOST，平常再加 TRANSPARENT(滑鼠穿透)；拖曳模式時要能接收滑鼠
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8;
                if (!placing) cp.ExStyle |= 0x20;
                return cp;
            }
        }

        float S(float v) { return v * scale; }

        public void ShowInfo(ClipInfo newInfo, Screen screen, PositionSettings pos)
        {
            if (info != null && info.Thumb != null) info.Thumb.Dispose();
            info = newInfo;

            float pad = S(18), gap = S(10), badge = S(22);
            float maxBodyW = S(620), minW = S(300);

            using (Graphics g = CreateGraphics())
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                SizeF titleSize = g.MeasureString(info.Title, titleFont);
                SizeF metaSize = info.Meta.Length > 0 ? g.MeasureString(info.Meta, metaFont) : SizeF.Empty;
                float headerW = badge + S(8) + titleSize.Width + (metaSize.Width > 0 ? S(24) + metaSize.Width : 0);
                float headerH = Math.Max(badge, titleSize.Height);

                SizeF bodySize = SizeF.Empty;
                if (info.Thumb != null)
                    bodySize = new SizeF(info.Thumb.Width, info.Thumb.Height);
                else if (info.Body.Length > 0)
                {
                    using (StringFormat fmt = BodyFormat())
                        bodySize = g.MeasureString(info.Body, bodyFont, (int)maxBodyW, fmt);
                    float lineH = bodyFont.GetHeight(g);
                    bodySize.Height = Math.Min(bodySize.Height, lineH * MaxBodyLines);
                    bodySize.Width = Math.Min(bodySize.Width + S(2), maxBodyW);
                }

                float contentW = Math.Max(Math.Max(headerW, bodySize.Width), minW - pad * 2);
                float w = contentW + pad * 2;
                float h = pad + headerH + (bodySize.Height > 0 ? gap + bodySize.Height : 0) + pad;

                badgeRect = new RectangleF(pad, pad + (headerH - badge) / 2, badge, badge);
                titleRect = new RectangleF(badgeRect.Right + S(8), pad + (headerH - titleSize.Height) / 2, titleSize.Width + S(4), titleSize.Height);
                metaRect = new RectangleF(w - pad - metaSize.Width, pad + (headerH - metaSize.Height) / 2, metaSize.Width + S(2), metaSize.Height);
                bodyRect = new RectangleF(pad, pad + headerH + gap, bodySize.Width, bodySize.Height);

                int iw = (int)Math.Ceiling(w), ih = (int)Math.Ceiling(h);
                Point loc = pos.FollowCursor && !placing
                    ? NearCursor(iw, ih)
                    : Aligned(screen.WorkingArea, iw, ih, pos.X, pos.Y);
                Bounds = new Rectangle(loc, new Size(iw, ih));
            }

            using (GraphicsPath path = RoundRect(new RectangleF(0, 0, Width, Height), S(12)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }

            Opacity = MaxOpacity;
            shownAt = DateTime.Now;
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Invalidate();
            fadeTimer.Start();
        }

        Point Aligned(Rectangle area, int w, int h, double fx, double fy)
        {
            int m = (int)S(EdgeMargin);
            return new Point(
                area.Left + m + (int)Math.Round(fx * Math.Max(0, area.Width - 2 * m - w)),
                area.Top + m + (int)Math.Round(fy * Math.Max(0, area.Height - 2 * m - h)));
        }

        Point NearCursor(int w, int h)
        {
            Point c = Cursor.Position;
            Rectangle a = Screen.FromPoint(c).WorkingArea;
            int off = (int)S(CursorOffset);
            int x = c.X + off, y = c.Y + off;
            if (x + w > a.Right) x = c.X - off - w;   // 右邊放不下就放到游標左邊
            if (y + h > a.Bottom) y = c.Y - off - h;  // 下面放不下就放到游標上面
            x = Math.Max(a.Left, Math.Min(x, a.Right - w));
            y = Math.Max(a.Top, Math.Min(y, a.Bottom - h));
            return new Point(x, y);
        }

        static double Fraction(int offset, int range)
        {
            if (range <= 0) return 0.5;
            return Math.Max(0, Math.Min(1, (double)offset / range));
        }

        public void BeginPlacement(Screen screen, PositionSettings pos, Action<bool> done)
        {
            if (placing) return;
            placing = true;
            placementPos = pos;
            placementDone = done;

            var info = new ClipInfo();
            info.Kind = ClipKind.Info;
            info.Title = "拖曳調整提示位置";
            info.Body = "按住滑鼠左鍵把這個框拖到想要的位置。\n雙擊左鍵：儲存位置　　按右鍵：取消";
            ShowInfo(info, screen, pos);
            fadeTimer.Stop();
            UpdateStyles(); // 取消滑鼠穿透
            Cursor = Cursors.SizeAll;
        }

        public void CancelPlacement() { EndPlacement(false); }

        void EndPlacement(bool save)
        {
            if (!placing) return;
            if (save)
            {
                Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
                int m = (int)S(EdgeMargin);
                placementPos.FollowCursor = false;
                placementPos.X = Fraction(Left - area.Left - m, area.Width - 2 * m - Width);
                placementPos.Y = Fraction(Top - area.Top - m, area.Height - 2 * m - Height);
                placementPos.Save();
            }
            placing = dragging = false;
            Capture = false;
            Cursor = Cursors.Default;
            UpdateStyles(); // 恢復滑鼠穿透
            Hide();
            if (placementDone != null) placementDone(save);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!placing) return;
            if (e.Button == MouseButtons.Left) { dragging = true; dragOffset = e.Location; }
            else if (e.Button == MouseButtons.Right) EndPlacement(false);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point c = Cursor.Position;
                Location = new Point(c.X - dragOffset.X, c.Y - dragOffset.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (placing && e.Button == MouseButtons.Left) EndPlacement(true);
        }

        void OnFadeTick(object sender, EventArgs e)
        {
            double elapsed = (DateTime.Now - shownAt).TotalMilliseconds;
            if (elapsed < HoldMs) return;
            if (elapsed < HoldMs + FadeMs)
            {
                Opacity = Math.Max(0.01, MaxOpacity * (1 - (elapsed - HoldMs) / FadeMs));
                return;
            }
            fadeTimer.Stop();
            Hide();
        }

        static StringFormat BodyFormat()
        {
            var fmt = new StringFormat(StringFormatFlags.LineLimit);
            fmt.Trimming = StringTrimming.EllipsisCharacter;
            return fmt;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (info == null) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (GraphicsPath path = RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(12)))
            using (var pen = new Pen(BorderColor, 1f))
                g.DrawPath(pen, path);

            using (var accent = new SolidBrush(AccentColor))
                g.FillEllipse(accent, badgeRect);
            using (var pen = new Pen(Color.White, S(2.4f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float x = badgeRect.X, y = badgeRect.Y, b = badgeRect.Width;
                if (info.Kind == ClipKind.Info)
                {
                    g.DrawLine(pen, x + b * 0.5f, y + b * 0.45f, x + b * 0.5f, y + b * 0.72f);
                    g.DrawLine(pen, x + b * 0.5f, y + b * 0.28f, x + b * 0.5f, y + b * 0.29f);
                }
                else
                {
                    g.DrawLines(pen, new[] {
                        new PointF(x + b * 0.27f, y + b * 0.52f),
                        new PointF(x + b * 0.44f, y + b * 0.68f),
                        new PointF(x + b * 0.74f, y + b * 0.34f) });
                }
            }

            using (var brush = new SolidBrush(TitleColor))
                g.DrawString(info.Title, titleFont, brush, titleRect.Location);
            if (info.Meta.Length > 0)
                using (var brush = new SolidBrush(MetaColor))
                    g.DrawString(info.Meta, metaFont, brush, metaRect.Location);

            if (info.Thumb != null)
            {
                g.DrawImage(info.Thumb, bodyRect.X, bodyRect.Y, bodyRect.Width, bodyRect.Height);
            }
            else if (info.Body.Length > 0)
            {
                using (var brush = new SolidBrush(BodyColor))
                using (StringFormat fmt = BodyFormat())
                    g.DrawString(info.Body, bodyFont, brush, bodyRect, fmt);
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fadeTimer.Dispose();
                titleFont.Dispose();
                metaFont.Dispose();
                bodyFont.Dispose();
                if (info != null && info.Thumb != null) info.Thumb.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
