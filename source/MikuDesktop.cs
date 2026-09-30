using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MikuDesktop
{
    // Input timestamps and cursor coordinates only; no text conversion, logging or networking.
    internal sealed class Behavior
    {
        internal const int Idle = 0, Right = 1, Left = 2, Jump = 4, Focus = 7, Look = 9;
        internal static readonly int[][] Times = {
            // Original idle column 0 is excluded; its hold time moves to column 1.
            new int[]{3110,110,140,140,1800},
            new int[]{120,120,120,120,120,120,120,220},
            new int[]{120,120,120,120,120,120,120,220},
            new int[]{140,140,140,280}, new int[]{140,140,140,140,280},
            new int[]{140,140,140,140,140,140,140,240},
            new int[]{150,150,150,150,150,260},
            new int[]{120,120,120,120,120,220},
            new int[]{150,150,150,150,150,280}
        };
        internal int TypingMs = 1500, GazeMs = 1000;
        internal long LastKey = -100000, LastMotion = -100000;
        internal int State = Idle, Frame = 0, Direction = 0, RunDirection = Right;
        internal long Due = 0, LastDragMotion = -100000;
        internal bool Hover, Dragging, HasDirection;

        internal void Key(long now, int key, bool up)
        {
            if (!up && key != 255 && key != 16 && key != 17 && key != 18 &&
                key != 91 && key != 92 && (key < 160 || key > 165))
            {
                LastKey = now;
            }
        }
        internal void Motion(long now, double dx, double dy)
        {
            LastMotion = now;
            if (dx * dx + dy * dy < 100) return;
            double degrees = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
            double old = Direction * 22.5;
            double distance = Math.Abs(degrees - old);
            distance = Math.Min(distance, 360 - distance);
            // Five-degree hysteresis beyond the sector boundary.
            if (!HasDirection || distance > 16.25)
            {
                Direction = ((int)Math.Floor((degrees + 11.25) / 22.5)) % 16;
                HasDirection = true;
            }
        }
        internal void DragMotion(long now, int dx)
        {
            if (Math.Abs(dx) >= 2) { RunDirection = dx > 0 ? Right : Left; LastDragMotion = now; }
        }
        internal void Reset(long now)
        {
            LastKey = LastMotion = LastDragMotion = -100000;
            Hover = Dragging = HasDirection = false;
            State = Idle; Frame = 0; Due = now + Times[Idle][0];
        }
        internal int Desired(long now)
        {
            if (Dragging) return now - LastDragMotion <= 160 ? RunDirection : Idle;
            if (Hover) return Jump;
            if (now - LastKey < TypingMs) return Focus;
            if (HasDirection && now - LastMotion < GazeMs) return Look;
            return Idle;
        }
        internal bool Step(long now)
        {
            int next = Desired(now);
            // Only jumping completes its full cycle; direct dragging can still interrupt it.
            if (next != State && State == Jump && !Dragging &&
                (Frame < Times[State].Length - 1 || now < Due)) next = State;
            if (next != State)
            {
                State = next; Frame = next == Look ? Direction : 0;
                Due = next == Look ? long.MaxValue : now + Times[next][0];
                return true;
            }
            if (State == Look)
            {
                if (Frame == Direction) return false;
                Frame = Direction; return true;
            }
            if (now < Due) return false;
            Frame = (Frame + 1) % Times[State].Length;
            Due = now + Times[State][Frame];
            return true;
        }
        internal int NextDelay(long now)
        {
            long due = Due;
            if (!Dragging && !Hover && State != Jump)
            {
                if (LastKey + TypingMs > now) due = Math.Min(due, LastKey + TypingMs);
                if (LastMotion + GazeMs > now) due = Math.Min(due, LastMotion + GazeMs);
            }
            if (Dragging && LastDragMotion + 161 > now) due = Math.Min(due, LastDragMotion + 161);
            return (int)Math.Max(15, Math.Min(1000, due - now));
        }
    }

    internal sealed class Settings
    {
        internal int X = int.MinValue, Y = int.MinValue, Scale = 100, Typing = 1500, Gaze = 1000;
        internal bool OnTop = true;
        internal readonly string PathName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MikuDesktop.settings.ini");
        internal void Load()
        {
            try
            {
                foreach (string line in File.ReadAllLines(PathName))
                {
                    string[] parts = line.Split('='); int value;
                    if (parts.Length != 2 || !int.TryParse(parts[1], out value)) continue;
                    switch (parts[0])
                    {
                        case "x": X = value; break; case "y": Y = value; break;
                        case "size": Scale = Math.Max(50, Math.Min(200, value)); break;
                        case "typing_ms": Typing = Math.Max(500, Math.Min(5000, value)); break;
                        case "gaze_ms": Gaze = Math.Max(300, Math.Min(5000, value)); break;
                        case "topmost": OnTop = value != 0; break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        internal bool Save()
        {
            try
            {
                string text = string.Format(CultureInfo.InvariantCulture,
                    "x={0}\r\ny={1}\r\nsize={2}\r\ntyping_ms={3}\r\ngaze_ms={4}\r\ntopmost={5}\r\n",
                    X, Y, Scale, Typing, Gaze, OnTop ? 1 : 0);
                string temporary = PathName + ".tmp";
                File.WriteAllText(temporary, text, Encoding.UTF8);
                if (File.Exists(PathName)) File.Replace(temporary, PathName, null);
                else File.Move(temporary, PathName);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal sealed class Atlas : IDisposable
    {
        internal const int W = 192, H = 208, FootBottom = 202;
        internal sealed class Tile { internal IntPtr Bitmap; internal byte[] Alpha; internal int TopPixel, LeftPixel, RightPixel; }
        private sealed class Source { internal byte[] Pixels, Alpha; }
        private static readonly Dictionary<int, Source> Sources = LoadSources();
        internal readonly Dictionary<int, Tile> Tiles = new Dictionary<int, Tile>();
        internal readonly byte[] HoverMask = new byte[W * H];
        internal IntPtr DC;
        private IntPtr original;
        internal int Width, Height;
        internal int TopInset, LeftInset, RightInset;
        internal Atlas(int width, int height)
        {
            Width = width; Height = height; TopInset = height; LeftInset = RightInset = width;
            DC = Native.CreateCompatibleDC(IntPtr.Zero);
            if (DC == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                int[] sampleX = new int[width];
                for (int px = 0; px < width; px++) sampleX[px] = px * W / width;
                byte[] bytes = new byte[width * height * 4];
                foreach (KeyValuePair<int, Source> item in Sources)
                {
                    Source source = item.Value;
                    if (item.Key < 8 || item.Key >= 32 && item.Key < 40)
                        for (int p = 0; p < source.Alpha.Length; p++)
                            HoverMask[p] = Math.Max(HoverMask[p], source.Alpha[p]);
                    Native.BITMAPINFO info = new Native.BITMAPINFO();
                    info.size = 40; info.width = width; info.height = -height;
                    info.planes = 1; info.bits = 32;
                    IntPtr pixels;
                    IntPtr dib = Native.CreateDIBSection(DC, ref info, 0, out pixels, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                    Tile tile = new Tile { Bitmap = dib, Alpha = source.Alpha, TopPixel = height, LeftPixel = width };
                    Tiles.Add(item.Key, tile);
                    // Scale and premultiply once. Playback only selects a cached native bitmap.
                    for (int py = 0; py < height; py++)
                    {
                        int sourceRow = (py * H / height) * W;
                        for (int px = 0; px < width; px++)
                        {
                            int from = (sourceRow + sampleX[px]) * 4, to = (py * width + px) * 4;
                            int alpha = source.Pixels[from + 3];
                            if (alpha > 0)
                            {
                                tile.TopPixel = Math.Min(tile.TopPixel, py);
                                tile.LeftPixel = Math.Min(tile.LeftPixel, px);
                                tile.RightPixel = Math.Max(tile.RightPixel, px + 1);
                            }
                            bytes[to] = (byte)((source.Pixels[from] * alpha + 127) / 255);
                            bytes[to + 1] = (byte)((source.Pixels[from + 1] * alpha + 127) / 255);
                            bytes[to + 2] = (byte)((source.Pixels[from + 2] * alpha + 127) / 255);
                            bytes[to + 3] = (byte)alpha;
                        }
                    }
                    Marshal.Copy(bytes, 0, pixels, bytes.Length);
                    TopInset = Math.Min(TopInset, tile.TopPixel);
                    LeftInset = Math.Min(LeftInset, tile.LeftPixel);
                    RightInset = Math.Min(RightInset, width - tile.RightPixel);
                }
                original = Native.SelectObject(DC, Tiles[1].Bitmap);
            }
            catch { Dispose(); throw; }
        }
        private static Dictionary<int, Source> LoadSources()
        {
            Dictionary<int, Source> result = new Dictionary<int, Source>();
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MikuSprites"))
            using (Bitmap sheet = new Bitmap(stream))
            {
                BitmapData data = sheet.LockBits(new Rectangle(0, 0, sheet.Width, sheet.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    foreach (int row in new int[] { 0, 1, 2, 4, 7, 9, 10 })
                    {
                        int count = row >= 9 ? 8 : Behavior.Times[row].Length;
                        for (int frame = 0; frame < count; frame++)
                        {
                            int col = row == Behavior.Idle ? frame + 1 : frame;
                            Source source = new Source { Pixels = new byte[W * H * 4], Alpha = new byte[W * H] };
                            for (int py = 0; py < H; py++)
                                Marshal.Copy(IntPtr.Add(data.Scan0, (row * H + py) * data.Stride + col * W * 4), source.Pixels, py * W * 4, W * 4);
                            for (int p = 0; p < source.Alpha.Length; p++) source.Alpha[p] = source.Pixels[p * 4 + 3];
                            if (row != 4 || col == 0 || col == 4) Ground(source);
                            result.Add(row * 8 + col, source);
                        }
                    }
                }
                finally { sheet.UnlockBits(data); }
            }
            return result;
        }
        private static void Ground(Source source)
        {
            int bottom = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++) if (source.Alpha[y * W + x] >= 24) bottom = y + 1;
            int offset = FootBottom - bottom;
            if (offset != 0)
            {
                byte[] moved = new byte[source.Pixels.Length];
                for (int y = 0; y < H; y++)
                    if (y + offset >= 0 && y + offset < H)
                        Buffer.BlockCopy(source.Pixels, y * W * 4, moved, (y + offset) * W * 4, W * 4);
                source.Pixels = moved;
            }
            // Keep the transparent window tail, but no visible pixels below the shared baseline.
            Array.Clear(source.Pixels, FootBottom * W * 4, (H - FootBottom) * W * 4);
            for (int p = 0; p < source.Alpha.Length; p++) source.Alpha[p] = source.Pixels[p * 4 + 3];
        }
        internal static void Export(string folder)
        {
            Directory.CreateDirectory(folder);
            foreach (int index in Sources.Keys)
            {
                using (Bitmap bitmap = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                {
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(Sources[index].Pixels, 0, data.Scan0, W * H * 4);
                    bitmap.UnlockBits(data);
                    bitmap.Save(Path.Combine(folder, index + ".png"), ImageFormat.Png);
                }
            }
        }
        internal int Index(int state, int frame)
        {
            if (state == Behavior.Look) return 72 + frame;
            if (state == Behavior.Idle) return frame + 1;
            return state * 8 + frame;
        }
        internal bool Hit(int state, int frame, int x, int y, bool hover)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            int at = (y * H / Height) * W + x * W / Width;
            return (hover ? HoverMask[at] : Tiles[Index(state, frame)].Alpha[at]) >= 24;
        }
        internal void Render(IntPtr window, int x, int y, int state, int frame)
        {
            Native.SelectObject(DC, Tiles[Index(state, frame)].Bitmap);
            Native.POINT dst = new Native.POINT(x, y), src = new Native.POINT(0, 0);
            Native.SIZE size = new Native.SIZE(Width, Height);
            Native.BLEND blend = new Native.BLEND { Alpha = 255, Format = 1 };
            if (!Native.UpdateLayeredWindow(window, IntPtr.Zero, ref dst, ref size, DC, ref src, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose()
        {
            if (DC != IntPtr.Zero && original != IntPtr.Zero) Native.SelectObject(DC, original);
            foreach (Tile tile in Tiles.Values) Native.DeleteObject(tile.Bitmap);
            Tiles.Clear();
            if (DC != IntPtr.Zero) Native.DeleteDC(DC);
            DC = IntPtr.Zero;
        }
    }

    internal sealed class PetWindow : Form
    {
        internal readonly Behavior Behavior = new Behavior();
        internal readonly Settings Config = new Settings();
        internal Atlas Atlas;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private NotifyIcon tray;
        private ContextMenuStrip menu;
        private ToolStripMenuItem pauseItem, hideItem;
        private IntPtr rawBuffer = Marshal.AllocHGlobal(256);
        private bool inputRegistered, paused, hidden, menuOpen, disposed, initializing = true;
        private bool pressed, dragging, mousePending;
        private Point pressCursor, lastCursor, dragCursor, pointer;
        private double gripX, gripY;
        private int x, y, dpi = 96;
        private Screen currentScreen;
        private readonly bool verify;
        private readonly uint showMessage = Native.RegisterWindowMessage("MikuDesktop_Show_8B221B24");
        internal int RenderCount;

        internal PetWindow(bool test)
        {
            verify = test;
            if (!test) Config.Load();
            Behavior.TypingMs = Config.Typing; Behavior.GazeMs = Config.Gaze;
            AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Text = "Miku Desktop"; Size = new Size(192, 208);
            timer.Tick += delegate { timer.Stop(); Pump(); };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle = (cp.ExStyle & ~0x40000) | 0x80000 | 0x80 | 0x08000000;
                if (Config.OnTop) cp.ExStyle |= 8;
                return cp;
            }
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Screen screen = Config.X == int.MinValue ? Screen.PrimaryScreen : Screen.FromPoint(new Point(Config.X + 96, Config.Y + 104));
            currentScreen = screen;
            dpi = Native.Dpi(screen);
            Rebuild();
            x = Config.X == int.MinValue ? screen.WorkingArea.Right - Atlas.Width - Dip(32) : Config.X;
            y = Config.Y == int.MinValue ? screen.WorkingArea.Bottom - VisibleHeight(Atlas.Height) : Config.Y;
            Clamp(screen); initializing = false;
            Native.POINT pos; Native.GetCursorPos(out pos); pointer = lastCursor = pos.ToPoint();
            CreateTray(); RegisterInput(true);
            Behavior.Reset(clock.ElapsedMilliseconds); Render(); Arm(280);
        }
        private int Dip(int value) { return Math.Max(1, (int)Math.Round(value * dpi / 96.0)); }
        private void Rebuild()
        {
            int width = Math.Max(48, (int)Math.Round(192 * Config.Scale / 100.0));
            int height = Math.Max(52, (int)Math.Round(208 * Config.Scale / 100.0));
            if (currentScreen != null)
            {
                int availableWidth = Math.Max(1, currentScreen.WorkingArea.Width);
                int availableHeight = Math.Max(1, currentScreen.WorkingArea.Height);
                Size fitted = Fit(new Size(width, height), new Size(availableWidth, availableHeight));
                width = fitted.Width; height = fitted.Height;
            }
            if (Atlas != null && Atlas.Width == width && Atlas.Height == height) return;
            Program.Trace("cache " + width + "x" + height);
            Atlas next = new Atlas(width, height);
            if (Atlas != null) Atlas.Dispose();
            Atlas = next; Size = new Size(width, height);
        }
        internal static Point ClampPoint(Rectangle work, Rectangle monitor, int width, int height, int dpiValue, Point wanted, int topInset = 0, int leftInset = 0, int rightInset = 0)
        {
            // Only transparent rows may leave the work area. The highest jump pixel
            // touches its upper boundary, including a taskbar docked at the top.
            int top = work.Top - topInset;
            int left = work.Left - leftInset, right = Math.Max(left, work.Right - width + rightInset);
            int bottom = Math.Max(top, work.Bottom - VisibleHeight(height));
            return new Point(Math.Max(left, Math.Min(right, wanted.X)), Math.Max(top, Math.Min(bottom, wanted.Y)));
        }
        private static int VisibleHeight(int height) { return (Atlas.FootBottom * height + Atlas.H - 1) / Atlas.H; }
        internal static Size Fit(Size wanted, Size available)
        {
            double factor = Math.Min(1.0, Math.Min(available.Width / (double)wanted.Width, available.Height / (double)wanted.Height));
            return new Size(Math.Max(1, (int)Math.Floor(wanted.Width * factor)), Math.Max(1, (int)Math.Floor(wanted.Height * factor)));
        }
        private void Clamp(Screen screen)
        {
            Point clamped = ClampPoint(screen.WorkingArea, screen.Bounds, Atlas.Width, Atlas.Height, dpi, new Point(x, y), Atlas.TopInset, Atlas.LeftInset, Atlas.RightInset);
            x = clamped.X; y = clamped.Y;
        }
        private void Render() { if (Atlas != null && !hidden) { Atlas.Render(Handle, x, y, Behavior.State, Behavior.Frame); RenderCount++; } }
        private void Arm(int interval)
        {
            if (paused || hidden || disposed || initializing) return;
            interval = Math.Max(15, Math.Min(1000, interval));
            if (timer.Enabled && timer.Interval <= interval) return;
            timer.Stop(); timer.Interval = interval; timer.Start();
        }
        private void Pump()
        {
            if (disposed || paused || hidden) return;
            long now = clock.ElapsedMilliseconds;
            Native.POINT cursor; Native.GetCursorPos(out cursor); pointer = cursor.ToPoint();
            bool moved = false;
            if (pressed && !dragging && (Math.Abs(pointer.X - pressCursor.X) >= Dip(4) || Math.Abs(pointer.Y - pressCursor.Y) >= Dip(4)))
            {
                dragging = Behavior.Dragging = true; Behavior.Hover = false;
                dragCursor = pressCursor;
            }
            if (dragging)
            {
                Screen screen = Screen.FromPoint(pointer);
                int newDpi = Native.Dpi(screen);
                if (newDpi != dpi || currentScreen.DeviceName != screen.DeviceName)
                { currentScreen = screen; dpi = newDpi; Rebuild(); }
                int horizontal = pointer.X - dragCursor.X;
                if (Math.Abs(horizontal) >= Dip(2)) { Behavior.DragMotion(now, horizontal); dragCursor = pointer; }
                int oldX = x, oldY = y;
                x = pointer.X - (int)Math.Round(gripX * Atlas.Width);
                y = pointer.Y - (int)Math.Round(gripY * Atlas.Height);
                Clamp(screen); moved = x != oldX || y != oldY;
            }
            else
            {
                Behavior.Hover = !menuOpen && Atlas.Hit(Behavior.State, Behavior.Frame, pointer.X - x, pointer.Y - y, Behavior.State == Behavior.Jump);
                if (mousePending && !menuOpen)
                    Behavior.Motion(now, (pointer.X - x) * 192.0 / Atlas.Width - 96, (pointer.Y - y) * 208.0 / Atlas.Height - 96);
            }
            mousePending = false;
            bool changed = Behavior.Step(now);
            if (changed || moved) Render();
            Arm(Behavior.NextDelay(now));
        }
        private void RegisterInput(bool enabled)
        {
            if (inputRegistered == enabled) return;
            Native.RAWINPUTDEVICE[] devices = new Native.RAWINPUTDEVICE[2];
            devices[0].page = devices[1].page = 1;
            devices[0].usage = 2; devices[1].usage = 6;
            for (int i = 0; i < 2; i++) { devices[i].flags = enabled ? 0x100u : 1u; devices[i].target = enabled ? Handle : IntPtr.Zero; }
            if (!Native.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            inputRegistered = enabled;
        }
        private void RawInput(IntPtr handle)
        {
            if (!inputRegistered || menuOpen) return;
            uint size = 256;
            uint header = (uint)(8 + IntPtr.Size * 2);
            uint read = Native.GetRawInputData(handle, 0x10000003, rawBuffer, ref size, header);
            if (read == uint.MaxValue || read < header) return;
            int type = Marshal.ReadInt32(rawBuffer);
            if (type == 1 && read >= header + 16)
            {
                int key = (ushort)Marshal.ReadInt16(rawBuffer, (int)header + 6);
                int flags = (ushort)Marshal.ReadInt16(rawBuffer, (int)header + 2);
                Behavior.Key(clock.ElapsedMilliseconds, key, (flags & 1) != 0);
                Arm(33);
            }
            else if (type == 0)
            {
                Native.POINT pos; Native.GetCursorPos(out pos); Point p = pos.ToPoint();
                if (p != lastCursor) { lastCursor = p; mousePending = true; Arm(33); }
            }
        }
        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == showMessage && !initializing) { hidden = false; ChangeActivity(); return; }
            if (m.Msg == 0xFF) RawInput(m.LParam);
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            if (m.Msg == 0x84 && Atlas != null)
            {
                long at = m.LParam.ToInt64(); int px = (short)(at & 0xffff), py = (short)((at >> 16) & 0xffff);
                m.Result = new IntPtr(Atlas.Hit(Behavior.State, Behavior.Frame, px - x, py - y, false) ? 1 : -1);
                return;
            }
            if (m.Msg == 0x201 && !paused && !hidden && !menuOpen)
            {
                Native.POINT p; Native.GetCursorPos(out p);
                pressed = true; pressCursor = p.ToPoint();
                gripX = (pressCursor.X - x) / (double)Atlas.Width;
                gripY = (pressCursor.Y - y) / (double)Atlas.Height;
                Native.SetCapture(Handle); Arm(15); return;
            }
            if (m.Msg == 0x200 && pressed) { Arm(15); return; }
            if (m.Msg == 0x202) { EndDrag(); return; }
            if (m.Msg == 0x215 && pressed) EndDrag();
            if ((m.Msg == 0x7E || m.Msg == 0x2E0 || m.Msg == 0x1A) && !initializing && Atlas != null)
            {
                Screen s = Screen.FromPoint(new Point(x + Atlas.Width / 2, y + Atlas.Height / 2));
                currentScreen = s;
                dpi = Native.Dpi(s); Rebuild(); Clamp(s); Render();
            }
            base.WndProc(ref m);
        }
        private void EndDrag()
        {
            if (!pressed) return;
            bool wasDragging = dragging;
            pressed = dragging = Behavior.Dragging = false;
            Native.ReleaseCapture();
            if (wasDragging) Save();
            Arm(15);
        }
        private ToolStripMenuItem Add(string label, EventHandler action)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label); item.Click += action; menu.Items.Add(item); return item;
        }
        private void CreateTray()
        {
            menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Items.Add(new ToolStripMenuItem("하츠네 미쿠 · Miku Desktop") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            pauseItem = Add("일시정지", delegate { paused = !paused; ChangeActivity(); });
            hideItem = Add("미쿠 숨기기", delegate { hidden = !hidden; ChangeActivity(); });
            ToolStripMenuItem sizes = new ToolStripMenuItem("크기"); menu.Items.Add(sizes);
            foreach (int value in new int[] { 50, 75, 100, 125, 150, 200 })
            {
                int percent = value;
                ToolStripMenuItem option = new ToolStripMenuItem(value + "%"); option.Tag = value;
                option.Click += delegate { Config.Scale = percent; Rebuild(); Clamp(Screen.FromPoint(new Point(x, y))); Render(); Save(); };
                sizes.DropDownItems.Add(option);
            }
            AddDelayMenu("타이핑 종료 대기", true, new int[] { 1000, 1500, 2000, 3000 });
            AddDelayMenu("시선 유지 시간", false, new int[] { 500, 1000, 1500, 2000 });
            ToolStripMenuItem top = Add("항상 위에 표시", delegate { Config.OnTop = !Config.OnTop; ApplyTop(); Save(); });
            top.Name = "top";
            Add("위치 초기화", delegate
            {
                Screen s = Screen.PrimaryScreen; currentScreen = s; dpi = Native.Dpi(s); Rebuild();
                x = s.WorkingArea.Right - Atlas.Width - Dip(32); y = s.WorkingArea.Bottom - VisibleHeight(Atlas.Height);
                Clamp(s); Render(); Save();
            });
            menu.Items.Add(new ToolStripSeparator());
            Add("사용법", delegate
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "사용법.txt");
                if (File.Exists(file)) Process.Start(new ProcessStartInfo("notepad.exe", "\"" + file + "\""));
            });
            Add("종료", delegate { Close(); });
            menu.Opening += delegate
            {
                menuOpen = true; Behavior.Hover = false;
                pauseItem.Checked = paused; hideItem.Text = hidden ? "미쿠 다시 표시" : "미쿠 숨기기";
                top.Checked = Config.OnTop;
                foreach (ToolStripItem item in sizes.DropDownItems)
                    ((ToolStripMenuItem)item).Checked = (int)item.Tag == Config.Scale;
            };
            menu.Closed += delegate { menuOpen = false; Arm(33); };
            tray = new NotifyIcon(); tray.Text = "하츠네 미쿠 · Miku Desktop";
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MikuIcon")) tray.Icon = new Icon(stream);
            tray.ContextMenuStrip = menu; tray.Visible = true;
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMenu(); };
        }
        private void ShowMenu() { if (menu != null) menu.Show(Cursor.Position); }
        private void AddDelayMenu(string label, bool typing, int[] values)
        {
            ToolStripMenuItem group = new ToolStripMenuItem(label); menu.Items.Add(group);
            foreach (int value in values)
            {
                int ms = value;
                ToolStripMenuItem option = new ToolStripMenuItem((value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "초");
                option.Tag = value;
                option.Click += delegate
                {
                    if (typing) Config.Typing = Behavior.TypingMs = ms;
                    else Config.Gaze = Behavior.GazeMs = ms;
                    Save(); Arm(33);
                };
                group.DropDownItems.Add(option);
            }
            group.DropDownOpening += delegate
            {
                foreach (ToolStripItem item in group.DropDownItems)
                    ((ToolStripMenuItem)item).Checked = (int)item.Tag == (typing ? Config.Typing : Config.Gaze);
            };
        }
        private void ApplyTop()
        {
            Native.SetWindowPos(Handle, new IntPtr(Config.OnTop ? -1 : -2), 0, 0, 0, 0, 0x13);
        }
        private void ChangeActivity()
        {
            EndDrag(); timer.Stop(); Behavior.Reset(clock.ElapsedMilliseconds);
            RegisterInput(!paused && !hidden);
            if (hidden) Hide();
            else { Show(); ApplyTop(); Render(); Arm(280); }
        }
        private void Save()
        {
            if (verify) return;
            Config.X = x; Config.Y = y;
            if (!Config.Save() && tray != null)
                tray.ShowBalloonTip(4000, "설정을 저장하지 못했습니다", "앱을 쓰기 가능한 폴더로 옮겨 주세요.", ToolTipIcon.Info);
        }
        internal void Verify(string path)
        {
            Program.Trace("verify start");
            StringBuilder report = new StringBuilder();
            report.AppendLine("Miku Desktop native-window verification");
            uint style = unchecked((uint)Native.GetWindowLong(Handle, -20));
            report.AppendLine("extended_style=0x" + style.ToString("X8"));
            Program.Check((style & 0x08080080) == 0x08080080 && (style & 0x40000) == 0, "toolwindow/layered/noactivate/no-appwindow");
            Program.Check(inputRegistered && tray.Visible, "raw input and tray registered");
            int before = Native.GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            Program.Trace("render stress start");
            int[] states = { 0, 1, 2, 4, 7, 9 };
            for (int repeat = 0; repeat < 20; repeat++)
                foreach (int state in states)
                    for (int frame = 0; frame < (state == 9 ? 16 : Behavior.Times[state].Length); frame++)
                        Atlas.Render(Handle, x, y, state, frame);
            int after = Native.GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            Program.Trace("render stress end");
            Program.Check(after <= before + 2, "no per-frame GDI resource growth");
            report.AppendLine("cached_frames=" + Atlas.Tiles.Count);
            report.AppendLine("gdi_before=" + before + "; gdi_after=" + after);
            Program.Check(Atlas.Tiles.Count == 48 && !Atlas.Tiles.ContainsKey(0), "48 original frames cached; idle column zero and bridges excluded");
            for (int frame = 0; frame < 6; frame++)
                Program.Check(!Atlas.Tiles.ContainsKey(48 + frame) && !Atlas.Tiles.ContainsKey(64 + frame), "waiting and review frames are not cached");
            for (int frame = 0; frame < Behavior.Times[Behavior.Idle].Length; frame++)
                Program.Check(Atlas.Index(Behavior.Idle, frame) == frame + 1, "idle uses only original columns one through five");
            Program.Check(Atlas.Width == 192 && Atlas.Height == 208, "100 percent means native physical pixels");
            report.AppendLine("highest_cached_pixel_y=" + Atlas.TopInset);
            report.AppendLine("transparent_side_insets=" + Atlas.LeftInset + "," + Atlas.RightInset);
            VerifyTopBoundary();
            Program.Check(!Atlas.Hit(0, 0, 0, 0, false), "transparent corner does not capture clicks");
            bool opaque = false;
            for (int py = 0; py < Atlas.Height && !opaque; py++)
                for (int px = 0; px < Atlas.Width && !opaque; px++) opaque = Atlas.Hit(0, 0, px, py, false);
            Program.Check(opaque, "visible character can receive clicks");
            // Change scale repeatedly and ensure old DIB handles are released.
            for (int repeat = 0; repeat < 3; repeat++)
                foreach (int percent in new int[] { 50, 200, 100 }) { Config.Scale = percent; Rebuild(); VerifyTopBoundary(); Render(); }
            Program.Check(Native.GetGuiResources(Process.GetCurrentProcess().Handle, 0) <= before + 4, "resizing releases old surfaces");
            Program.Trace("resize end");
            paused = true; ChangeActivity();
            Program.Check(!inputRegistered && !timer.Enabled, "paused: no input registration or animation timer");
            paused = false; hidden = true; ChangeActivity();
            Program.Check(!inputRegistered && !timer.Enabled && !Visible, "hidden: no input registration or timer");
            hidden = false; ChangeActivity();
            Program.Check(inputRegistered && timer.Enabled && Visible, "resume restores input and animation");
            Atlas.Render(Handle, x, y, 0, 0);
            report.AppendLine("working_area=" + Screen.FromPoint(new Point(x, y)).WorkingArea);
            report.AppendLine("window=" + new Rectangle(x, y, Atlas.Width, Atlas.Height));
            report.AppendLine("private_bytes=" + Process.GetCurrentProcess().PrivateMemorySize64);
            report.AppendLine("result=PASS");
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
            Close();
        }
        private void VerifyTopBoundary()
        {
            Rectangle work = currentScreen.WorkingArea;
            Point atTop = ClampPoint(work, currentScreen.Bounds, Atlas.Width, Atlas.Height, dpi, new Point(x, work.Top - 10000), Atlas.TopInset);
            Program.Check(atTop.Y + Atlas.TopInset == work.Top, "highest jump pixel touches work-area top");
            Program.Check(Atlas.Tiles[34].TopPixel == Atlas.TopInset, "jump apex defines the upper limit");
            foreach (Atlas.Tile tile in Atlas.Tiles.Values)
                Program.Check(atTop.Y + tile.TopPixel >= work.Top, "no animation frame has pixels clipped at top");
            Point atLeft = ClampPoint(work, currentScreen.Bounds, Atlas.Width, Atlas.Height, dpi, new Point(work.Left - 10000, y), Atlas.TopInset, Atlas.LeftInset, Atlas.RightInset);
            Point atRight = ClampPoint(work, currentScreen.Bounds, Atlas.Width, Atlas.Height, dpi, new Point(work.Right + 10000, y), Atlas.TopInset, Atlas.LeftInset, Atlas.RightInset);
            Program.Check(atLeft.X + Atlas.LeftInset == work.Left, "widest pose touches left work-area edge");
            Program.Check(atRight.X + Atlas.Width - Atlas.RightInset == work.Right, "widest pose touches right work-area edge");
            foreach (Atlas.Tile tile in Atlas.Tiles.Values)
            {
                Program.Check(atLeft.X + tile.LeftPixel >= work.Left, "no animation is clipped at left edge");
                Program.Check(atRight.X + tile.RightPixel <= work.Right, "no animation is clipped at right edge");
            }
        }
        internal void Benchmark(string path)
        {
            StringBuilder report = new StringBuilder("Miku Desktop performance sample\r\n");
            System.Windows.Forms.Timer sample = new System.Windows.Forms.Timer { Interval = 6000 };
            Process process = Process.GetCurrentProcess();
            int phase = 0, initialFrames = RenderCount;
            TimeSpan initialCpu = process.TotalProcessorTime;
            long started = clock.ElapsedMilliseconds;
            sample.Tick += delegate
            {
                process.Refresh();
                double elapsed = (clock.ElapsedMilliseconds - started) / 1000.0;
                double cpu = (process.TotalProcessorTime - initialCpu).TotalSeconds;
                report.AppendLine((phase == 0 ? "visible" : "hidden") + ": seconds=" + elapsed.ToString("F2", CultureInfo.InvariantCulture) +
                    ", cpu_seconds=" + cpu.ToString("F4", CultureInfo.InvariantCulture) +
                    ", one_core_percent=" + (cpu / elapsed * 100).ToString("F2", CultureInfo.InvariantCulture) +
                    ", frames=" + (RenderCount - initialFrames) + ", private_bytes=" + process.PrivateMemorySize64 +
                    ", working_set=" + process.WorkingSet64 + ", final_state=" + Behavior.State);
                if (phase == 0)
                {
                    hidden = true; ChangeActivity(); phase = 1;
                    initialFrames = RenderCount; process.Refresh(); initialCpu = process.TotalProcessorTime; started = clock.ElapsedMilliseconds;
                }
                else
                {
                    sample.Stop(); sample.Dispose();
                    Program.Check(RenderCount == initialFrames && !inputRegistered && !timer.Enabled, "hidden remains asleep");
                    report.AppendLine("result=PASS"); File.WriteAllText(path, report.ToString(), Encoding.UTF8); Close();
                }
            };
            sample.Start();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (!disposed)
            {
                Save(); timer.Stop(); timer.Dispose();
                if (inputRegistered) RegisterInput(false);
                if (tray != null) { tray.Visible = false; tray.Icon.Dispose(); tray.Dispose(); }
                if (menu != null) menu.Dispose();
                if (Atlas != null) Atlas.Dispose();
                Marshal.FreeHGlobal(rawBuffer); rawBuffer = IntPtr.Zero; disposed = true;
            }
            base.OnFormClosed(e);
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct POINT
        {
            internal int X, Y; internal POINT(int x, int y) { X = x; Y = y; }
            internal Point ToPoint() { return new Point(X, Y); }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct SIZE { internal int W, H; internal SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct BLEND { internal byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFO
        {
            internal uint size; internal int width, height; internal ushort planes, bits;
            internal uint compression, imageSize; internal int xppm, yppm; internal uint used, important;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct RAWINPUTDEVICE { internal ushort page, usage; internal uint flags; internal IntPtr target; }
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dstDC, ref POINT dst, ref SIZE size, IntPtr srcDC, ref POINT src, uint key, ref BLEND blend, uint flags);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint use, out IntPtr pixels, IntPtr section, uint offset);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint header);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] internal static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern int GetGuiResources(IntPtr process, int flags);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, int length, out int needed);
        internal static string DesktopName()
        {
            StringBuilder text = new StringBuilder(256); int needed;
            if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, text, 512, out needed))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return text.ToString();
        }
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        internal static int Dpi(Screen screen)
        {
            try
            {
                uint x, y;
                IntPtr monitor = MonitorFromPoint(new POINT(screen.Bounds.Left + screen.Bounds.Width / 2, screen.Bounds.Top + screen.Bounds.Height / 2), 2);
                if (GetDpiForMonitor(monitor, 0, out x, out y) == 0) return (int)x;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 96;
        }
    }

    internal static class Program
    {
        private static string tracePath;
        internal static void Trace(string message)
        {
            if (tracePath != null) File.AppendAllText(tracePath, DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + message + "\r\n");
        }
        internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Verification failed: " + message); }
        [STAThread] private static void Main(string[] args)
        {
            bool test = args.Length > 0 && (args[0] == "--self-test" || args[0] == "--verify" || args[0] == "--benchmark");
            try
            {
                if (args.Length == 2 && args[0] == "--verify") tracePath = args[1] + ".trace";
                if (args.Length == 2 && args[0] == "--self-test") { SelfTest(args[1]); return; }
                if (args.Length == 2 && args[0] == "--export-frames") { Atlas.Export(args[1]); return; }
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
                bool created;
                // Windows desktops share session-scoped mutexes but cannot receive each other's
                // window broadcasts. Keep one instance per desktop, including isolated test desktops.
                using (Mutex mutex = new Mutex(true, "Local\\MikuDesktop_8B221B24_" + Native.DesktopName(), out created))
                {
                    if (!created)
                    {
                        if (!test) Native.PostMessage(new IntPtr(0xffff), Native.RegisterWindowMessage("MikuDesktop_Show_8B221B24"), IntPtr.Zero, IntPtr.Zero);
                        return;
                    }
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (PetWindow pet = new PetWindow(test))
                    {
                        if (args.Length == 2 && args[0] == "--verify")
                            pet.Shown += delegate { pet.BeginInvoke(new Action(delegate { pet.Verify(args[1]); })); };
                        if (args.Length == 2 && args[0] == "--benchmark")
                            pet.Shown += delegate { pet.BeginInvoke(new Action(delegate { pet.Benchmark(args[1]); })); };
                        Application.Run(pet);
                    }
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception error)
            {
                if (test && args.Length == 2) File.WriteAllText(args[1], "FAIL\r\n" + error, Encoding.UTF8);
                else MessageBox.Show("미쿠를 실행하지 못했습니다.\n\n" + error.Message, "Miku Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
        }
        private static void SelfTest(string path)
        {
            Behavior b = new Behavior(); b.Reset(0);
            b.Key(10, 65, false); b.Step(10); Check(b.State == Behavior.Focus && b.Frame == 0, "typing uses only the original focus animation");
            int selected = b.State;
            b.Key(1400, 66, false); b.Step(2000); Check(b.State == selected, "latest keystroke extends the same typing pose");
            b.Step(2899); Check(b.State == selected, "typing remains until last key plus 1.5 seconds");
            b.Step(2900); Check(b.State == Behavior.Idle && b.Frame == 0, "typing expiry switches immediately to remaining idle frames");
            b.Reset(0); b.Step(2999); Check(b.Frame == 0, "idle eyes stay open for three seconds");
            b.Step(3000); Check(b.Frame == 0, "removed idle frame hold is transferred to the next frame");
            b.Step(3110); b.Step(3220); b.Step(3360); b.Step(3500); b.Step(5300);
            Check(b.State == Behavior.Idle && b.Frame == 0 && b.Due == 8410, "single blink cycle lasts 5.3 seconds");
            b.Reset(0); b.Key(10, 160, false); b.Key(10, 65, true); b.Step(10); Check(b.State == Behavior.Idle, "modifiers and key releases ignored");
            for (int direction = 0; direction < 16; direction++)
            {
                b.Reset(0); double angle = direction * Math.PI / 8;
                b.Motion(10, Math.Sin(angle) * 100, -Math.Cos(angle) * 100); b.Step(10);
                Check(b.State == Behavior.Look && b.Frame == direction, "cursor direction " + direction);
                b.Step(1009); Check(b.State == Behavior.Look, "gaze hold");
                b.Step(1010); Check(b.State == Behavior.Idle, "gaze expiry");
            }
            b.Reset(0); b.Motion(10, 0, -100); b.Motion(20, 25, -100); Check(b.Direction == 0, "sector hysteresis");
            b.Motion(30, 40, -100); Check(b.Direction == 1, "sector transition beyond hysteresis");
            b.Step(30); Check(b.State == Behavior.Look, "gaze becomes active before typing");
            b.Key(40, 65, false); b.Step(40); Check(b.State == Behavior.Focus, "typing beats gaze");
            b.Hover = true; b.Step(50); Check(b.State == Behavior.Jump, "hover beats typing");
            int loops = 0, previous = b.Frame;
            for (int now = 60; now < 3000; now += 10)
            {
                b.Step(now); if (previous == 4 && b.Frame == 0) loops++; previous = b.Frame;
            }
            Check(loops >= 3 && b.State == Behavior.Jump, "hover repeats jump continuously");
            b.Dragging = true; b.DragMotion(3000, -10); b.Step(3000); Check(b.State == Behavior.Left, "drag cancels jump and runs left");
            b.DragMotion(3020, 10); b.Step(3020); Check(b.State == Behavior.Right, "drag runs right");
            b.Step(3181); Check(b.State == Behavior.Idle, "stationary held drag stops running immediately");
            b.Dragging = false; b.Step(3200); Check(b.State == Behavior.Jump, "release over pet resumes jump");
            for (int departure = 0; departure < Behavior.Times[Behavior.Jump].Length; departure++)
            {
                Behavior jumping = new Behavior(); jumping.Reset(0); jumping.Hover = true; jumping.Step(0);
                long now = 0;
                for (int frame = 0; frame < departure; frame++) { now = jumping.Due; jumping.Step(now); }
                jumping.Hover = false; jumping.Step(now + 1);
                Check(jumping.State == Behavior.Jump && jumping.Frame == departure, "leaving at each jump phase preserves the cycle");
                for (int i = 0; i < 6 && jumping.State != Behavior.Idle; i++) jumping.Step(jumping.Due);
                Check(jumping.State == Behavior.Idle, "each departed jump completes landing");
            }
            foreach (int state in new int[] { Behavior.Right, Behavior.Left, Behavior.Focus })
            {
                for (int frame = 0; frame < Behavior.Times[state].Length; frame++)
                {
                    Behavior ending = new Behavior(); ending.Reset(0);
                    ending.State = state; ending.Frame = frame; ending.Due = Behavior.Times[state][frame];
                    ending.Step(1); Check(ending.State == Behavior.Idle, "non-jump actions exit immediately from every frame");
                }
            }
            b.Reset(0); b.Hover = true; b.Step(0); b.Step(140);
            b.Hover = false; b.Key(150, 65, false); b.Step(150);
            Check(b.State == Behavior.Jump, "typing during a departed jump waits for landing");
            for (int i = 0; i < 6 && b.State == Behavior.Jump; i++) b.Step(b.Due);
            Check(b.State == Behavior.Focus, "landed jump switches to the active focus pose");
            Behavior bursts = new Behavior(); bursts.Reset(0);
            for (int burst = 0; burst < 200; burst++)
            {
                long start = burst * 5000L;
                bursts.Key(start, 160, false); bursts.Key(start, 65, true);
                bursts.Key(start, 65, false); bursts.Step(start);
                Check(bursts.State == Behavior.Focus, "every typing burst uses only focus");
                bursts.Key(start + 100, 66, false); bursts.Key(start + 1499, 67, false);
                bursts.Step(start + 2000); Check(bursts.State == Behavior.Focus, "continuous typing maintains focus");
                bursts.Hover = true; Check(bursts.Desired(start + 2100) == Behavior.Jump, "hover overrides every typing pose");
                bursts.Dragging = true; bursts.DragMotion(start + 2100, -10);
                Check(bursts.Desired(start + 2100) == Behavior.Left, "drag overrides every typing pose");
                bursts.Hover = bursts.Dragging = false; bursts.Step(start + 2100);
                Check(bursts.State == Behavior.Focus, "interrupted typing resumes focus");
                bursts.Step(start + 2998); Check(bursts.State == Behavior.Focus, "typing retains debounce");
                bursts.Step(start + 2999); Check(bursts.State == Behavior.Idle, "typing exits exactly when its trigger expires");
            }
            Point p = PetWindow.ClampPoint(new Rectangle(0, 0, 1920, 1040), new Rectangle(0, 0, 1920, 1080), 192, 208, 96, new Point(5000, -500));
            Check(p.X == 1728 && p.Y == 0, "top and right bounds");
            p = PetWindow.ClampPoint(new Rectangle(-1920, -1080, 1920, 1040), new Rectangle(-1920, -1080, 1920, 1080), 288, 312, 144, new Point(-9000, 5000));
            Check(p.X == -1920 && p.Y == -343, "negative monitor coordinates and DPI bounds");
            p = PetWindow.ClampPoint(new Rectangle(0, 0, 1920, 1040), new Rectangle(0, 0, 1920, 1080), 192, 208, 96, new Point(500, 5000));
            Check(p.Y + Atlas.FootBottom == 1040, "feet can touch the taskbar edge without a bottom gap");
            p = PetWindow.ClampPoint(new Rectangle(40, 40, 1880, 1040), new Rectangle(0, 0, 1920, 1080), 192, 208, 96, new Point(-100, -100));
            Check(p.X == 40 && p.Y == 40, "left or top taskbar respected");
            p = PetWindow.ClampPoint(new Rectangle(40, 40, 1880, 1040), new Rectangle(0, 0, 1920, 1080), 192, 208, 96, new Point(-100, 200), 1, 21, 22);
            Check(p.X == 19 && p.X + 21 == 40, "transparent left margin may overlap a left taskbar without visible pixels doing so");
            p = PetWindow.ClampPoint(new Rectangle(-1920, 0, 1920, 1040), new Rectangle(-1920, 0, 1920, 1080), 192, 208, 96, new Point(5000, 200), 1, 21, 22);
            Check(p.X == -170 && p.X + 192 - 22 == 0, "right visible edge on a negative-coordinate monitor");
            p = PetWindow.ClampPoint(new Rectangle(0, 0, 1920, 1040), new Rectangle(0, 0, 1920, 1080), 192, 208, 96, new Point(500, -500), 1);
            Check(p.Y == -1 && p.Y + 1 == 0, "transparent top row can leave screen without clipping jump");
            p = PetWindow.ClampPoint(new Rectangle(0, 40, 1920, 1040), new Rectangle(0, 0, 1920, 1080), 384, 416, 96, new Point(500, -500), 2);
            Check(p.Y == 38 && p.Y + 2 == 40, "scaled apex touches work-area edge below top taskbar");
            Size fit = PetWindow.Fit(new Size(960, 1040), new Size(800, 600));
            Check(fit.Width <= 800 && fit.Height == 600, "oversized sprite fits small high-DPI display");
            File.WriteAllText(path, "PASS\r\n200 typing bursts use focus only, modifier/up filtering and immediate exit at 1.5 seconds after the latest key. Running and focus exit immediately from every frame. Every jump departure phase completes landing; dragging interrupts jumping. Idle column zero excluded, 5.3-second blink period retained. All 16 gaze sectors, gaze hold, hysteresis, repeated hover jump, drag direction, work-area limits and DPI coordinates.\r\n", Encoding.UTF8);
        }
    }
}
