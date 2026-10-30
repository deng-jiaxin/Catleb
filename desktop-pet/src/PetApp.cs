using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CatlebDesktopPet
{
    public sealed class FrameSpec
    {
        public string file { get; set; }
        public int duration_ms { get; set; }
        public double? anchor_x { get; set; }
        public double? anchor_y { get; set; }
    }

    public sealed class ActionSpec
    {
        public string id { get; set; }
        public string name { get; set; }
        public double anchor_x { get; set; }
        public double anchor_y { get; set; }
        public double grab_x { get; set; }
        public double grab_y { get; set; }
        public List<FrameSpec> frames { get; set; }
    }

    public sealed class PetManifest
    {
        public string name { get; set; }
        public int canvas_width { get; set; }
        public int canvas_height { get; set; }
        public string default_action { get; set; }
        public List<ActionSpec> actions { get; set; }
    }

    internal sealed class LoadedAction : IDisposable
    {
        internal ActionSpec Spec;
        internal readonly List<Bitmap> Frames = new List<Bitmap>();
        internal int Duration { get { return Spec.frames.Sum(delegate(FrameSpec f) { return f.duration_ms; }); } }
        public void Dispose() { foreach (Bitmap b in Frames) b.Dispose(); Frames.Clear(); }
    }

    internal sealed class AssetSet : IDisposable
    {
        internal readonly PetManifest Manifest;
        internal readonly Dictionary<string, LoadedAction> Actions = new Dictionary<string, LoadedAction>(StringComparer.Ordinal);
        internal AssetSet(string directory)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string json = File.ReadAllText(Path.Combine(root, "pet.json"));
            Manifest = new JavaScriptSerializer().Deserialize<PetManifest>(json);
            if (Manifest == null || Manifest.canvas_width < 16 || Manifest.canvas_height < 16 ||
                Manifest.canvas_width > 1024 || Manifest.canvas_height > 1024 || Manifest.actions == null || Manifest.actions.Count == 0)
                throw new InvalidDataException("素材清单的画布或动作设置无效。");
            try
            {
                foreach (ActionSpec spec in Manifest.actions)
                {
                    if (String.IsNullOrEmpty(spec.id) || Actions.ContainsKey(spec.id) || spec.frames == null || spec.frames.Count == 0)
                        throw new InvalidDataException("素材清单含空动作或重复动作编号。");
                    LoadedAction action = new LoadedAction();
                    action.Spec = spec;
                    Actions.Add(spec.id, action);
                    foreach (FrameSpec frame in spec.frames)
                    {
                        if (frame.duration_ms < 10 || frame.duration_ms > 60000 || String.IsNullOrEmpty(frame.file))
                            throw new InvalidDataException("动作帧的时长或文件名无效：" + spec.id);
                        string path = Path.GetFullPath(Path.Combine(root, frame.file.Replace('/', Path.DirectorySeparatorChar)));
                        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("素材路径越出 assets 目录。");
                        using (Image image = Image.FromFile(path))
                        {
                            if (image.Width != Manifest.canvas_width || image.Height != Manifest.canvas_height)
                                throw new InvalidDataException("素材画布大小不一致：" + frame.file);
                            Bitmap copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
                            using (Graphics graphics = Graphics.FromImage(copy))
                            {
                                graphics.CompositingMode = CompositingMode.SourceCopy;
                                graphics.DrawImageUnscaled(image, 0, 0);
                            }
                            action.Frames.Add(copy);
                        }
                    }
                }
                if (String.IsNullOrEmpty(Manifest.default_action) || !Actions.ContainsKey(Manifest.default_action) || !Actions.ContainsKey("lift"))
                    throw new InvalidDataException("素材清单缺少默认动作或拎起动作。");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (LoadedAction action in Actions.Values) action.Dispose(); Actions.Clear(); }
    }

    internal sealed class PortableSettings
    {
        public PortableSettings() { }
        public double Scale = 1.0;
        public bool AutoActions = true;
        public bool Roam = false;
        public bool Topmost = true;
        public bool HasPosition = false;
        public int X;
        public int Y;
    }

    internal static class Native
    {
        internal const int WS_EX_LAYERED = 0x80000;
        internal const int WS_EX_TOOLWINDOW = 0x80;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WM_NCHITTEST = 0x84;
        internal const int WM_MOUSEACTIVATE = 0x21;
        internal const int WM_DISPLAYCHANGE = 0x7E;
        internal const int HTTRANSPARENT = -1;
        internal const int HTCLIENT = 1;
        internal const int MA_NOACTIVATE = 3;
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { internal int x, y; internal POINT(int a, int b) { x = a; y = b; } }
        [StructLayout(LayoutKind.Sequential)] internal struct SIZE { internal int cx, cy; internal SIZE(int x, int y) { cx = x; cy = y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BLENDFUNCTION { internal byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT source, int colorKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr item);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();

        internal static void ShowBitmap(IntPtr window, Bitmap bitmap, Point location)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            if (screen == IntPtr.Zero || memory == IntPtr.Zero)
            {
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建桌宠绘图上下文。");
            }
            IntPtr handle = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                handle = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(memory, handle);
                POINT destination = new POINT(location.X, location.Y), source = new POINT(0, 0);
                SIZE size = new SIZE(bitmap.Width, bitmap.Height);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;
                if (!UpdateLayeredWindow(window, screen, ref destination, ref size, memory, ref source, 0, ref blend, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "透明窗口更新失败。");
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (handle != IntPtr.Zero) DeleteObject(handle);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    internal sealed class PetWindow : Form
    {
        private enum PeekDirection { None, Left, Right, Bottom }
        private const int PeekEnterDistance = 32;
        private const int PeekExitDistance = 56;
        private const double PeekRevealRatio = 0.46;
        private const double PeekVisibleHeightRatio = 0.50;

        private readonly AssetSet assets;
        private readonly string settingsPath;
        private readonly PortableSettings settings;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Random random = new Random();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private NotifyIcon tray;
        private Icon trayIcon;
        private LoadedAction current;
        private Bitmap displayed;
        private int frameIndex;
        private double frameElapsed;
        private long previousTick;
        private long nextIdleAction;
        private bool activeAnimation;
        private bool loopAnimation;
        private bool paused;
        private bool pointerDown;
        private bool dragging;
        private bool petVisible = true;
        private bool closing;
        private bool roaming;
        private PeekDirection peekDirection = PeekDirection.None;
        private double roamMillis;
        private double roamRemainder;
        private Point mouseDownScreen;
        private PointF grabDifference;
        private readonly string smokeReport;
        private int layeredUpdates;
        private int visitedFrames;
        private bool smokeBehaviorRunning;
        private readonly List<string> smokeChecks = new List<string>();
        private ToolStripMenuItem pauseItem, autoItem, roamItem, topItem, hideItem;
        private readonly List<ToolStripMenuItem> scaleItems = new List<ToolStripMenuItem>();

        // HD assets use a 480px canvas while preserving the original 320px
        // on-screen contract and scale-menu semantics.
        private double AssetToDisplayScale
        {
            get { return assets.Manifest.canvas_width > 320 ? 320.0 / assets.Manifest.canvas_width : 1.0; }
        }

        internal PetWindow(AssetSet loaded, string config, string testReport)
        {
            assets = loaded;
            settingsPath = config;
            smokeReport = testReport;
            settings = LoadSettings(config);
            if (smokeReport != null) { settings.Scale = 0.5; settings.AutoActions = false; settings.Roam = false; }
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = settings.Topmost;
            Text = String.IsNullOrEmpty(assets.Manifest.name) ? "豹嗷喵" : assets.Manifest.name;
            SetStyle(ControlStyles.Selectable, false);
            KeyPreview = true;
            current = assets.Actions[assets.Manifest.default_action];
            UpdateCanvasSize();
            if (smokeReport != null) Location = new Point(-30000, -30000);
            else if (settings.HasPosition) { Location = new Point(settings.X, settings.Y); ClampToWorkArea(); }
            else ResetPosition();
            BuildMenu();
            if (smokeReport == null) BuildTray();
            timer.Interval = 15;
            timer.Tick += OnTick;
            MouseDown += OnPetMouseDown;
            MouseMove += OnPetMouseMove;
            MouseUp += OnPetMouseUp;
            MouseCaptureChanged += OnCaptureChanged;
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape && ContainsFocus) Close(); };
            Shown += delegate
            {
                RenderFrame();
                ScheduleIdleAction();
                previousTick = clock.ElapsedMilliseconds;
                if (smokeReport != null) PlayAction(assets.Manifest.default_action, true, false);
                timer.Start();
            };
            FormClosing += OnPetClosing;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return value;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.WM_MOUSEACTIVATE) { message.Result = new IntPtr(Native.MA_NOACTIVATE); return; }
            if (message.Msg == Native.WM_NCHITTEST)
            {
                long raw = message.LParam.ToInt64();
                Point screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
                Point point = PointToClient(screen);
                if (dragging && peekDirection != PeekDirection.None &&
                    point.X >= 0 && point.Y >= 0 && point.X < Width && point.Y < Height)
                {
                    message.Result = new IntPtr(Native.HTCLIENT);
                    return;
                }
                if (displayed != null && point.X >= 0 && point.Y >= 0 && point.X < displayed.Width && point.Y < displayed.Height)
                {
                    message.Result = new IntPtr(displayed.GetPixel(point.X, point.Y).A < 12 ? Native.HTTRANSPARENT : Native.HTCLIENT);
                    return;
                }
                message.Result = new IntPtr(Native.HTTRANSPARENT);
                return;
            }
            base.WndProc(ref message);
            if (message.Msg == Native.WM_DISPLAYCHANGE && smokeReport == null && !closing)
            {
                ClampToWorkArea();
                if (IsHandleCreated && petVisible) RenderFrame();
            }
        }

        private static PortableSettings LoadSettings(string path)
        {
            PortableSettings value = null;
            try { if (File.Exists(path)) value = new JavaScriptSerializer().Deserialize<PortableSettings>(File.ReadAllText(path)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (value == null) value = new PortableSettings();
            if (value.Scale < 0.75 || value.Scale > 2.0) value.Scale = 1.0;
            return value;
        }

        private void SaveSettings()
        {
            if (smokeReport != null) return;
            settings.X = Left;
            settings.Y = Top;
            settings.HasPosition = true;
            try { File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(settings), System.Text.Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void BuildTray()
        {
            try { trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { trayIcon = null; }
            if (trayIcon == null) trayIcon = (Icon)SystemIcons.Application.Clone();
            tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = Text + " · 右键管理，拖拽拎起";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ToggleVisibility(); };
            tray.Visible = true;
        }

        private void BuildMenu()
        {
            ToolStripMenuItem title = new ToolStripMenuItem(Text + " · 桌宠");
            title.Enabled = false;
            menu.Items.Add(title);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem actions = new ToolStripMenuItem("播放动作");
            foreach (LoadedAction action in assets.Actions.Values)
            {
                string id = action.Spec.id;
                ToolStripMenuItem item = new ToolStripMenuItem(action.Spec.name);
                item.Click += delegate { SetPaused(false); PlayAction(id, false, id == "patrol"); };
                actions.DropDownItems.Add(item);
            }
            menu.Items.Add(actions);
            menu.Items.Add("安静待机", null, delegate { GoIdle(); });
            pauseItem = new ToolStripMenuItem("暂停动画");
            pauseItem.CheckOnClick = true;
            pauseItem.CheckedChanged += delegate { SetPaused(pauseItem.Checked); };
            menu.Items.Add(pauseItem);
            autoItem = new ToolStripMenuItem("随机小动作");
            autoItem.CheckOnClick = true;
            autoItem.Checked = settings.AutoActions;
            autoItem.CheckedChanged += delegate { settings.AutoActions = autoItem.Checked; ScheduleIdleAction(); };
            menu.Items.Add(autoItem);
            roamItem = new ToolStripMenuItem("允许自主巡逻");
            roamItem.CheckOnClick = true;
            roamItem.Checked = settings.Roam;
            roamItem.CheckedChanged += delegate
            {
                settings.Roam = roamItem.Checked;
                if (!settings.Roam && roaming) GoIdle();
                ScheduleIdleAction();
            };
            menu.Items.Add(roamItem);
            topItem = new ToolStripMenuItem("总在最前");
            topItem.CheckOnClick = true;
            topItem.Checked = settings.Topmost;
            topItem.CheckedChanged += delegate { settings.Topmost = topItem.Checked; TopMost = settings.Topmost; };
            menu.Items.Add(topItem);
            ToolStripMenuItem size = new ToolStripMenuItem("显示大小");
            size.DropDownItems.Add(CreateScaleSlider());
            menu.Items.Add(size);
            menu.Items.Add(new ToolStripSeparator());
            hideItem = new ToolStripMenuItem("暂时隐藏");
            hideItem.Click += delegate { ToggleVisibility(); };
            menu.Items.Add(hideItem);
            menu.Items.Add("找回桌宠", null, delegate
            {
                if (!petVisible) ToggleVisibility();
                ResetPosition();
                RenderFrame();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出豹嗷喵", null, delegate { Close(); });
        }

        private ToolStripControlHost CreateScaleSlider()
        {
            TrackBar slider = new TrackBar();
            slider.Minimum = 75; slider.Maximum = 200; slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, (int)Math.Round(settings.Scale * 100)));
            slider.TickFrequency = 25; slider.SmallChange = 5; slider.LargeChange = 10;
            slider.Width = 180; slider.AutoSize = false; slider.Height = 35;
            ToolStripControlHost host = new ToolStripControlHost(slider);
            slider.Scroll += delegate { ApplyScale(slider.Value / 100.0); };
            return host;
        }

        private void AddScaleItem(ToolStripMenuItem parent, string label, double value)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Tag = value;
            item.Checked = Math.Abs(settings.Scale - value) < 0.001;
            item.Click += delegate { ApplyScale(value); };
            scaleItems.Add(item);
            parent.DropDownItems.Add(item);
        }

        private void ApplyScale(double value)
        {
            PointF anchor = WorldAnchor(current, frameIndex);
            settings.Scale = value;
            UpdateCanvasSize();
            SetLocationFromAnchor(anchor, current, frameIndex);
            ClampToWorkArea();
            foreach (ToolStripMenuItem scaleItem in scaleItems)
                scaleItem.Checked = Math.Abs((double)scaleItem.Tag - value) < 0.001;
            RenderFrame();
        }

        private void SetPaused(bool value)
        {
            paused = value;
            if (pauseItem != null && pauseItem.Checked != value) pauseItem.Checked = value;
            previousTick = clock.ElapsedMilliseconds;
            if (!value) ScheduleIdleAction();
        }

        private void ToggleVisibility()
        {
            petVisible = !petVisible;
            if (petVisible)
            {
                Show();
                ClampToWorkArea();
                RenderFrame();
                ScheduleIdleAction();
            }
            else { pointerDown = false; dragging = false; SetPeekDirection(PeekDirection.None); Capture = false; Hide(); }
            hideItem.Text = petVisible ? "暂时隐藏" : "显示豹嗷喵";
            previousTick = clock.ElapsedMilliseconds;
        }

        private void UpdateCanvasSize()
        {
            Size = new Size(Math.Max(32, (int)Math.Round(assets.Manifest.canvas_width * AssetToDisplayScale * settings.Scale)),
                Math.Max(32, (int)Math.Round(assets.Manifest.canvas_height * AssetToDisplayScale * settings.Scale)));
        }
        private static PointF FrameAnchor(LoadedAction action, int index)
        {
            FrameSpec spec = action.Spec.frames[Math.Max(0, Math.Min(index, action.Spec.frames.Count - 1))];
            return new PointF((float)(spec.anchor_x ?? action.Spec.anchor_x),
                (float)(spec.anchor_y ?? action.Spec.anchor_y));
        }
        private PointF WorldAnchor(LoadedAction action, int index)
        {
            PointF local = FrameAnchor(action, index);
            return new PointF((float)(Left + local.X * AssetToDisplayScale * settings.Scale),
                (float)(Top + local.Y * AssetToDisplayScale * settings.Scale));
        }
        private void SetLocationFromAnchor(PointF anchor, LoadedAction action, int index)
        {
            PointF local = FrameAnchor(action, index);
            Location = new Point((int)Math.Round(anchor.X - local.X * AssetToDisplayScale * settings.Scale),
                (int)Math.Round(anchor.Y - local.Y * AssetToDisplayScale * settings.Scale));
        }

        private void PlayAction(string id, bool loop, bool move)
        {
            LoadedAction next;
            if (!assets.Actions.TryGetValue(id, out next)) return;
            PointF anchor = WorldAnchor(current, frameIndex);
            current = next;
            frameIndex = 0;
            SetLocationFromAnchor(anchor, current, frameIndex);
            activeAnimation = true;
            loopAnimation = loop || move;
            frameIndex = 0;
            frameElapsed = 0;
            roaming = move;
            roamMillis = move ? 4500 : 0;
            roamRemainder = 0;
            previousTick = clock.ElapsedMilliseconds;
            timer.Interval = 15;
            if (smokeReport == null && !dragging) ClampToWorkArea();
            RenderFrame();
        }

        private void GoIdle()
        {
            PointF anchor = WorldAnchor(current, frameIndex);
            current = assets.Actions[assets.Manifest.default_action];
            frameIndex = 0;
            SetLocationFromAnchor(anchor, current, frameIndex);
            activeAnimation = false;
            loopAnimation = false;
            roaming = false;
            frameIndex = 0;
            frameElapsed = 0;
            timer.Interval = 100;
            if (smokeReport == null) ClampToWorkArea();
            ScheduleIdleAction();
            RenderFrame();
        }

        private void ScheduleIdleAction() { nextIdleAction = clock.ElapsedMilliseconds + random.Next(8000, 15001); }

        private void OnTick(object sender, EventArgs args)
        {
            if (closing) return;
            long now = clock.ElapsedMilliseconds;
            double elapsed = Math.Max(0, now - previousTick);
            previousTick = now;
            if (smokeReport != null && !smokeBehaviorRunning && now > 1200)
            {
                try
                {
                    RunOffscreenBehaviorChecks();
                    WriteSmokeReport(true, null);
                }
                catch (Exception error) { WriteSmokeReport(false, error.ToString()); }
                Close();
                return;
            }
            if (!petVisible || paused) { timer.Interval = 100; return; }
            timer.Interval = activeAnimation || pointerDown ? 15 : 100;
            if (roaming && !dragging)
            {
                roamMillis -= elapsed;
                roamRemainder += Math.Min(elapsed, 200) * 0.026;
                int pixels = (int)roamRemainder;
                roamRemainder -= pixels;
                Rectangle work = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)).WorkingArea;
                int right = Math.Max(work.Left, work.Right - Width);
                Left = Math.Min(right, Left + pixels);
                if (Left >= right || roamMillis <= 0) { GoIdle(); return; }
            }
            if (activeAnimation)
            {
                frameElapsed += elapsed;
                bool changed = false;
                while (activeAnimation && frameElapsed >= current.Spec.frames[frameIndex].duration_ms)
                {
                    frameElapsed -= current.Spec.frames[frameIndex].duration_ms;
                    frameIndex++;
                    visitedFrames++;
                    changed = true;
                    if (frameIndex == current.Frames.Count)
                    {
                        if (loopAnimation) frameIndex = 0;
                        else { GoIdle(); changed = false; break; }
                    }
                }
                if (changed) RenderFrame();
            }
            else if (!pointerDown && now >= nextIdleAction)
            {
                ScheduleIdleAction();
                List<string> choices = new List<string>();
                if (settings.AutoActions)
                {
                    foreach (string id in new string[] { "greeting", "guitar", "apple", "sweeping", "cool" })
                        if (assets.Actions.ContainsKey(id)) choices.Add(id);
                }
                if (settings.Roam && assets.Actions.ContainsKey("patrol"))
                {
                    Rectangle work = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)).WorkingArea;
                    if (work.Right - Right > 40) choices.Add("patrol");
                }
                if (choices.Count > 0)
                {
                    string id = choices[random.Next(choices.Count)];
                    PlayAction(id, false, id == "patrol" && settings.Roam);
                }
            }
        }

        private void OnPetMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            pointerDown = true;
            dragging = false;
            mouseDownScreen = System.Windows.Forms.Cursor.Position;
            grabDifference = new PointF((float)(e.X - current.Spec.grab_x * AssetToDisplayScale * settings.Scale),
                (float)(e.Y - current.Spec.grab_y * AssetToDisplayScale * settings.Scale));
            Capture = true;
            roaming = false;
            Cursor = Cursors.Hand;
        }

        private void OnPetMouseMove(object sender, MouseEventArgs e)
        {
            if (!pointerDown) return;
            Point mouse = System.Windows.Forms.Cursor.Position;
            if (!dragging && Math.Abs(mouse.X - mouseDownScreen.X) + Math.Abs(mouse.Y - mouseDownScreen.Y) >= 5)
            {
                dragging = true;
                PlayAction("lift", true, false);
            }
            if (dragging)
            {
                Rectangle work = Screen.FromPoint(mouse).WorkingArea;
                Point desired = new Point(
                    (int)Math.Round(mouse.X - current.Spec.grab_x * AssetToDisplayScale * settings.Scale - grabDifference.X),
                    (int)Math.Round(mouse.Y - current.Spec.grab_y * AssetToDisplayScale * settings.Scale - grabDifference.Y));
                PeekDirection previous = peekDirection;
                SetPeekDirection(ChoosePeekDirection(mouse, desired, work));
                Location = PeekLocation(desired, work, peekDirection);
                if (previous != peekDirection) RenderFrame();
            }
        }

        private PeekDirection ChoosePeekDirection(Point mouse, Point desired, Rectangle work)
        {
            Rectangle candidate = new Rectangle(desired, Size);
            int threshold = peekDirection == PeekDirection.None ? PeekEnterDistance : PeekExitDistance;
            bool nearLeft = mouse.X <= work.Left + threshold || candidate.Left <= work.Left + threshold;
            bool nearRight = mouse.X >= work.Right - threshold || candidate.Right >= work.Right - threshold;
            bool nearBottom = mouse.Y >= work.Bottom - threshold || candidate.Bottom >= work.Bottom - threshold;
            if (peekDirection == PeekDirection.Left && nearLeft) return PeekDirection.Left;
            if (peekDirection == PeekDirection.Right && nearRight) return PeekDirection.Right;
            if (peekDirection == PeekDirection.Bottom && nearBottom) return PeekDirection.Bottom;
            if (mouse.Y <= work.Top + PeekEnterDistance || candidate.Top <= work.Top + PeekEnterDistance)
                return PeekDirection.None;
            int leftDistance = Math.Abs(mouse.X - work.Left);
            int rightDistance = Math.Abs(work.Right - mouse.X);
            int bottomDistance = Math.Abs(work.Bottom - mouse.Y);
            if (nearBottom && bottomDistance <= Math.Min(leftDistance, rightDistance)) return PeekDirection.Bottom;
            if (nearLeft && leftDistance <= rightDistance) return PeekDirection.Left;
            if (nearRight) return PeekDirection.Right;
            return PeekDirection.None;
        }

        private Point PeekLocation(Point desired, Rectangle work, PeekDirection direction)
        {
            if (direction == PeekDirection.Left)
                return new Point(work.Left - Width + Math.Max(1, (int)Math.Round(Width * PeekRevealRatio)),
                    Math.Max(work.Top, Math.Min(desired.Y, work.Bottom - Height)));
            if (direction == PeekDirection.Right)
                return new Point(work.Right - Math.Max(1, (int)Math.Round(Width * PeekRevealRatio)),
                    Math.Max(work.Top, Math.Min(desired.Y, work.Bottom - Height)));
            if (direction == PeekDirection.Bottom)
                return new Point(Math.Max(work.Left, Math.Min(desired.X, work.Right - Width)),
                    work.Bottom - Math.Max(1, (int)Math.Round(Height * PeekVisibleHeightRatio)));
            return desired;
        }

        private void SetPeekDirection(PeekDirection value)
        {
            peekDirection = value;
        }

        private void OnPetMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                menu.Show(System.Windows.Forms.Cursor.Position);
                return;
            }
            if (e.Button != MouseButtons.Left || !pointerDown) return;
            bool wasDragging = dragging;
            pointerDown = false;
            dragging = false;
            Capture = false;
            Cursor = Cursors.Default;
            if (wasDragging) { SetPeekDirection(PeekDirection.None); GoIdle(); }
            else
            {
                List<string> choices = new List<string>();
                foreach (string id in new string[] { "greeting", "sweeping", "apple", "guitar", "cool", "patrol" })
                    if (assets.Actions.ContainsKey(id)) choices.Add(id);
                if (choices.Count == 0) GoIdle();
                else { string id = choices[random.Next(choices.Count)]; PlayAction(id, false, id == "patrol"); }
            }
        }

        private void OnCaptureChanged(object sender, EventArgs args)
        {
            if (!Capture && pointerDown)
            {
                pointerDown = false;
                bool wasDragging = dragging;
                dragging = false;
                Cursor = Cursors.Default;
                if (wasDragging && !closing) { SetPeekDirection(PeekDirection.None); GoIdle(); }
            }
        }

        private void ResetPosition()
        {
            Rectangle work = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            Location = new Point(work.Right - Width - 24, work.Bottom - Height - 12);
            ClampToWorkArea();
        }

        private void ClampToWorkArea()
        {
            if (smokeReport != null) return;
            Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
            Location = ClampedLocation(Bounds, work);
        }

        private static Point ClampedLocation(Rectangle bounds, Rectangle work)
        {
            int x = Math.Max(work.Left, Math.Min(bounds.Left, Math.Max(work.Left, work.Right - bounds.Width)));
            int y = Math.Max(work.Top, Math.Min(bounds.Top, Math.Max(work.Top, work.Bottom - bounds.Height)));
            return new Point(x, y);
        }

        private void RenderFrame()
        {
            if (closing || !IsHandleCreated || !petVisible) return;
            Bitmap surface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(surface))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (peekDirection != PeekDirection.None)
                    graphics.SetClip(new Rectangle(0, 0, Width,
                        Math.Max(1, (int)Math.Round(Height * PeekVisibleHeightRatio))));
                if (peekDirection == PeekDirection.Right)
                {
                    graphics.TranslateTransform(Width, 0);
                    graphics.ScaleTransform(-1, 1);
                }
                graphics.DrawImage(current.Frames[frameIndex], new Rectangle(0, 0, Width, Height));
            }
            try { Native.ShowBitmap(Handle, surface, Location); layeredUpdates++; }
            catch
            {
                surface.Dispose();
                throw;
            }
            Bitmap old = displayed;
            displayed = surface;
            if (old != null) old.Dispose();
        }

        private void WriteSmokeReport(bool ok, string error)
        {
            if (smokeReport == null) return;
            Program.WriteJson(smokeReport, new
            {
                ok = ok,
                test = "offscreen_layered_window",
                layered_window_updates = layeredUpdates,
                visited_animation_frames = visitedFrames,
                elapsed_ms = clock.ElapsedMilliseconds,
                no_taskbar = !ShowInTaskbar,
                topmost = TopMost,
                offscreen = Left < -10000 && Top < -10000,
                passed_checks = smokeChecks.ToArray(),
                error = error
            });
        }

        private void SmokeAssert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Offscreen behavior check failed: " + name);
            smokeChecks.Add(name);
        }

        private static bool SamePoint(PointF left, PointF right, double tolerance)
        {
            return Math.Abs(left.X - right.X) <= tolerance && Math.Abs(left.Y - right.Y) <= tolerance;
        }

        private void RunOffscreenBehaviorChecks()
        {
            // These assertions exercise only this window's own state and native
            // offscreen bitmap updates. They never send/capture user input, open
            // menus, query/change Cursor.Position, or persist portable settings.
            smokeBehaviorRunning = true;
            double initialScale = settings.Scale;
            try
            {
                int beforeUpdates = layeredUpdates;
                int checkedStates = 0;
                foreach (ActionSpec action in assets.Manifest.actions)
                {
                    PlayAction(action.id, false, false);
                    if (current.Spec.id != action.id || frameIndex != 0 || !activeAnimation)
                        throw new InvalidOperationException("Action state did not enter correctly: " + action.id);
                    checkedStates++;
                }
                SmokeAssert(checkedStates == assets.Manifest.actions.Count && checkedStates == 7 &&
                    layeredUpdates >= beforeUpdates + checkedStates, "all_seven_action_states_render");

                LoadedAction translated = null;
                int translatedIndex = 0;
                double greatestOffset = -1;
                foreach (LoadedAction action in assets.Actions.Values)
                {
                    for (int i = 0; i < action.Spec.frames.Count; i++)
                    {
                        FrameSpec spec = action.Spec.frames[i];
                        if (!spec.anchor_x.HasValue && !spec.anchor_y.HasValue) continue;
                        double offset = Math.Abs((spec.anchor_x ?? action.Spec.anchor_x) - action.Spec.anchor_x) +
                            Math.Abs((spec.anchor_y ?? action.Spec.anchor_y) - action.Spec.anchor_y);
                        if (offset > greatestOffset) { greatestOffset = offset; translated = action; translatedIndex = i; }
                    }
                }
                SmokeAssert(translated != null, "variable_frame_anchor_available");
                PlayAction(translated.Spec.id, false, false);
                frameIndex = translatedIndex;
                RenderFrame();
                FrameSpec translatedSpec = translated.Spec.frames[translatedIndex];
                PointF explicitAnchor = new PointF(
                    (float)(Left + (translatedSpec.anchor_x ?? translated.Spec.anchor_x) * AssetToDisplayScale * settings.Scale),
                    (float)(Top + (translatedSpec.anchor_y ?? translated.Spec.anchor_y) * AssetToDisplayScale * settings.Scale));
                PointF oldAnchor = WorldAnchor(current, frameIndex);
                SmokeAssert(SamePoint(oldAnchor, explicitAnchor, 0.01), "current_frame_anchor_used");
                PlayAction(assets.Manifest.default_action, false, false);
                SmokeAssert(SamePoint(oldAnchor, WorldAnchor(current, frameIndex), 1.0),
                    "variable_anchor_action_transition_preserves_world_point");

                PlayAction(assets.Manifest.default_action, true, false);
                frameIndex = Math.Min(1, current.Frames.Count - 1);
                frameElapsed = 12;
                int frozenIndex = frameIndex;
                double frozenElapsed = frameElapsed;
                SetPaused(true);
                previousTick = clock.ElapsedMilliseconds - 1000;
                OnTick(this, EventArgs.Empty);
                SmokeAssert(frameIndex == frozenIndex && frameElapsed == frozenElapsed,
                    "pause_freezes_animation_state");
                SetPaused(false);

                PlayAction(translated.Spec.id, false, false);
                frameIndex = translatedIndex;
                RenderFrame();
                PointF preScale = WorldAnchor(current, frameIndex);
                ApplyScale(1.25);
                SmokeAssert(SamePoint(preScale, WorldAnchor(current, frameIndex), 1.0),
                    "scale_change_preserves_current_frame_anchor");
                SmokeAssert(Width == (int)Math.Round(assets.Manifest.canvas_width * AssetToDisplayScale * 1.25) &&
                    Height == (int)Math.Round(assets.Manifest.canvas_height * AssetToDisplayScale * 1.25), "scale_updates_canvas");
                ApplyScale(initialScale);

                pointerDown = true;
                dragging = true;
                PlayAction("lift", true, false);
                SmokeAssert(current.Spec.id == "lift" && activeAnimation && loopAnimation && dragging,
                    "drag_state_enters_lift_without_input");
                pointerDown = false;
                dragging = false;
                GoIdle();
                SmokeAssert(current.Spec.id == assets.Manifest.default_action && !activeAnimation && frameIndex == 0,
                    "drag_release_state_returns_idle_without_input");

                Point negative = ClampedLocation(new Rectangle(-2100, 1100, 320, 320), new Rectangle(-1920, 0, 1920, 1040));
                SmokeAssert(negative == new Point(-1920, 720), "negative_monitor_workarea_clamp");
                Point oversized = ClampedLocation(new Rectangle(500, 500, 2500, 1500), new Rectangle(0, 0, 1920, 1040));
                SmokeAssert(oversized == Point.Empty, "oversized_window_workarea_clamp");
                SmokeAssert(Left < -10000 && Top < -10000 && !ShowInTaskbar && tray == null,
                    "behavior_checks_remain_offscreen_without_tray");
            }
            finally
            {
                smokeBehaviorRunning = false;
                pointerDown = false;
                dragging = false;
                paused = false;
            }
        }

        private void OnPetClosing(object sender, FormClosingEventArgs e)
        {
            Cleanup();
        }

        private void Cleanup()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            timer.Dispose();
            SaveSettings();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (trayIcon != null) { trayIcon.Dispose(); trayIcon = null; }
            menu.Dispose();
            if (displayed != null) { displayed.Dispose(); displayed = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cleanup();
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        private static string Argument(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(args[i + 1]);
            return null;
        }

        internal static void WriteJson(string path, object value)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(value), System.Text.Encoding.UTF8);
        }

        private static void SelfTest(AssetSet assets, string report)
        {
            List<object> details = new List<object>();
            int totalFrames = 0;
            int transparentFrames = 0;
            int semitransparentFrames = 0;
            string folder = Path.Combine(Path.GetDirectoryName(report), "self-test-previews");
            Directory.CreateDirectory(folder);
            Bitmap contact = new Bitmap(assets.Manifest.canvas_width * 3,
                (assets.Manifest.canvas_height + 30) * assets.Actions.Count, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(contact))
            using (Font label = new Font("Microsoft YaHei UI", 12))
            {
                graphics.Clear(Color.FromArgb(236, 237, 240));
                int row = 0;
                foreach (LoadedAction action in assets.Actions.Values)
                {
                    int[] indices = new int[] { 0, action.Frames.Count / 2, action.Frames.Count - 1 };
                    for (int col = 0; col < indices.Length; col++)
                    {
                        int x = col * assets.Manifest.canvas_width;
                        int y = row * (assets.Manifest.canvas_height + 30);
                        graphics.DrawString(action.Spec.name + " #" + indices[col].ToString(), label, Brushes.Black, x + 8, y + 4);
                        graphics.DrawImageUnscaled(action.Frames[indices[col]], x, y + 30);
                    }
                    foreach (Bitmap bitmap in action.Frames)
                    {
                        bool transparent = false, fractional = false;
                        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            byte[] bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
                            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                            for (int offset = 3; offset < bytes.Length; offset += 4)
                            {
                                byte alpha = bytes[offset];
                                if (alpha == 0) transparent = true;
                                else if (alpha < 255) fractional = true;
                                if (transparent && fractional) break;
                            }
                        }
                        finally { bitmap.UnlockBits(data); }
                        if (transparent) transparentFrames++;
                        if (fractional) semitransparentFrames++;
                    }
                    details.Add(new { id = action.Spec.id, name = action.Spec.name,
                        frames = action.Frames.Count, duration_ms = action.Duration,
                        durations_ms = action.Spec.frames.Select(delegate(FrameSpec f) { return f.duration_ms; }).ToArray(),
                        frame_anchors = action.Spec.frames.Select(delegate(FrameSpec f) { return new double?[] { f.anchor_x, f.anchor_y }; }).ToArray(),
                        anchor_x = action.Spec.anchor_x, anchor_y = action.Spec.anchor_y,
                        grab_x = action.Spec.grab_x, grab_y = action.Spec.grab_y });
                    totalFrames += action.Frames.Count;
                    row++;
                }
            }
            string preview = Path.Combine(folder, "actions-contact.png");
            contact.Save(preview, ImageFormat.Png);
            contact.Dispose();
            WriteJson(report, new { ok = transparentFrames == totalFrames, test = "assets_and_png_rendering",
                name = assets.Manifest.name, canvas = new int[] { assets.Manifest.canvas_width, assets.Manifest.canvas_height },
                actions = details, total_frames = totalFrames, transparent_frames = transparentFrames,
                fractional_alpha_frames = semitransparentFrames, contact_sheet = preview });
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string selfTest = Argument(args, "--self-test");
            string smokeTest = Argument(args, "--smoke-test");
            string assetsPath = Argument(args, "--assets") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
            Mutex mutex = null;
            bool ownsMutex = false;
            try
            {
                Native.SetProcessDPIAware();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (selfTest == null && smokeTest == null)
                {
                    mutex = new Mutex(true, "Local\\CatlebDesktopPet_LeopardMeow_v1", out ownsMutex);
                    if (!ownsMutex)
                    {
                        MessageBox.Show("豹嗷喵已经在运行。请从系统托盘右键菜单选择“找回桌宠”。", "豹嗷喵", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 0;
                    }
                }
                using (AssetSet assets = new AssetSet(assetsPath))
                {
                    if (selfTest != null) { SelfTest(assets, selfTest); return 0; }
                    using (PetWindow pet = new PetWindow(assets,
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"), smokeTest))
                    {
                        Application.Run(pet);
                    }
                }
                return 0;
            }
            catch (Exception error)
            {
                string report = selfTest ?? smokeTest;
                if (report != null)
                {
                    try { WriteJson(report, new { ok = false, error = error.ToString() }); }
                    catch (Exception) { }
                }
                else MessageBox.Show("豹嗷喵暂时无法启动：\n" + error.Message +
                    "\n\n请保持 exe 与 assets 文件夹放在一起。", "豹嗷喵", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                if (mutex != null)
                {
                    if (ownsMutex) mutex.ReleaseMutex();
                    mutex.Dispose();
                }
            }
        }
    }
}
