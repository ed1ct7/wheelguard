// WheelGuard: tray tool for a mouse whose wheel (middle) button sticks.
//  - Hotkey (default Ctrl+Alt+W), the tray menu or the window toggles the wheel click on/off.
//  - While enabled it only polls the middle-button state; if Windows sees it held longer
//    than holdSeconds (longHoldSeconds in longHoldApps, e.g. Blender), the wheel click is
//    switched off automatically and the stuck state in Windows is released.
//  - While disabled, a low-level mouse hook swallows physical middle-button events.
//    The hook exists only in that mode, so normal use adds no input latency.
//  - The physical button state (window, optional tray indicator) comes from raw input on a
//    separate thread, registered only while it is shown; raw input never delays other apps.
// Build: csc /target:winexe /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll WheelGuard.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WheelGuard
{
    static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_HOTKEY = 0x312, WM_INPUT = 0xFF;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x40, MOD_NOREPEAT = 0x4000;
        public const uint RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100, RID_INPUT = 0x10000003;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public static bool MiddleHeld() { return (GetAsyncKeyState(4) & 0x8000) != 0; }
    }

    sealed class Settings
    {
        public bool Enabled = true, AutoDisable = true, TrayIndicator = true;
        public int HoldSeconds = 5, LongHoldSeconds = 60;
        public string LongHoldApps = "blender";
        public string Hotkey = "Ctrl+Alt+W";
        public readonly string Path;

        public Settings(string path)
        {
            Path = path;
            if (!File.Exists(path)) { Save(); return; }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                int n;
                switch (k)
                {
                    case "enabled": Enabled = v != "0"; break;
                    case "autodisable": AutoDisable = v != "0"; break;
                    case "trayindicator": TrayIndicator = v != "0"; break;
                    case "holdseconds": if (int.TryParse(v, out n) && n > 0) HoldSeconds = n; break;
                    case "longholdseconds": if (int.TryParse(v, out n) && n > 0) LongHoldSeconds = n; break;
                    case "longholdapps": LongHoldApps = v; break;
                    case "hotkey": Hotkey = v; break;
                }
            }
        }

        public void Save()
        {
            File.WriteAllLines(Path, new[]
            {
                "# WheelGuard settings. Easier to change them in the WheelGuard window (left-click the tray icon).",
                "enabled=" + (Enabled ? 1 : 0),
                "autoDisable=" + (AutoDisable ? 1 : 0),
                "# seconds Windows may see the wheel held before the wheel click is switched off",
                "holdSeconds=" + HoldSeconds,
                "# longer limit for apps where the wheel is held on purpose (comma-separated process names)",
                "longHoldSeconds=" + LongHoldSeconds,
                "longHoldApps=" + LongHoldApps,
                "# e.g. Ctrl+Alt+W, Ctrl+Shift+F12, Win+Alt+W",
                "hotkey=" + Hotkey,
                "# show the physical wheel-button state on the tray icon",
                "trayIndicator=" + (TrayIndicator ? 1 : 0),
            }, new UTF8Encoding(false));
        }
    }

    struct MiddleEvent
    {
        public bool Down, Blocked;
        public long Ticks;
        public DateTime Time;
    }

    // Raw-input listener on its own thread: sees the physical button without delaying anyone's input.
    sealed class RawListener : NativeWindow
    {
        const int WM_APP_SET = 0x8001;
        readonly Action<bool> onMiddle;
        readonly IntPtr buf = Marshal.AllocHGlobal(256);
        readonly uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
        bool registered;

        RawListener(Action<bool> onMiddle)
        {
            this.onMiddle = onMiddle;
            CreateHandle(new CreateParams());
        }

        public static RawListener Start(Action<bool> onMiddle)
        {
            RawListener listener = null;
            var ready = new ManualResetEvent(false);
            var thread = new Thread(() =>
            {
                listener = new RawListener(onMiddle);
                ready.Set();
                Application.Run();
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.WaitOne();
            return listener;
        }

        public void SetListening(bool on)
        {
            Native.PostMessage(Handle, WM_APP_SET, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APP_SET)
            {
                bool on = m.WParam != IntPtr.Zero;
                if (on != registered)
                {
                    var rid = new[] { new Native.RAWINPUTDEVICE {
                        UsagePage = 1, Usage = 2,
                        Flags = on ? Native.RIDEV_INPUTSINK : Native.RIDEV_REMOVE,
                        Target = on ? Handle : IntPtr.Zero } };
                    if (Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)))) registered = on;
                }
                return;
            }
            if (m.Msg == Native.WM_INPUT)
            {
                uint size = 256;
                if (Native.GetRawInputData(m.LParam, Native.RID_INPUT, buf, ref size, headerSize) != unchecked((uint)-1))
                {
                    int flags = Marshal.ReadInt16(buf, (int)headerSize + 4) & 0x30;   // RAWMOUSE.usButtonFlags, middle bits
                    if (flags != 0 && Marshal.ReadIntPtr(buf, 8) != IntPtr.Zero)       // real device, not injected
                    {
                        if ((flags & 0x10) != 0) onMiddle(true);
                        if ((flags & 0x20) != 0) onMiddle(false);
                    }
                }
            }
            base.WndProc(ref m);
        }
    }

    sealed class App : NativeWindow, IDisposable
    {
        const int HotkeyId = 1;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly string logPath;
        readonly Settings cfg;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem toggleItem, autoItem, indicatorItem, startupItem;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Dictionary<int, Icon> icons = new Dictionary<int, Icon>();
        readonly Native.HookProc hookProc;
        readonly ConcurrentQueue<MiddleEvent> events = new ConcurrentQueue<MiddleEvent>();
        readonly RawListener raw;
        readonly MainForm form;
        string[] longApps;
        string hotkeyText;

        IntPtr hook;
        volatile bool hookActive;
        bool tripped, rawListening, windowsHeld;
        int physState = -1;        // physical button: -1 unknown, 0 released, 1 pressed
        long lastEdgeTicks;
        int chatter, autoTrips;
        int heldSince;             // enabled mode: tick when Windows started reporting M held (0 = not held)
        int strayHeldSince;        // disabled mode: tick when a leftover held state was first seen
        long hookEvents, lastHookEvents, blocked;
        Native.POINT lastPos;
        int lastHealth, lastReinstall = -600000, lastReinstallLog = -600000;
        uint cachedPid;
        string cachedName = "";
        Icon shownIcon;
        string shownTip;

        public App()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            logPath = Path.Combine(dir, "WheelGuard.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024)
            {
                File.Delete(logPath + ".old");
                File.Move(logPath, logPath + ".old");
            }
            cfg = new Settings(Path.Combine(dir, "WheelGuard.ini"));
            ApplyAppList();
            hookProc = HookCallback;
            CreateHandle(new CreateParams());

            uint mods, vk;
            if (ParseHotkey(cfg.Hotkey, out mods, out vk) && Native.RegisterHotKey(Handle, HotkeyId, mods | Native.MOD_NOREPEAT, vk))
                hotkeyText = cfg.Hotkey;
            else
                Log("hotkey '" + cfg.Hotkey + "' could not be registered (invalid or used by another program)");

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Открыть окно", null, delegate { ShowWindow(); });
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            toggleItem = new ToolStripMenuItem("", null, delegate { Toggle(); });
            toggleItem.ShortcutKeyDisplayString = hotkeyText;
            autoItem = new ToolStripMenuItem("Автоотключение при залипании", null, delegate
            {
                cfg.AutoDisable = !cfg.AutoDisable;
                SettingsChanged();
            });
            indicatorItem = new ToolStripMenuItem("Показывать нажатие на значке", null, delegate { SetTrayIndicator(!cfg.TrayIndicator); });
            startupItem = new ToolStripMenuItem("Запускать вместе с Windows", null, delegate { SetStartup(!IsStartup()); });
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(toggleItem);
            menu.Items.Add(autoItem);
            menu.Items.Add(indicatorItem);
            menu.Items.Add("Отпустить колесо в Windows сейчас", null, delegate { ReleaseMiddle("вручную"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startupItem);
            menu.Items.Add("Открыть лог", null, delegate { OpenLog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { Application.Exit(); });
            menu.Opening += delegate { UpdateMenu(); };
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

            raw = RawListener.Start(OnRawMiddle);
            form = new MainForm(this);
            form.VisibleChanged += delegate { UpdateRawListening(); };

            ApplyHookState();
            UpdateRawListening();
            UpdateTray();
            tray.Visible = true;
            timer.Interval = 100;
            timer.Tick += Tick;
            timer.Start();
            Log("start: wheel click " + (cfg.Enabled ? "ON" : "OFF") + ", hotkey " + (hotkeyText ?? "none")
                + ", hold limit " + cfg.HoldSeconds + " s (" + cfg.LongHoldSeconds + " s in " + cfg.LongHoldApps + ")");
        }

        // ---- state exposed to the window ----
        public Settings Config { get { return cfg; } }
        public bool WheelEnabled { get { return cfg.Enabled; } }
        public bool Tripped { get { return tripped; } }
        public bool WindowsHeld { get { return windowsHeld; } }
        public int PhysState { get { return physState; } }
        public long Blocked { get { return blocked; } }
        public int Chatter { get { return chatter; } }
        public int AutoTrips { get { return autoTrips; } }
        public string HotkeyText { get { return hotkeyText; } }
        public Icon WindowIcon { get { return GetIcon(IconReleased); } }

        void Tick(object sender, EventArgs e)
        {
            DrainEvents();
            int now = Environment.TickCount;
            windowsHeld = Native.MiddleHeld();
            if (cfg.Enabled) CheckStuck(now);
            else
            {
                ClearStrayHold(now);
                CheckHookAlive(now);
            }
            UpdateTray();
            if (form.Visible) form.RefreshState();
        }

        void CheckStuck(int now)
        {
            if (!windowsHeld) { heldSince = 0; return; }
            if (heldSince == 0) { heldSince = now; return; }
            if (!cfg.AutoDisable) return;
            int limitMs = (IsLongHoldApp() ? cfg.LongHoldSeconds : cfg.HoldSeconds) * 1000;
            if (now - heldSince > limitMs)
            {
                tripped = true;
                autoTrips++;
                SetEnabled(false, "колесо зажато " + (now - heldSince) / 1000 + " с");
            }
        }

        // Disabled: nothing physical can hold M any more, so a held state is a leftover.
        void ClearStrayHold(int now)
        {
            if (!windowsHeld) { strayHeldSince = 0; return; }
            if (strayHeldSince == 0) strayHeldSince = now;
            else if (now - strayHeldSince > 500) { ReleaseMiddle("остаточное залипание"); strayHeldSince = 0; }
        }

        // Windows silently drops a low-level hook that ever answers too slowly.
        // If the cursor keeps moving but the hook sees nothing, install it again.
        void CheckHookAlive(int now)
        {
            if (now - lastHealth < 2000) return;
            lastHealth = now;
            Native.POINT p;
            Native.GetCursorPos(out p);
            bool moved = p.x != lastPos.x || p.y != lastPos.y;
            lastPos = p;
            if (moved && hookEvents == lastHookEvents && now - lastReinstall > 30000)
            {
                InstallHook();
                lastReinstall = now;
                if (now - lastReinstallLog > 600000) { Log("mouse hook reinstalled"); lastReinstallLog = now; }
            }
            lastHookEvents = hookEvents;
        }

        IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                hookEvents++;
                int msg = wParam.ToInt32();
                if ((msg == Native.WM_MBUTTONDOWN || msg == Native.WM_MBUTTONUP) && !cfg.Enabled)
                {
                    uint flags = (uint)Marshal.ReadInt32(lParam, 12);   // MSLLHOOKSTRUCT.flags
                    if ((flags & 1) == 0)                               // physical, not injected
                    {
                        bool down = msg == Native.WM_MBUTTONDOWN;
                        if (down) blocked++;
                        events.Enqueue(new MiddleEvent { Down = down, Blocked = true, Ticks = Stopwatch.GetTimestamp(), Time = DateTime.Now });
                        return (IntPtr)1;
                    }
                }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }

        // Raw thread. While the hook is active it reports the same presses, so raw is ignored then.
        void OnRawMiddle(bool down)
        {
            if (hookActive) return;
            events.Enqueue(new MiddleEvent { Down = down, Ticks = Stopwatch.GetTimestamp(), Time = DateTime.Now });
        }

        void DrainEvents()
        {
            MiddleEvent ev;
            while (events.TryDequeue(out ev))
            {
                int state = ev.Down ? 1 : 0;
                if (state == physState) continue;
                double dt = lastEdgeTicks == 0 ? -1 : (ev.Ticks - lastEdgeTicks) * 1000.0 / Stopwatch.Frequency;
                if (dt >= 0 && dt < 50) chatter++;
                lastEdgeTicks = ev.Ticks;
                physState = state;
                form.AddEvent(ev.Time.ToString("HH:mm:ss.fff") + "  " + (ev.Down ? "нажата " : "отпущена")
                    + (dt >= 0 ? string.Format("  +{0:F0} мс", dt) : "")
                    + (dt >= 0 && dt < 50 ? "  дребезг" : "")
                    + (ev.Blocked ? "  [заглушено]" : ""));
            }
        }

        public void Toggle()
        {
            tripped = false;
            SetEnabled(!cfg.Enabled, "вручную");
        }

        void SetEnabled(bool on, string reason)
        {
            cfg.Enabled = on;
            cfg.Save();
            if (on) tripped = false;
            ApplyHookState();
            if (!on && Native.MiddleHeld()) ReleaseMiddle(null);
            heldSince = 0;
            strayHeldSince = 0;
            Log("wheel click " + (on ? "ON" : "OFF") + " (" + reason + ")" + (on ? ", blocked presses while off: " + blocked : ""));
            if (on) blocked = 0;
            UpdateTray();
            if (form.Visible) form.RefreshState();

            string how = hotkeyText != null ? hotkeyText : "меню в трее";
            if (!on && tripped)
                tray.ShowBalloonTip(5000, "Колесо залипло",
                    "Клик колесом отключён (" + reason + ").\n" + how + " — включить обратно.", ToolTipIcon.Warning);
            else
                tray.ShowBalloonTip(1500, "WheelGuard", on ? "Клик колесом включён" : "Клик колесом выключен", ToolTipIcon.Info);
        }

        void ApplyHookState()
        {
            if (!cfg.Enabled && hook == IntPtr.Zero) InstallHook();
            else if (cfg.Enabled && hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
                hookActive = false;
                if (!rawListening) physState = -1;
            }
        }

        void InstallHook()
        {
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, hookProc, Native.GetModuleHandle(null), 0);
            hookActive = hook != IntPtr.Zero;
            if (!hookActive) Log("SetWindowsHookEx failed, error " + Marshal.GetLastWin32Error());
            // fresh baseline for CheckHookAlive, so a new hook is not judged dead right away
            Native.GetCursorPos(out lastPos);
            lastHookEvents = hookEvents;
            lastHealth = Environment.TickCount;
        }

        public void UpdateRawListening()
        {
            bool want = cfg.TrayIndicator || form.Visible;
            if (want == rawListening) return;
            rawListening = want;
            raw.SetListening(want);
            if (!want && !hookActive) physState = -1;
        }

        public void ReleaseMiddle(string why)
        {
            Native.mouse_event(Native.MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
            if (why != null) Log("released middle button in Windows (" + why + ")");
        }

        public void SetTrayIndicator(bool on)
        {
            cfg.TrayIndicator = on;
            SettingsChanged();
            UpdateRawListening();
        }

        public void SettingsChanged()
        {
            cfg.Save();
            ApplyAppList();
            UpdateTray();
        }

        void ApplyAppList()
        {
            longApps = cfg.LongHoldApps.ToLowerInvariant().Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // Returns null on success, otherwise a short reason.
        public string SetHotkey(string text)
        {
            uint mods, vk;
            if (!ParseHotkey(text, out mods, out vk)) return "не понял сочетание";
            Native.UnregisterHotKey(Handle, HotkeyId);
            if (!Native.RegisterHotKey(Handle, HotkeyId, mods | Native.MOD_NOREPEAT, vk))
            {
                uint oldMods, oldVk;
                if (hotkeyText != null && ParseHotkey(hotkeyText, out oldMods, out oldVk))
                    Native.RegisterHotKey(Handle, HotkeyId, oldMods | Native.MOD_NOREPEAT, oldVk);
                return "занято другой программой";
            }
            hotkeyText = text;
            cfg.Hotkey = text;
            cfg.Save();
            toggleItem.ShortcutKeyDisplayString = text;
            Log("hotkey " + text);
            return null;
        }

        bool IsLongHoldApp()
        {
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            if (pid != cachedPid)
            {
                cachedPid = pid;
                try { cachedName = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
                catch { cachedName = ""; }
            }
            foreach (string app in longApps)
                if (cachedName.Contains(app)) return true;
            return false;
        }

        void ShowWindow()
        {
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.RefreshState();
            form.Activate();
        }

        // Tray colour: grey = wheel click off; otherwise green = released, orange = pressed
        // (pressed/released only with the indicator on; unknown physical state falls back to Windows' view).
        void UpdateTray()
        {
            bool pressed = physState == 1 || (physState == -1 && windowsHeld);
            int look = !cfg.Enabled ? IconOff : (cfg.TrayIndicator && pressed) ? IconPressed : IconReleased;
            Icon icon = GetIcon(look);
            if (icon != shownIcon) { tray.Icon = icon; shownIcon = icon; }

            string tip = "WheelGuard — колесо " + (cfg.Enabled ? "ВКЛ" : (tripped ? "ВЫКЛ (залипало)" : "ВЫКЛ"));
            if (!cfg.Enabled && blocked > 0) tip += ", заглушено " + blocked;
            if (cfg.Enabled && cfg.TrayIndicator) tip += pressed ? " · НАЖАТА" : " · отпущена";
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tip != shownTip) { tray.Text = tip; shownTip = tip; }
        }

        void UpdateMenu()
        {
            toggleItem.Text = cfg.Enabled ? "Выключить клик колесом" : "Включить клик колесом";
            autoItem.Checked = cfg.AutoDisable;
            indicatorItem.Checked = cfg.TrayIndicator;
            startupItem.Checked = IsStartup();
        }

        const int IconReleased = 0, IconOff = 1, IconPressed = 2;

        Icon GetIcon(int look)
        {
            Icon icon;
            if (!icons.TryGetValue(look, out icon)) { icon = MakeIcon(look); icons[look] = icon; }
            return icon;
        }

        static Icon MakeIcon(int look)
        {
            Color body = look == IconReleased ? Color.FromArgb(46, 160, 67)
                       : look == IconPressed ? Color.FromArgb(245, 140, 0)
                       : Color.FromArgb(128, 128, 128);
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(body)) g.FillEllipse(b, 7, 1, 18, 30);
                    using (var w = new SolidBrush(Color.White)) g.FillRectangle(w, 14, 5, 4, 9);
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        public static bool IsStartup()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("WheelGuard") != null;
        }

        public void SetStartup(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("WheelGuard", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("WheelGuard", false);
            }
            Log("autostart " + (on ? "on" : "off"));
        }

        public void OpenLog()
        {
            try { Process.Start(logPath); } catch { }
        }

        static bool ParseHotkey(string s, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            foreach (string part in s.Split('+'))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= 2;
                else if (p == "alt") mods |= 1;
                else if (p == "shift") mods |= 4;
                else if (p == "win") mods |= 8;
                else
                {
                    Keys key;
                    if (!Enum.TryParse(part.Trim(), true, out key)) return false;
                    vk = (uint)key;
                }
            }
            return vk != 0;
        }

        void Log(string s)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId) Toggle();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            timer.Stop();
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            Native.UnregisterHotKey(Handle, HotkeyId);
            raw.SetListening(false);
            tray.Visible = false;
            tray.Dispose();
            Log("exit");
            DestroyHandle();
        }
    }

    sealed class MainForm : Form
    {
        static readonly Color Green = Color.FromArgb(30, 140, 60), Red = Color.FromArgb(200, 40, 40),
            Orange = Color.FromArgb(220, 120, 0), Grey = Color.FromArgb(110, 110, 110);

        readonly App app;
        readonly Label stateVal, physVal, winVal, countersVal, hotkeyStatus;
        readonly Button toggleBtn;
        readonly ListBox eventList;
        readonly CheckBox autoChk, trayChk, startupChk;
        readonly NumericUpDown holdNum, longNum;
        readonly TextBox appsBox, hotkeyBox;
        bool loading;

        public MainForm(App app)
        {
            this.app = app;
            Text = "WheelGuard";
            Icon = app.WindowIcon;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 566);

            var state = new GroupBox { Text = "Состояние", Location = new Point(10, 8), Size = new Size(420, 150) };
            AddLabel(state, "Клик колесом:", 12, 26);
            stateVal = AddLabel(state, "", 130, 24, true);
            toggleBtn = new Button { Location = new Point(300, 20), Size = new Size(108, 28) };
            toggleBtn.Click += delegate { app.Toggle(); };
            state.Controls.Add(toggleBtn);
            AddLabel(state, "Кнопка на мыши:", 12, 58);
            physVal = AddLabel(state, "", 130, 56, true);
            AddLabel(state, "Windows видит:", 12, 88);
            winVal = AddLabel(state, "", 130, 86, true);
            countersVal = AddLabel(state, "", 12, 120);
            countersVal.ForeColor = Grey;
            Controls.Add(state);

            AddLabel(this, "Последние нажатия колеса (физически, с интервалами):", 12, 166);
            eventList = new ListBox
            {
                Location = new Point(10, 186), Size = new Size(420, 124),
                Font = new Font("Consolas", 9f), IntegralHeight = false
            };
            Controls.Add(eventList);

            var settings = new GroupBox { Text = "Настройки", Location = new Point(10, 318), Size = new Size(420, 196) };
            autoChk = new CheckBox { Text = "Автоотключение, если колесо зажато дольше", Location = new Point(12, 24), AutoSize = true };
            autoChk.CheckedChanged += delegate { if (loading) return; app.Config.AutoDisable = autoChk.Checked; app.SettingsChanged(); };
            settings.Controls.Add(autoChk);
            holdNum = AddNumber(settings, 318, 22, 1, 600);
            holdNum.ValueChanged += delegate { if (loading) return; app.Config.HoldSeconds = (int)holdNum.Value; app.SettingsChanged(); };
            AddLabel(settings, "с", 376, 25);

            AddLabel(settings, "в программах", 30, 56);
            appsBox = new TextBox { Location = new Point(128, 52), Size = new Size(176, 23) };
            appsBox.Leave += delegate { SaveApps(); };
            appsBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { SaveApps(); e.SuppressKeyPress = true; } };
            settings.Controls.Add(appsBox);
            longNum = AddNumber(settings, 318, 52, 1, 3600);
            longNum.ValueChanged += delegate { if (loading) return; app.Config.LongHoldSeconds = (int)longNum.Value; app.SettingsChanged(); };
            AddLabel(settings, "с", 376, 55);

            AddLabel(settings, "Хоткей вкл/выкл:", 12, 90);
            hotkeyBox = new TextBox { Location = new Point(128, 86), Size = new Size(176, 23) };
            settings.Controls.Add(hotkeyBox);
            var applyBtn = new Button { Text = "Применить", Location = new Point(312, 84), Size = new Size(94, 27) };
            applyBtn.Click += delegate { ApplyHotkey(); };
            settings.Controls.Add(applyBtn);
            hotkeyStatus = AddLabel(settings, "", 128, 113);
            hotkeyStatus.ForeColor = Grey;

            trayChk = new CheckBox { Text = "Показывать нажатие колеса на значке в трее", Location = new Point(12, 138), AutoSize = true };
            trayChk.CheckedChanged += delegate { if (!loading) app.SetTrayIndicator(trayChk.Checked); };
            settings.Controls.Add(trayChk);
            startupChk = new CheckBox { Text = "Запускать вместе с Windows", Location = new Point(12, 164), AutoSize = true };
            startupChk.CheckedChanged += delegate { if (!loading) app.SetStartup(startupChk.Checked); };
            settings.Controls.Add(startupChk);
            Controls.Add(settings);

            var releaseBtn = new Button { Text = "Отпустить колесо в Windows", Location = new Point(10, 526), Size = new Size(210, 30) };
            releaseBtn.Click += delegate { app.ReleaseMiddle("вручную из окна"); };
            var logBtn = new Button { Text = "Лог", Location = new Point(228, 526), Size = new Size(90, 30) };
            logBtn.Click += delegate { app.OpenLog(); };
            var exitBtn = new Button { Text = "Выход", Location = new Point(326, 526), Size = new Size(104, 30) };
            exitBtn.Click += delegate { Application.Exit(); };
            Controls.AddRange(new Control[] { releaseBtn, logBtn, exitBtn });

            VisibleChanged += delegate { if (Visible) LoadSettings(); };
        }

        static Label AddLabel(Control parent, string text, int x, int y, bool bold = false)
        {
            var l = new Label { Text = text, Location = new Point(x, y), AutoSize = true };
            if (bold) l.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            parent.Controls.Add(l);
            return l;
        }

        static NumericUpDown AddNumber(Control parent, int x, int y, int min, int max)
        {
            var n = new NumericUpDown { Location = new Point(x, y), Size = new Size(54, 23), Minimum = min, Maximum = max };
            parent.Controls.Add(n);
            return n;
        }

        void LoadSettings()
        {
            loading = true;
            Settings c = app.Config;
            autoChk.Checked = c.AutoDisable;
            holdNum.Value = Math.Max(holdNum.Minimum, Math.Min(holdNum.Maximum, c.HoldSeconds));
            longNum.Value = Math.Max(longNum.Minimum, Math.Min(longNum.Maximum, c.LongHoldSeconds));
            appsBox.Text = c.LongHoldApps;
            hotkeyBox.Text = app.HotkeyText ?? c.Hotkey;
            hotkeyStatus.Text = app.HotkeyText != null ? "работает" : "не зарегистрирован — выбери другое сочетание";
            trayChk.Checked = c.TrayIndicator;
            startupChk.Checked = App.IsStartup();
            loading = false;
        }

        void SaveApps()
        {
            if (loading || appsBox.Text.Trim() == app.Config.LongHoldApps) return;
            app.Config.LongHoldApps = appsBox.Text.Trim();
            app.SettingsChanged();
        }

        void ApplyHotkey()
        {
            string error = app.SetHotkey(hotkeyBox.Text.Trim());
            hotkeyStatus.Text = error == null ? "работает" : "не применено: " + error;
            hotkeyStatus.ForeColor = error == null ? Grey : Red;
        }

        public void AddEvent(string line)
        {
            eventList.Items.Insert(0, line);
            while (eventList.Items.Count > 60) eventList.Items.RemoveAt(eventList.Items.Count - 1);
        }

        public void RefreshState()
        {
            if (app.WheelEnabled) { stateVal.Text = "ВКЛ"; stateVal.ForeColor = Green; toggleBtn.Text = "Выключить"; }
            else
            {
                stateVal.Text = app.Tripped ? "ВЫКЛ (залипало)" : "ВЫКЛ";
                stateVal.ForeColor = app.Tripped ? Red : Grey;
                toggleBtn.Text = "Включить";
            }

            int p = app.PhysState;
            physVal.Text = p == 1 ? "● НАЖАТА" : p == 0 ? "○ отпущена" : "? неизвестно — нажми колесо";
            physVal.ForeColor = p == 1 ? Orange : p == 0 ? Green : Grey;

            winVal.Text = app.WindowsHeld ? "зажата" : "отпущена";
            winVal.ForeColor = app.WindowsHeld ? Red : Green;

            countersVal.Text = "Заглушено нажатий: " + app.Blocked + "    Дребезг (<50 мс): " + app.Chatter + "    Автоотключений: " + app.AutoTrips;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\WheelGuardSingleton", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (new App()) Application.Run();
            }
        }
    }
}
