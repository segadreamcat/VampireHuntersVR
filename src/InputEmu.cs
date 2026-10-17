using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VampireHuntersVR
{
    // Turns VR controller input into real keyboard/mouse events for the game window.
    // Works in every scene (hub, menus, runs) because the game already reads keyboard + mouse.
    internal static class InputEmu
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        const uint KEYUP = 0x2, EXTENDED = 0x1;
        const uint M_MOVE = 0x1, M_LDOWN = 0x2, M_LUP = 0x4, M_RDOWN = 0x8, M_RUP = 0x10;

        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        static readonly uint Pid = GetCurrentProcessId();
        static readonly Dictionary<string, bool> down = new Dictionary<string, bool>();
        static bool warnedFocus;
        static float mouseAccumX;

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        delegate bool EnumProc(IntPtr h, IntPtr lp);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        static readonly EnumProc enumCb = FindOwn;
        static IntPtr ownWindow;
        static float lastFocusTry = -10f;

        static bool FindOwn(IntPtr h, IntPtr lp)
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == Pid && IsWindowVisible(h)) { ownWindow = h; return false; }
            return true;
        }

        // Bring the game window to the front (Windows only allows this with a few tricks).
        public static void TryFocus(float now, string why)
        {
            if (now - lastFocusTry < 1.5f) return;
            lastFocusTry = now;
            try
            {
                if (ownWindow == IntPtr.Zero) EnumWindows(enumCb, IntPtr.Zero);
                if (ownWindow == IntPtr.Zero) return;
                if (IsIconic(ownWindow)) ShowWindow(ownWindow, 9); // SW_RESTORE
                var fg = GetForegroundWindow();
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
                uint me = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
                // A synthetic Alt tap lifts Windows' foreground lock.
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, KEYUP, UIntPtr.Zero);
                BringWindowToTop(ownWindow);
                bool ok = SetForegroundWindow(ownWindow);
                if (attached) AttachThreadInput(me, fgThread, false);
                Plugin.L.LogInfo($"Auto-focus game window ({why}): {(ok ? "ok" : "blocked by Windows")}");
            }
            catch (Exception e) { Plugin.L.LogWarning("focus: " + e.Message); }
        }

        public static bool GameFocused()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out uint pid);
            bool f = pid == Pid;
            if (!f && !warnedFocus) { warnedFocus = true; Plugin.L.LogWarning("Game window not focused; will auto-focus when you use the controllers."); }
            if (f) warnedFocus = false;
            return f;
        }

        // name: key name (W, Space, LeftShift, Escape, Tab, E, R, F1...) or Mouse0 / Mouse1.
        // Batch mode: several controls may share one key (e.g. both grips = C); the key is held if ANY wants it.
        static Dictionary<string, bool> batch;
        public static void Begin() { batch = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); }
        public static void End()
        {
            var b = batch; batch = null;
            if (b != null) foreach (var kv in b) Set(kv.Key, kv.Value);
        }

        public static void Set(string name, bool pressed)
        {
            if (batch != null)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                batch.TryGetValue(name, out bool cur);
                batch[name] = cur || pressed;
                return;
            }
            if (string.IsNullOrWhiteSpace(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
            down.TryGetValue(name, out bool was);
            if (was == pressed) return;
            down[name] = pressed;
            if (name.Equals("Mouse0", StringComparison.OrdinalIgnoreCase)) { mouse_event(pressed ? M_LDOWN : M_LUP, 0, 0, 0, UIntPtr.Zero); return; }
            if (name.Equals("Mouse1", StringComparison.OrdinalIgnoreCase)) { mouse_event(pressed ? M_RDOWN : M_RUP, 0, 0, 0, UIntPtr.Zero); return; }
            if (!TryVk(name, out byte vk, out bool ext)) { Plugin.L.LogWarning("Unknown key name in config: " + name); down[name] = false; return; }
            byte scan = (byte)MapVirtualKey(vk, 0);
            uint flags = (pressed ? 0 : KEYUP) | (ext ? EXTENDED : 0);
            keybd_event(vk, scan, flags, UIntPtr.Zero);
        }

        public static void Wheel(int delta) => mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);

        public static void MouseMove(float dx)
        {
            mouseAccumX += dx;
            int ix = (int)mouseAccumX;
            if (ix == 0) return;
            mouseAccumX -= ix;
            mouse_event(M_MOVE, ix, 0, 0, UIntPtr.Zero);
        }

        public static void ReleaseAll()
        {
            foreach (var k in new List<string>(down.Keys)) if (down[k]) Set(k, false);
        }

        static bool TryVk(string n, out byte vk, out bool ext)
        {
            ext = false; vk = 0;
            n = n.Trim();
            if (n.Length == 1)
            {
                char c = char.ToUpperInvariant(n[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = (byte)c; return true; }
            }
            if (n.Length >= 2 && (n[0] == 'F' || n[0] == 'f') && int.TryParse(n.Substring(1), out int fn) && fn >= 1 && fn <= 12) { vk = (byte)(0x6F + fn); return true; }
            switch (n.ToLowerInvariant())
            {
                case "space": vk = 0x20; return true;
                case "enter": case "return": vk = 0x0D; return true;
                case "escape": case "esc": vk = 0x1B; return true;
                case "tab": vk = 0x09; return true;
                case "backspace": vk = 0x08; return true;
                case "leftshift": case "shift": vk = 0xA0; return true;
                case "rightshift": vk = 0xA1; return true;
                case "leftcontrol": case "leftctrl": case "ctrl": vk = 0xA2; return true;
                case "leftalt": case "alt": vk = 0xA4; return true;
                case "up": vk = 0x26; ext = true; return true;
                case "down": vk = 0x28; ext = true; return true;
                case "left": vk = 0x25; ext = true; return true;
                case "right": vk = 0x27; ext = true; return true;
            }
            return false;
        }
    }
}
