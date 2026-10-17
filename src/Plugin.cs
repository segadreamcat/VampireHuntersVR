using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace VampireHuntersVR
{
    [BepInPlugin(Guid, "Vampire Hunters VR", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "local.vampirehunters.vr";
        public const string Version = "0.6.0";

        internal static ManualLogSource L;
        internal static Plugin Instance;
        internal static string PluginDir;

        // Rendering
        internal static ConfigEntry<bool> StartInVR;
        internal static ConfigEntry<float> ResolutionScale;
        internal static ConfigEntry<bool> FlipEyeY;
        internal static ConfigEntry<bool> MirrorToDesktop;
        internal static ConfigEntry<bool> PostProcessing;
        internal static ConfigEntry<bool> DebugSnapshots;
        // Tracking / comfort
        internal static ConfigEntry<float> WorldUnitsPerMeter;
        internal static ConfigEntry<string> TurnMode;
        internal static ConfigEntry<float> SnapTurnDegrees;
        internal static ConfigEntry<float> SmoothTurnSpeed;
        internal static ConfigEntry<string> MoveRelativeTo;
        // Hands / aim
        internal static ConfigEntry<bool> HandTracking;
        internal static ConfigEntry<bool> HandAim;
        internal static ConfigEntry<float> HandPitchOffset;
        internal static ConfigEntry<float> GripOffsetX, GripOffsetY, GripOffsetZ;
        internal static ConfigEntry<float> LeftGunOffsetX, LeftGunOffsetY, LeftGunOffsetZ;
        internal static ConfigEntry<string> MenuMode;
        internal static ConfigEntry<bool> ControllerInput;
        internal static ConfigEntry<string> InputMode;
        internal static ConfigEntry<bool> AutoFocus, HapticOnlyWhenTrigger;
        internal static ConfigEntry<int> StickAxis;
        internal static ConfigEntry<string> KeyForward, KeyBack, KeyLeft, KeyRight, KeyRightTrigger, KeyLeftTrigger,
            KeyA, KeyB, KeyX, KeyY, KeyLeftGrip, KeyRightGrip, KeyLeftStickClick, KeyRightStickClick;
        internal static ConfigEntry<float> SnapTurnMousePixels, SmoothTurnMousePixels;
        // HUD
        internal static ConfigEntry<bool> HudInVR;
        internal static ConfigEntry<float> HudDistance;
        internal static ConfigEntry<float> HudWidth;
        internal static ConfigEntry<float> MenuWidth, MenuDistance;
        internal static ConfigEntry<bool> LockMenus, FlatScreenMenus, FlipMenuScreen, FlatIntro;
        internal static ConfigEntry<float> HapticStrength, HapticFrames, HapticMinGap, NearClip, MenuPointerSmoothing, CrosshairSize;
        internal static ConfigEntry<float> HudFollowSpeed;
        internal static ConfigEntry<float> HudHeightOffset;
        internal static ConfigEntry<string> HudHideNames;

        public override void Load()
        {
            Instance = this;
            L = Log;
            PluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);

            StartInVR = Config.Bind("General", "StartInVR", true, "Start VR automatically. F8 toggles VR at any time.");
            ResolutionScale = Config.Bind("Rendering", "ResolutionScale", 0.8f, "Fraction of SteamVR recommended per-eye resolution (0.3 to 1.5). Lower = faster.");
            FlipEyeY = Config.Bind("Rendering", "FlipEyeY", true, "Flip eye images vertically. Toggle with F7 if the headset image is upside down.");
            PostProcessing = Config.Bind("Rendering", "PostProcessing", false, "Game screen effects (bloom, color grading, lens distortion, motion blur). Off by default: several warp or smear in VR.");
            DebugSnapshots = Config.Bind("Debug", "Snapshots", false, "Save left-eye image to BepInEx/vhvr_eye_N.png every 10 s for troubleshooting.");
            MirrorToDesktop = Config.Bind("Rendering", "MirrorToDesktop", false, "Keep rendering the normal game view on the monitor (costs performance).");

            WorldUnitsPerMeter = Config.Bind("Tracking", "WorldUnitsPerMeter", 1f, "Game units per real meter. Raise if the world feels tiny, lower if it feels huge.");
            TurnMode = Config.Bind("Comfort", "TurnMode", "Smooth", "Right stick turning: Snap, Smooth, or Off. Mouse look still works.");
            SnapTurnDegrees = Config.Bind("Comfort", "SnapTurnDegrees", 30f, "Degrees per snap turn.");
            SmoothTurnSpeed = Config.Bind("Comfort", "SmoothTurnSpeed", 140f, "Degrees per second for smooth turn.");
            MoveRelativeTo = Config.Bind("Comfort", "MoveRelativeTo", "Head", "Forward direction for movement: Head, LeftHand, or Body.");

            HandTracking = Config.Bind("Hands", "HandTracking", true, "Attach the in-game hands/guns to your VR controllers.");
            HandAim = Config.Bind("Hands", "HandAim", true, "Guns shoot where the controller points. False = shoot where you look.");
            HandPitchOffset = Config.Bind("Hands", "HandPitchOffset", 35f, "Degrees to tilt guns down relative to controller pose. Tune until gun feels natural.");
            GripOffsetX = Config.Bind("Hands", "GripOffsetX", 0f, "Gun offset from controller in meters (controller space, X right).");
            GripOffsetY = Config.Bind("Hands", "GripOffsetY", -0.02f, "Gun offset from controller in meters (Y up).");
            GripOffsetZ = Config.Bind("Hands", "GripOffsetZ", 0.04f, "Gun offset from controller in meters (Z forward).");
            LeftGunOffsetX = Config.Bind("Hands", "LeftGunOffsetX", 0f, "Extra left-gun offset in meters (X right).");
            LeftGunOffsetY = Config.Bind("Hands", "LeftGunOffsetY", 0f, "Extra left-gun offset in meters (Y up).");
            LeftGunOffsetZ = Config.Bind("Hands", "LeftGunOffsetZ", 0f, "Extra left-gun offset in meters (Z forward). Negative pulls the left gun back toward your hand.");
            MenuMode = Config.Bind("Input", "MenuMode", "Both", "Menus: Both = laser point-and-click plus right stick highlight. Laser = laser only. Stick = stick only.");
            ControllerInput = Config.Bind("Input", "ControllerInput", true, "Map VR controller buttons/sticks to game actions.");
            InputMode = Config.Bind("Input", "InputMode", "Keyboard", "Keyboard = controllers press real keys/mouse (works everywhere, game window must be focused). Direct = write into game input (runs only).");
            HapticOnlyWhenTrigger = Config.Bind("Input", "HapticOnlyWhenTrigger", true, "Rumble only while you hold a trigger (auto-shoot otherwise buzzes nonstop).");
            StickAxis = Config.Bind("Input", "StickAxis", 0, "Controller axis used as thumbstick (0 for Quest/Index/most). Do not use 2: on Quest that is the grip.");
            AutoFocus = Config.Bind("Input", "AutoFocus", true, "Bring the game window to the front when VR starts and when you use the controllers while another window is active.");
            const string kd = "Key or mouse button pressed by this control. Names: A-Z, 0-9, F1-F12, Space, Enter, Escape, Tab, LeftShift, LeftControl, LeftAlt, Up/Down/Left/Right, Mouse0 (left click), Mouse1 (right click), None.";
            KeyForward = Config.Bind("Keys", "LeftStickUp", "W", kd);
            KeyBack = Config.Bind("Keys", "LeftStickDown", "S", kd);
            KeyLeft = Config.Bind("Keys", "LeftStickLeft", "A", kd);
            KeyRight = Config.Bind("Keys", "LeftStickRight", "D", kd);
            KeyRightTrigger = Config.Bind("Keys", "RightTrigger", "Mouse0", kd);
            KeyLeftTrigger = Config.Bind("Keys", "LeftTrigger", "Mouse1", kd);
            KeyA = Config.Bind("Keys", "AButton", "Space", kd);
            KeyB = Config.Bind("Keys", "BButton", "R", kd);
            KeyX = Config.Bind("Keys", "XButton", "Z", kd);
            KeyY = Config.Bind("Keys", "YButton", "Escape", kd);
            KeyLeftGrip = Config.Bind("Keys", "LeftGrip", "C", kd);
            KeyRightGrip = Config.Bind("Keys", "RightGrip", "C", kd);
            KeyLeftStickClick = Config.Bind("Keys", "LeftStickClick", "LeftShift", kd);
            KeyRightStickClick = Config.Bind("Keys", "RightStickClick", "T", kd);
            SnapTurnMousePixels = Config.Bind("Comfort", "SnapTurnMousePixels", 150f, "Keyboard mode: mouse pixels per snap turn. Raise for bigger turns.");
            SmoothTurnMousePixels = Config.Bind("Comfort", "SmoothTurnMousePixels", 2500f, "Keyboard mode: mouse pixels per second at full stick for smooth turn.");

            HudInVR = Config.Bind("HUD", "HudInVR", true, "Show screen UI (HUD, menus, level-up) on a floating panel in VR.");
            HudDistance = Config.Bind("HUD", "HudDistance", 1.1f, "HUD panel distance in meters.");
            HudWidth = Config.Bind("HUD", "HudWidth", 1.2f, "HUD panel width in meters.");
            MenuWidth = Config.Bind("HUD", "MenuWidth", 1.9f, "Panel width in meters while a menu (mouse cursor) is shown.");
            MenuDistance = Config.Bind("HUD", "MenuDistance", 1.4f, "Menu panel distance in meters.");
            LockMenus = Config.Bind("HUD", "LockMenus", true, "Menus stay in place when you move your head (re-centered each time a menu opens, or with F6).");
            FlatScreenMenus = Config.Bind("HUD", "FlatScreenMenus", true, "Show menus exactly like the flat game on a big virtual screen. Laser moves the mouse, trigger clicks.");
            FlatIntro = Config.Bind("HUD", "FlatIntro", true, "Show the logo intro on a flat floating screen; full VR starts at the main menu.");
            FlipMenuScreen = Config.Bind("HUD", "FlipMenuScreen", true, "Flip the menu screen image vertically (toggle if menus appear upside down).");
            HapticStrength = Config.Bind("Input", "HapticStrength", 0.4f, "Controller rumble per shot, 0 = off, 1 = strongest.");
            HapticFrames = Config.Bind("Input", "HapticFrames", 1f, "How many frames each shot rumbles (1 = short tap).");
            HapticMinGap = Config.Bind("Input", "HapticMinGap", 0.07f, "Minimum seconds between rumble taps on one hand (keeps rapid fire pulsing).");
            NearClip = Config.Bind("Rendering", "NearClip", 0.12f, "Hide anything closer than this to your eyes, in meters (stops the arm model poking into your face).");
            MenuPointerSmoothing = Config.Bind("HUD", "MenuPointerSmoothing", 0.6f, "0 = raw laser, up to 0.9 = very steady menu cursor.");
            CrosshairSize = Config.Bind("HUD", "CrosshairSize", 0.6f, "Crosshair size multiplier (1 = same size as the flat game).");
            HudFollowSpeed = Config.Bind("HUD", "HudFollowSpeed", 4f, "How fast the HUD follows your head. 0 = locked to head.");
            HudHideNames = Config.Bind("HUD", "HideFullScreenNamed", "Shadow,Vignette,Damage,Hit,Hurt,Blood,Flash,Pain", "Comma list: full-screen HUD images whose name contains any of these are hidden in VR (flat-screen effects like hit flashes).");
            HudHeightOffset = Config.Bind("HUD", "HudHeightOffset", -0.05f, "HUD vertical offset in meters.");

            try
            {
                var dll = Path.Combine(PluginDir, "openvr_api.dll");
                if (!File.Exists(dll)) { L.LogError("openvr_api.dll missing next to VampireHuntersVR.dll"); return; }
                var handle = NativeLibrary.Load(dll);
                NativeLibrary.SetDllImportResolver(typeof(Valve.VR.OpenVR).Assembly, (name, asm, path) =>
                    name.StartsWith("openvr_api", StringComparison.OrdinalIgnoreCase) ? handle : IntPtr.Zero);
            }
            catch (Exception e) { L.LogError("Failed to load openvr_api.dll: " + e); return; }

            ClassInjector.RegisterTypeInIl2Cpp<VrDriver>();
            AddComponent<VrDriver>();
            // Hooks are applied later by VrDriver, once the main menu has loaded (see VrDriver.TryApplyHooks).
            L.LogInfo($"Vampire Hunters VR {Version} loaded. F8 toggle VR, F6 recenter, F7 flip eyes. Requires SteamVR + DX11 (-force-d3d11).");
        }
    }
}
