using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Valve.VR;
using static VampireHuntersVR.VMath;

namespace VampireHuntersVR
{
    public class VrDriver : MonoBehaviour
    {
        public VrDriver(IntPtr ptr) : base(ptr) { }

        internal static VrDriver Instance;

        // ---------- OpenVR ----------
        CVRSystem sys;
        bool vrOn;
        readonly TrackedDevicePose_t[] renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        readonly TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[0];
        uint eyeW, eyeH;
        readonly float[][] raw = { new float[4], new float[4] };
        readonly Vector3[] eyeOffset = new Vector3[2];
        readonly Quaternion[] eyeRotOffset = new Quaternion[2];

        // Render-thread shared state
        static CVRCompositor sComp;
        static CVRSystem sSys;
        // All per-frame OpenVR calls run on Unity's render thread (like Valve's Unity plugin).
        // Mixing threads made SteamVR receive duplicate eye submits and crash the game.
        static readonly object sCallLock = new object(), sDataLock = new object();
        static readonly TrackedDevicePose_t[] rtPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        static readonly TrackedDevicePose_t[] rtNoGame = new TrackedDevicePose_t[0];
        static readonly TrackedDevicePose_t[] sPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        static readonly uint[] sRole = { OpenVR.k_unTrackedDeviceIndexInvalid, OpenVR.k_unTrackedDeviceIndexInvalid };
        static readonly VRControllerState_t[] sCtrl = new VRControllerState_t[2];
        static readonly bool[] sCtrlOk = new bool[2];
        static bool sWaited;
        static readonly int[] sHapticFrames = new int[2];
        // Intro (logo) shown on a SteamVR overlay screen until the main menu loads.
        static CVROverlay sOverlay; static ulong sOvHandle; static IntPtr sIntroTex; static bool sIntroPlaced;
        static bool sIntroFlip = true;
        static ushort sHapticUs = 3000;
        // Head pose each frame was rendered with, so SteamVR reprojects from the right pose.
        static readonly HmdMatrix34_t[] sSlotPose = new HmdMatrix34_t[4];
        static readonly bool[] sSlotValid = new bool[4];
        int renderSlot;
        static EColorSpace sColor = EColorSpace.Gamma;
        static int sPoseFrame;
        static readonly IntPtr[] sTex = new IntPtr[2];
        static VRTextureBounds_t sBounds;
        static volatile int sSubmitError;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RenderEventFn(int id);
        static readonly RenderEventFn sRenderDel = OnRenderEvent;
        static readonly IntPtr sRenderFn = Marshal.GetFunctionPointerForDelegate(sRenderDel);
        bool frameReady;
        int loggedSubmitError;

        // ---------- Cameras ----------
        Camera src;
        IntPtr srcPtr;
        readonly Camera[] eyes = new Camera[2];
        readonly Camera[] hudCams = new Camera[2];
        readonly List<Camera>[] overlayClones = { new List<Camera>(), new List<Camera>() };
        readonly RenderTexture[] eyeRT = new RenderTexture[2];
        readonly Texture2D[] eyeCopy = new Texture2D[2];
        // Submit strategies tried in order until SteamVR accepts frames.
        static readonly (bool copy, EColorSpace cs, string name)[] Modes =
        {
            (true, EColorSpace.Gamma, "copy/gamma"), (true, EColorSpace.Auto, "copy/auto"),
            (false, EColorSpace.Gamma, "direct/gamma"), (false, EColorSpace.Linear, "direct/linear"),
            (false, EColorSpace.Auto, "direct/auto"), (true, EColorSpace.Linear, "copy/linear"),
        };
        int mode; float nextModeCheck; bool modeConfirmed;
        static int sOk, sErr;
        readonly List<(Camera cam, int mask)> mutedCams = new List<(Camera, int)>();
        Camera pointerCam;
        int hudLayer = 31;

        // ---------- Tracking ----------
        bool hmdValid;
        Vector3 hmdPos; Quaternion hmdRot = Identity;
        readonly bool[] ctrlValid = new bool[2];
        readonly Vector3[] ctrlPos = new Vector3[2];
        readonly Quaternion[] ctrlRot = { Identity, Identity };
        Vector3 origin; float originYaw; bool needRecenter = true;
        float extraYaw; bool snapLatch;
        internal Vector3 headPos; internal Quaternion headRot = Identity;
        internal readonly Vector3[] handPos = new Vector3[2];
        internal readonly Quaternion[] handRot = { Identity, Identity };
        internal readonly bool[] handValid = new bool[2];
        float bodyYaw;
        int lastFrame = -1;

        // ---------- Game ----------
        global::PlayerController pc;
        IntPtr pcPtr;
        readonly Dictionary<IntPtr, int> weaponSlot = new Dictionary<IntPtr, int>();

        // ---------- HUD ----------
        class CanvasRec
        {
            public Canvas c; public RenderMode mode; public Camera cam; public float plane;
            public Behaviour scaler; public bool scalerOn; public Vector3 scale; public Vector2 size;
            public Vector2 aMin, aMax, pivot, sizeDelta;
            public readonly List<(Transform t, int layer)> layers = new List<(Transform, int)>();
            public readonly HashSet<IntPtr> seen = new HashSet<IntPtr>();
            public readonly HashSet<IntPtr> hidden = new HashSet<IntPtr>();
            public readonly List<UnityEngine.UI.Graphic> hiddenList = new List<UnityEngine.UI.Graphic>();
            public readonly HashSet<IntPtr> unblocked = new HashSet<IntPtr>();
            public List<(Transform t, Vector3 scale)> crosshairs;
            public List<Transform> rects;
            public readonly List<UnityEngine.UI.Graphic> unblockedList = new List<UnityEngine.UI.Graphic>();
        }
        readonly List<CanvasRec> canvases = new List<CanvasRec>();
        float nextScan, nextPcSearch;
        Vector3 hudPos; Quaternion hudRot = Identity; bool hudInit;

        // ---------- Input ----------
        enum B { RTrig, LTrig, RA, RB, LA, LB, LGrip, RGrip, LStick, RStick, Count }
        readonly bool[] btn = new bool[(int)B.Count], prevBtn = new bool[(int)B.Count];
        readonly bool[] pendDown = new bool[(int)B.Count], pendUp = new bool[(int)B.Count];
        Vector2 lStick, rStick;
        readonly List<Action> clearNext = new List<Action>();
        bool injMove, injNav, injFireHold, injFire2Hold, injRun;
        Vector2 lastRawMove, lastWrittenMove; bool haveWritten;

        // ---------- Aim swap ----------
        int aimDepth; bool aimSwapped;
        Transform aimCam, aimCam2;
        Vector3 savedCamPos, savedCam2Pos; Quaternion savedCamRot, savedCam2Rot;
        readonly Transform[] savedHand = new Transform[2];
        readonly Vector3[] savedHandPos = new Vector3[2];
        readonly Quaternion[] savedHandRot = new Quaternion[2];

        UnityEngine.Events.UnityAction beforeRender;
        Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>> lateRender;
        int lateFrame = -1;

        // Runs after every game script, right before cameras render: final say on gun/hand placement.
        void OnBeginContext(ScriptableRenderContext ctx, Il2CppSystem.Collections.Generic.List<Camera> cams)
        {
            if (!vrOn || pc == null || Time.frameCount == lateFrame) return;
            lateFrame = Time.frameCount;
            try
            {
                float scale = Mathf.Max(0.01f, Plugin.WorldUnitsPerMeter.Value);
                if (Plugin.HandTracking.Value) PlaceHands(scale);
                FlattenMenus();
            }
            catch (Exception e) { Plugin.L.LogWarning("late place: " + e.Message); lateFrame = int.MaxValue - 1; }
        }
        bool beforeRenderHooked;
        bool autoStarted;

        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

        void Awake()
        {
            Instance = this;
            try
            {
                beforeRender = (Action)OnBeforeRender;
                Application.add_onBeforeRender(beforeRender);
                beforeRenderHooked = true;
            }
            catch (Exception e) { Plugin.L.LogWarning("onBeforeRender hook failed, using LateUpdate fallback: " + e.Message); }
            this.StartCoroutine(EndOfFrameLoop());
            try
            {
                lateRender = (Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>)
                    (Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>)OnBeginContext;
                RenderPipelineManager.add_beginContextRendering(lateRender);
                Plugin.L.LogInfo("Late (pre-render) hook active.");
            }
            catch (Exception e) { Plugin.L.LogWarning("pre-render hook failed: " + e.Message); }
        }

        // =====================================================================
        // Unity loop
        // =====================================================================
        void Update()
        {
            try
            {
                var input = UnityInput.Current;
                if (input.GetKeyDown(KeyCode.F8)) { if (vrOn) StopVr(); else StartVr(); }
                if (input.GetKeyDown(KeyCode.F6)) needRecenter = true;
                if (input.GetKeyDown(KeyCode.F7)) { Plugin.FlipEyeY.Value = !Plugin.FlipEyeY.Value; UpdateBounds(); }

                TryApplyHooks();
                if (!autoStarted && Time.frameCount > 30)
                {
                    autoStarted = true;
                    if (Plugin.StartInVR.Value) StartVr();
                }
                if (!vrOn) return;

                if (sSubmitError != 0 && sSubmitError != loggedSubmitError)
                {
                    loggedSubmitError = sSubmitError;
                    Plugin.L.LogWarning("SteamVR submit error: " + (EVRCompositorError)sSubmitError);
                }

                CheckSubmitMode();
                Snapshot();
                PollControllers();
                HandleUiClicks();
                StickNavigate();
                EmulateKeys();
                WatchAfterClick();
                DumpBindingsOnce();

                bool flat = FlatMenu;
                if (flat != flatActive) SetFlatMenu(flat);
                if (Plugin.HudInVR.Value && !flat && !introActive && Time.unscaledTime >= nextScan)
                {
                    nextScan = Time.unscaledTime + 0.5f;
                    ScanCanvases();
                }
            }
            catch (Exception e) { Fail(e); }
        }

        bool hooksApplied;
        // Delay game hooks until the menu scene is up, so game classes initialize in their normal order.
        void TryApplyHooks()
        {
            if (hooksApplied) return;
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
            if (!(scene.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0 && Time.unscaledTime > 3f) && Time.unscaledTime < 30f) return;
            hooksApplied = true;
            try { Patches.Apply(new HarmonyLib.Harmony(Plugin.Guid)); Plugin.L.LogInfo($"Game hooks applied (scene '{scene}')."); }
            catch (Exception e) { Plugin.L.LogError("Hook setup failed: " + e); }
        }

        void LateUpdate()
        {
            if (!vrOn) return;
            if (hooksApplied && !Patches.PlayerHooked && pc == null && Time.unscaledTime >= nextPcSearch)
            {
                nextPcSearch = Time.unscaledTime + 1f;
                var found = UnityEngine.Object.FindObjectOfType<global::PlayerController>();
                if (found != null) { pc = found; pcPtr = found.Pointer; needRecenter = true; }
            }
            if (!beforeRenderHooked && (pc == null || !Patches.PlayerHooked)) SafeFrame();
        }

        void OnBeforeRender() => SafeFrame();

        void DumpTree(Transform t, int depth, int maxDepth, List<string> outp)
        {
            if (t == null || outp.Count > 120) return;
            var lp = t.localPosition;
            outp.Add($"{new string(' ', depth * 2)}{t.name} [{(t.gameObject.activeSelf ? "on" : "off")}] lp=({lp.x:0.00},{lp.y:0.00},{lp.z:0.00})");
            if (depth >= maxDepth) return;
            for (int i = 0; i < t.childCount; i++) DumpTree(t.GetChild(i), depth + 1, maxDepth, outp);
        }

        internal void OnPlayerLateUpdate(global::PlayerController p)
        {
            if (pcPtr != p.Pointer && Plugin.DebugSnapshots.Value)
            {
                try
                {
                    var lines = new List<string>();
                    var cam = p.fp_camera != null ? p.fp_camera.transform : null;
                    DumpTree(cam, 0, 4, lines);
                    if (p.right_hand_parent != null && (cam == null || !p.right_hand_parent.IsChildOf(cam)))
                    { lines.Add("-- Gun.R ancestry:"); for (var a = p.right_hand_parent; a != null; a = a.parent) lines.Add("   " + a.name); }
                    var ws = p.weapons;
                    if (ws != null)
                        for (int s = 0; s < ws.Length; s++)
                        {
                            var arr = ws[s]; if (arr == null) continue;
                            for (int j = 0; j < arr.Length; j++)
                            {
                                var w = arr[j]; if (w == null) continue;
                                string path = w.gameObject.name; for (var a = w.transform.parent; a != null; a = a.parent) path = a.name + "/" + path;
                                lines.Add($"-- weapon slot {s}: {path} active={w.gameObject.activeInHierarchy}");
                            }
                        }
                    System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "vhvr_player_tree.txt"), lines);
                }
                catch (Exception e) { Plugin.L.LogWarning("tree dump: " + e.Message); }
            }
            if (pcPtr != p.Pointer) { pc = p; pcPtr = p.Pointer; needRecenter = true; Plugin.L.LogInfo($"Player found: {p.name} fp_camera={(p.fp_camera == null ? "null" : p.fp_camera.name)} camera_transform={(p.camera_transform == null ? "null" : p.camera_transform.name)} hands L={(p.left_hand_parent == null ? "null" : p.left_hand_parent.name)} R={(p.right_hand_parent == null ? "null" : p.right_hand_parent.name)}"); }
            if (!beforeRenderHooked && vrOn) SafeFrame();
        }

        void SafeFrame()
        {
            try { FrameUpdate(); }
            catch (Exception e) { Fail(e); }
        }

        bool introActive, introDone, introWasShown;
        RenderTexture introRT; Texture2D introTex;

        void EnsureIntroTex()
        {
            int w = Math.Max(640, Screen.width), h = Math.Max(360, Screen.height);
            if (introRT != null && introRT.width == w && introRT.height == h) return;
            if (introRT != null) { introRT.Release(); UnityEngine.Object.Destroy(introRT); }
            if (introTex != null) UnityEngine.Object.Destroy(introTex);
            introRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32); introRT.Create();
            introTex = new Texture2D(w, h, TextureFormat.RGBA32, false, QualitySettings.activeColorSpace == ColorSpace.Gamma);
            introTex.Apply(false, true);
            sIntroFlip = Plugin.FlipMenuScreen.Value;
            sIntroTex = introTex.GetNativeTexturePtr();
            introWasShown = true;
        }

        IEnumerator EndOfFrameLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (vrOn && introActive)
                {
                    try
                    {
                        EnsureIntroTex();
                        ScreenCapture.CaptureScreenshotIntoRenderTexture(introRT);
                        Graphics.CopyTexture(introRT, introTex);
                        GL.IssuePluginEvent(sRenderFn, 3);
                    }
                    catch (Exception e) { Plugin.L.LogWarning("intro screen: " + e.Message); introActive = false; introDone = true; }
                    continue;
                }
                if (vrOn && introWasShown && !introActive) { introWasShown = false; GL.IssuePluginEvent(sRenderFn, 4); }
                if (vrOn && flatActive && screenRT != null)
                {
                    try { ScreenCapture.CaptureScreenshotIntoRenderTexture(screenRT); } catch (Exception e) { Plugin.L.LogWarning("menu capture: " + e.Message); }
                }
                if (vrOn)
                {
                    if (frameReady && Modes[mode].copy)
                        for (int e = 0; e < 2; e++)
                            if (eyeRT[e] != null && eyeCopy[e] != null) Graphics.CopyTexture(eyeRT[e], eyeCopy[e]);
                    GL.IssuePluginEvent(sRenderFn, frameReady ? 10 + renderSlot : 2);
                    frameReady = false;
                }
            }
        }

        static void IntroOverlay(CVRCompositor c)
        {
            if (sOvHandle == 0)
            {
                ulong hnd = 0;
                if (sOverlay.CreateOverlay("vhvr.intro", "Vampire Hunters", ref hnd) != EVROverlayError.None) return;
                sOvHandle = hnd;
                sOverlay.SetOverlayWidthInMeters(sOvHandle, 3.0f);
                var b = new VRTextureBounds_t { uMin = 0, uMax = 1, vMin = sIntroFlip ? 1 : 0, vMax = sIntroFlip ? 0 : 1 };
                sOverlay.SetOverlayTextureBounds(sOvHandle, ref b);
                sOverlay.ShowOverlay(sOvHandle);
            }
            var hp = rtPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!sIntroPlaced && hp.bPoseIsValid)
            {
                var m = hp.mDeviceToAbsoluteTracking;
                float fx = -m.m2, fz = -m.m10;
                float len = (float)Math.Sqrt(fx * fx + fz * fz);
                if (len > 1e-3f)
                {
                    fx /= len; fz /= len;
                    var t = new HmdMatrix34_t
                    {
                        m0 = -fz, m1 = 0, m2 = -fx, m3 = m.m3 + fx * 2.5f,
                        m4 = 0, m5 = 1, m6 = 0, m7 = m.m7,
                        m8 = fx, m9 = 0, m10 = -fz, m11 = m.m11 + fz * 2.5f
                    };
                    sOverlay.SetOverlayTransformAbsolute(sOvHandle, c.GetTrackingSpace(), ref t);
                    sIntroPlaced = true;
                }
            }
            var tex = new Texture_t { handle = sIntroTex, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
            sOverlay.SetOverlayTexture(sOvHandle, ref tex);
        }

        static void OnRenderEvent(int id)
        {
            lock (sCallLock)
            {
                var c = sComp; var sys = sSys;
                if (c == null || sys == null) return;
                try
                {
                    // id 1 = eyes rendered this frame: submit them, then wait for next poses.
                    if (id >= 10 && sWaited && sTex[0] != IntPtr.Zero && sTex[1] != IntPtr.Zero)
                    {
                        int slot = (id - 10) & 3;
                        bool withPose = sSlotValid[slot];
                        for (int e = 0; e < 2; e++)
                        {
                            var tex = new VRTextureWithPose_t
                            {
                                handle = sTex[e], eType = ETextureType.DirectX, eColorSpace = sColor,
                                mDeviceToAbsoluteTracking = sSlotPose[slot]
                            };
                            var b = sBounds;
                            // Texture_t is a prefix of VRTextureWithPose_t; native side reads the pose when the flag is set.
                            var err = c.Submit(e == 0 ? EVREye.Eye_Left : EVREye.Eye_Right,
                                ref System.Runtime.CompilerServices.Unsafe.As<VRTextureWithPose_t, Texture_t>(ref tex), ref b,
                                withPose ? EVRSubmitFlags.Submit_TextureWithPose : EVRSubmitFlags.Submit_Default);
                            if (err != EVRCompositorError.None) { sSubmitError = (int)err; System.Threading.Interlocked.Increment(ref sErr); }
                            else System.Threading.Interlocked.Increment(ref sOk);
                        }
                        c.PostPresentHandoff();
                    }
                    if (id == 3 && sOverlay != null && sIntroTex != IntPtr.Zero) IntroOverlay(c);
                    if (id == 4 && sOverlay != null && sOvHandle != 0) { sOverlay.DestroyOverlay(sOvHandle); sOvHandle = 0; sIntroPlaced = false; }
                    c.WaitGetPoses(rtPoses, rtNoGame);
                    sWaited = true;

                    uint l = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
                    uint r = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
                    for (int hh = 0; hh < 2; hh++)
                    {
                        int left = sHapticFrames[hh];
                        if (left <= 0) continue;
                        sHapticFrames[hh] = left - 1;
                        uint di = hh == 0 ? l : r;
                        if (di < OpenVR.k_unMaxTrackedDeviceCount) sys.TriggerHapticPulse(di, 0, sHapticUs);
                    }
                    var st0 = new VRControllerState_t(); var st1 = new VRControllerState_t();
                    uint size = (uint)Marshal.SizeOf<VRControllerState_t>();
                    bool ok0 = l < OpenVR.k_unMaxTrackedDeviceCount && sys.GetControllerState(l, ref st0, size);
                    bool ok1 = r < OpenVR.k_unMaxTrackedDeviceCount && sys.GetControllerState(r, ref st1, size);

                    lock (sDataLock)
                    {
                        Array.Copy(rtPoses, sPoses, rtPoses.Length);
                        sRole[0] = l; sRole[1] = r;
                        sCtrl[0] = st0; sCtrl[1] = st1; sCtrlOk[0] = ok0; sCtrlOk[1] = ok1;
                        sPoseFrame++;
                    }
                }
                catch { sSubmitError = -1; }
            }
        }

        void OnApplicationQuit() { if (vrOn) StopVr(); }

        // =====================================================================
        // Start / stop
        // =====================================================================
        void StartVr()
        {
            if (vrOn) return;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
            {
                Plugin.L.LogError($"VR needs DirectX 11 but game runs {SystemInfo.graphicsDeviceType}. Add -force-d3d11 to Steam launch options.");
                return;
            }
            var err = EVRInitError.None;
            sys = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
            if (err != EVRInitError.None || sys == null) { Plugin.L.LogError("SteamVR init failed: " + err + ". Is SteamVR running and headset on?"); sys = null; return; }
            var comp = OpenVR.Compositor;
            if (comp == null) { Plugin.L.LogError("SteamVR compositor unavailable."); OpenVR.Shutdown(); sys = null; return; }

            sys.GetRecommendedRenderTargetSize(ref eyeW, ref eyeH);
            float s = Mathf.Clamp(Plugin.ResolutionScale.Value, 0.3f, 1.5f);
            eyeW = (uint)Math.Max(256, eyeW * s); eyeH = (uint)Math.Max(256, eyeH * s);

            for (int e = 0; e < 2; e++)
            {
                var eye = e == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
                sys.GetProjectionRaw(eye, ref raw[e][0], ref raw[e][1], ref raw[e][2], ref raw[e][3]);
                var m = sys.GetEyeToHeadTransform(eye);
                eyeOffset[e] = PosOf(m); eyeRotOffset[e] = RotOf(m);

                var rt = new RenderTexture((int)eyeW, (int)eyeH, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                rt.name = "VHVR Eye " + e;
                rt.antiAliasing = 1;
                rt.hideFlags = HideFlags.DontUnloadUnusedAsset;
                if (!rt.Create()) throw new Exception("Eye texture allocation failed.");
                eyeRT[e] = rt;
                var tex = new Texture2D((int)eyeW, (int)eyeH, TextureFormat.RGBA32, false, QualitySettings.activeColorSpace == ColorSpace.Gamma);
                tex.name = "VHVR Submit " + e;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                tex.Apply(false, true);
                eyeCopy[e] = tex;
                if (rt.GetNativeTexturePtr() == IntPtr.Zero) throw new Exception("Eye texture has no native handle.");
            }
            UpdateBounds();
            modeConfirmed = false;
            ApplyMode(0);
            Plugin.L.LogInfo($"Eye texture: {eyeRT[0].graphicsFormat}, sRGB={eyeRT[0].sRGB}, project color space {QualitySettings.activeColorSpace}.");
            hudLayer = 0; // Default layer: always drawn by the game renderer (custom layers were filtered out)
            sSubmitError = 0; loggedSubmitError = 0;
            try { sOverlay = OpenVR.Overlay; } catch { sOverlay = null; }
            lock (sCallLock) { sWaited = false; sSys = sys; sComp = comp; sOvHandle = 0; sIntroPlaced = false; }
            vrOn = true; needRecenter = true; hudInit = false; srcPtr = IntPtr.Zero;
            Plugin.L.LogInfo($"VR on: {eyeW}x{eyeH} per eye, HUD layer {hudLayer}, GPU {SystemInfo.graphicsDeviceName}.");
        }

        void StopVr()
        {
            bool was = vrOn;
            vrOn = false; frameReady = false;
            lock (sCallLock)
            {
                try { if (sOverlay != null && sOvHandle != 0) sOverlay.DestroyOverlay(sOvHandle); } catch { }
                sOvHandle = 0; sComp = null; sSys = null; sWaited = false;
            }
            try { EndAimForce(); } catch { }
            try { InputEmu.ReleaseAll(); } catch { }
            try { DestroyEyes(); } catch (Exception e) { Plugin.L.LogWarning(e.Message); }
            for (int h = 0; h < 2; h++) if (crossCanvas[h] != null) { UnityEngine.Object.Destroy(crossCanvas[h]); crossCanvas[h] = null; }
            if (hiddenCross != null) { hiddenCross.alpha = 1f; hiddenCross = null; } crossSource = null;
            if (screenGo != null) { UnityEngine.Object.Destroy(screenGo); screenGo = null; }
            if (screenRT != null) { screenRT.Release(); UnityEngine.Object.Destroy(screenRT); screenRT = null; }
            flatActive = false;
            try { RestoreCanvases(); } catch (Exception e) { Plugin.L.LogWarning(e.Message); }
            for (int e = 0; e < 2; e++)
            {
                if (eyeRT[e] != null) { eyeRT[e].Release(); UnityEngine.Object.Destroy(eyeRT[e]); eyeRT[e] = null; }
                if (eyeCopy[e] != null) { UnityEngine.Object.Destroy(eyeCopy[e]); eyeCopy[e] = null; }
                sTex[e] = IntPtr.Zero;
            }
            if (sys != null) { lock (sCallLock) { try { OpenVR.Shutdown(); } catch { } } sys = null; }
            if (was) Plugin.L.LogInfo("VR off. Game camera and UI restored.");
        }

        internal void Fail(Exception e)
        {
            Plugin.L.LogError(e.ToString());
            if (vrOn) StopVr();
        }

        void RefreshHandles()
        {
            bool copy = Modes[mode].copy;
            for (int e = 0; e < 2; e++)
            {
                if (eyeRT[e] == null) continue;
                if (!eyeRT[e].IsCreated()) eyeRT[e].Create();
                var ptr = copy && eyeCopy[e] != null ? eyeCopy[e].GetNativeTexturePtr() : eyeRT[e].GetNativeTexturePtr();
                if (ptr != IntPtr.Zero) sTex[e] = ptr;
            }
            sColor = Modes[mode].cs;
        }

        void ApplyMode(int m)
        {
            mode = m;
            lock (sCallLock) { RefreshHandles(); }
            System.Threading.Interlocked.Exchange(ref sOk, 0);
            System.Threading.Interlocked.Exchange(ref sErr, 0);
            nextModeCheck = Time.unscaledTime + 2f;
        }

        // Watches submit results; if SteamVR keeps rejecting frames, try the next texture strategy.
        void CheckSubmitMode()
        {
            if (Time.unscaledTime < nextModeCheck) return;
            nextModeCheck = Time.unscaledTime + 2f;
            int ok = System.Threading.Interlocked.Exchange(ref sOk, 0);
            int err = System.Threading.Interlocked.Exchange(ref sErr, 0);
            if (ok + err == 0) return;
            if (err == 0 || ok > err * 20)
            {
                if (!modeConfirmed) { modeConfirmed = true; Plugin.L.LogInfo($"SteamVR accepting frames using {Modes[mode].name} ({ok} ok, {err} errors in 2s)."); }
                return;
            }
            if (modeConfirmed) { Plugin.L.LogWarning($"SteamVR errors returned ({err}/{ok + err}): {(EVRCompositorError)sSubmitError}"); return; }
            int next = (mode + 1) % Modes.Length;
            Plugin.L.LogWarning($"SteamVR rejected {Modes[mode].name} ({(EVRCompositorError)sSubmitError}, {err} errors). Trying {Modes[next].name}.");
            ApplyMode(next);
        }

        void UpdateBounds()
        {
            bool f = Plugin.FlipEyeY.Value;
            sBounds = new VRTextureBounds_t { uMin = 0, uMax = 1, vMin = f ? 1 : 0, vMax = f ? 0 : 1 };
        }

        int PickHudLayer()
        {
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return 31;
        }

        // =====================================================================
        // Per-frame tracking and camera placement
        // =====================================================================
        void FrameUpdate()
        {
            if (!vrOn || Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount;

            ReadPoses();

            // Until the main menu loads, the game's logo intro plays on a flat floating screen.
            if (!introDone)
            {
                string scene = "";
                try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
                if (!Plugin.FlatIntro.Value || scene.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0 || Time.unscaledTime > 90f)
                {
                    introDone = true;
                    if (introActive) Plugin.L.LogInfo("Intro finished: full VR on (scene '" + scene + "').");
                    introActive = false;
                }
                else
                {
                    if (!introActive) Plugin.L.LogInfo("Intro: showing logo screen flat.");
                    introActive = true;
                    frameReady = false;
                    return;
                }
            }

            var cam = PickSourceCamera();
            if (cam == null) { SetEyesActive(false); return; }
            if (cam.Pointer != srcPtr) SetupEyes(cam);
            SetEyesActive(true);

            float scale = Mathf.Max(0.01f, Plugin.WorldUnitsPerMeter.Value);
            var ct = cam.transform;
            var basePos = ct.position;
            lastBasePos = basePos;
            bodyYaw = YawOf(ct.rotation);

            if (needRecenter && hmdValid)
            {
                needRecenter = false;
                origin = hmdPos; originYaw = YawOf(hmdRot); extraYaw = 0; hudInit = false; menuLocked = false;
            }

            ApplyTurning();

            var world = Mul(Yaw(bodyYaw + extraYaw), Yaw(-originYaw));
            lastBaseRot = Yaw(bodyYaw + extraYaw);
            headPos = Add(basePos, Rotate(world, Mul(Sub(hmdPos, origin), scale)));
            headRot = Mul(world, hmdRot);
            var tilt = Pitch(Plugin.HandPitchOffset.Value);
            for (int h = 0; h < 2; h++)
            {
                handValid[h] = ctrlValid[h];
                if (!ctrlValid[h]) continue;
                handPos[h] = Add(basePos, Rotate(world, Mul(Sub(ctrlPos[h], origin), scale)));
                handRot[h] = Mul(Mul(world, ctrlRot[h]), tilt);
            }

            // Raised near clip hides the flat-game arm model where it pokes back into your face.
            float near = Mathf.Max(Mathf.Max(0.01f, Plugin.NearClip.Value * scale), cam.nearClipPlane), far = Mathf.Max(near + 1f, cam.farClipPlane);
            for (int e = 0; e < 2; e++)
            {
                var p = Add(headPos, Rotate(headRot, Mul(eyeOffset[e], scale)));
                var r = Mul(headRot, eyeRotOffset[e]);
                var proj = Projection(raw[e][0], raw[e][1], raw[e][2], raw[e][3], near, far);
                PlaceCam(eyes[e], p, r, near, far, proj);
                foreach (var oc in overlayClones[e]) if (oc != null) PlaceCam(oc, p, r, near, far, proj);
                if (hudCams[e] != null)
                {
                    float hn = 0.02f * scale, hf = 20f * scale;
                    PlaceCam(hudCams[e], p, r, hn, hf, Projection(raw[e][0], raw[e][1], raw[e][2], raw[e][3], hn, hf));
                }
            }

            if (pc != null)
            {
                BuildWeaponMap();
                if (Plugin.HandTracking.Value) PlaceHands(scale);
            }

            if (flatActive) PlaceScreen(scale);
            else if (Plugin.HudInVR.Value) PlaceHud(scale);
            UpdatePointer();

            // Unity can recreate render textures (device reset, resize); keep native handles fresh.
            if (Time.frameCount % 90 == 0) RefreshHandles();

            frameReady = true;
        }

        static IntPtr SafePtr(UnityEngine.Object o) => o == null ? IntPtr.Zero : o.Pointer;

        static void PlaceCam(Camera c, Vector3 p, Quaternion r, float n, float f, Matrix4x4 proj)
        {
            if (c == null) return;
            c.transform.SetPositionAndRotation(p, r);
            c.nearClipPlane = n; c.farClipPlane = f;
            c.projectionMatrix = proj;
        }

        void ReadPoses()
        {
            uint[] role = new uint[2];
            lock (sDataLock)
            {
                Array.Copy(sPoses, renderPoses, sPoses.Length);
                role[0] = sRole[0]; role[1] = sRole[1];
            }
            var hp = renderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            hmdValid = hp.bPoseIsValid;
            renderSlot = (renderSlot + 1) & 3;
            sSlotPose[renderSlot] = hp.mDeviceToAbsoluteTracking;
            sSlotValid[renderSlot] = hmdValid;
            if (hmdValid) { hmdPos = PosOf(hp.mDeviceToAbsoluteTracking); hmdRot = RotOf(hp.mDeviceToAbsoluteTracking); }
            for (int h = 0; h < 2; h++)
            {
                uint idx = role[h];
                ctrlValid[h] = idx < OpenVR.k_unMaxTrackedDeviceCount && renderPoses[idx].bPoseIsValid;
                if (ctrlValid[h])
                {
                    ctrlPos[h] = PosOf(renderPoses[idx].mDeviceToAbsoluteTracking);
                    ctrlRot[h] = RotOf(renderPoses[idx].mDeviceToAbsoluteTracking);
                }
            }
        }

        void ApplyTurning()
        {
            if (!Plugin.ControllerInput.Value || KeyboardMode) return;
            var mode = Plugin.TurnMode.Value;
            float x = rStick.x;
            if (mode.Equals("Snap", StringComparison.OrdinalIgnoreCase))
            {
                if (!snapLatch && Math.Abs(x) > 0.7f) { snapLatch = true; extraYaw += Math.Sign(x) * Plugin.SnapTurnDegrees.Value; }
                else if (snapLatch && Math.Abs(x) < 0.3f) snapLatch = false;
            }
            else if (mode.Equals("Smooth", StringComparison.OrdinalIgnoreCase) && Math.Abs(x) > 0.15f)
                extraYaw += x * Plugin.SmoothTurnSpeed.Value * Time.unscaledDeltaTime;
        }

        Camera PickSourceCamera()
        {
            if (pc != null)
            {
                var fp = pc.fp_camera;
                if (fp != null && fp.isActiveAndEnabled) return fp;
            }
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled) return main;
            if (src != null && src.isActiveAndEnabled) return src;
            return null;
        }

        // =====================================================================
        // Eye camera rig
        // =====================================================================
        void SetupEyes(Camera cam)
        {
            DestroyEyes();
            src = cam; srcPtr = cam.Pointer;
            int hudBit = 1 << hudLayer;

            UniversalAdditionalCameraData sd = null;
            try { sd = CameraExtensions.GetUniversalAdditionalCameraData(cam); } catch { }

            for (int e = 0; e < 2; e++)
            {
                var c = NewCam("VHVR Eye " + e);
                c.CopyFrom(cam);
                c.stereoTargetEye = StereoTargetEyeMask.None;
                c.targetTexture = eyeRT[e];
                c.cullingMask = cam.cullingMask | hudBit; // HUD also drawn in-world in case URP camera stacking is unavailable
                c.depth = cam.depth - 20 + e;
                c.enabled = true;
                eyes[e] = c;

                if (sd != null)
                {
                    var ed = CameraExtensions.GetUniversalAdditionalCameraData(c);
                    ed.renderType = CameraRenderType.Base;
                    bool post = Plugin.PostProcessing.Value;
                    ed.renderPostProcessing = post && sd.renderPostProcessing;
                    ed.antialiasing = post && sd.antialiasing != AntialiasingMode.TemporalAntiAliasing ? sd.antialiasing : AntialiasingMode.None;
                    ed.antialiasingQuality = sd.antialiasingQuality;
                    ed.renderShadows = sd.renderShadows;
                    ed.requiresDepthTexture = sd.requiresDepthTexture;
                    ed.volumeLayerMask = sd.volumeLayerMask;
                    try { ed.SetRenderer(sd.m_RendererIndex); } catch { }
                    ed.cameraStack.Clear();

                    var stack = sd.cameraStack;
                    if (stack != null)
                    {
                        for (int i = 0; i < stack.Count; i++)
                        {
                            var oc = stack[i];
                            if (oc == null) continue;
                            var clone = NewCam($"VHVR Eye {e} Overlay {i}");
                            clone.CopyFrom(oc);
                            clone.targetTexture = null;
                            clone.stereoTargetEye = StereoTargetEyeMask.None;
                            clone.cullingMask = oc.cullingMask & ~hudBit;
                            var od = CameraExtensions.GetUniversalAdditionalCameraData(clone);
                            od.renderType = CameraRenderType.Overlay;
                            try
                            {
                                var sod = CameraExtensions.GetUniversalAdditionalCameraData(oc);
                                od.renderPostProcessing = Plugin.PostProcessing.Value && sod.renderPostProcessing;
                                od.m_ClearDepth = sod.clearDepth;
                                od.renderShadows = sod.renderShadows;
                            }
                            catch { }
                            clone.enabled = true;
                            ed.cameraStack.Add(clone);
                            overlayClones[e].Add(clone);
                        }
                    }

                    if (false)
                    {
                        var hc = NewCam($"VHVR Eye {e} HUD");
                        hc.clearFlags = CameraClearFlags.Depth;
                        hc.cullingMask = hudBit;
                        hc.stereoTargetEye = StereoTargetEyeMask.None;
                        var hd = CameraExtensions.GetUniversalAdditionalCameraData(hc);
                        hd.renderType = CameraRenderType.Overlay;
                        hd.renderPostProcessing = false;
                        hd.renderShadows = false;
                        hd.m_ClearDepth = true;
                        hc.enabled = true;
                        ed.cameraStack.Add(hc);
                        hudCams[e] = hc;
                    }
                }
            }

            if (!Plugin.MirrorToDesktop.Value)
            {
                Mute(cam);
                if (sd != null && sd.cameraStack != null)
                    for (int i = 0; i < sd.cameraStack.Count; i++) Mute(sd.cameraStack[i]);
            }
            Plugin.L.LogInfo($"[cursor visible={Cursor.visible}, lock={Cursor.lockState}] VR rig attached to camera '{cam.name}' ({(sd?.cameraStack?.Count ?? 0)} overlay cams).");
        }

        void Mute(Camera c)
        {
            if (c == null) return;
            mutedCams.Add((c, c.cullingMask));
            c.cullingMask = 0;
        }

        Camera NewCam(string name)
        {
            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var c = go.AddComponent<Camera>();
            c.enabled = false;
            return c;
        }

        void SetEyesActive(bool on)
        {
            for (int e = 0; e < 2; e++)
                if (eyes[e] != null && eyes[e].enabled != on) eyes[e].enabled = on;
            if (!on) frameReady = false;
        }

        void DestroyEyes()
        {
            foreach (var (c, mask) in mutedCams) if (c != null) c.cullingMask = mask;
            mutedCams.Clear();
            for (int e = 0; e < 2; e++)
            {
                if (eyes[e] != null) UnityEngine.Object.Destroy(eyes[e].gameObject);
                if (hudCams[e] != null) UnityEngine.Object.Destroy(hudCams[e].gameObject);
                eyes[e] = null; hudCams[e] = null;
                foreach (var oc in overlayClones[e]) if (oc != null) UnityEngine.Object.Destroy(oc.gameObject);
                overlayClones[e].Clear();
            }
            if (pointerCam != null) { UnityEngine.Object.Destroy(pointerCam.gameObject); pointerCam = null; }
            src = null; srcPtr = IntPtr.Zero;
        }

        // =====================================================================
        // Hands and aiming
        // =====================================================================
        void BuildWeaponMap()
        {
            weaponSlot.Clear();
            var ws = pc.weapons;
            if (ws == null) return;
            for (int s = 0; s < ws.Length; s++)
            {
                var arr = ws[s];
                if (arr == null) continue;
                for (int j = 0; j < arr.Length; j++)
                {
                    var w = arr[j];
                    if (w != null) weaponSlot[w.Pointer] = s;
                }
            }
        }

        static float MaxAbs(float a, float b, float c) { float m = a; if (Math.Abs(b) > Math.Abs(m)) m = b; if (Math.Abs(c) > Math.Abs(m)) m = c; return m; }

        static int HandOfSlot(int slot) => slot == 0 || slot == 2 ? 0 : slot == 1 || slot == 3 ? 1 : -1;

        // The visible guns hang under "Weapons - Right/Left Hand" below the game camera.
        IntPtr handRootPc; readonly Transform[] handRoot = new Transform[2];
        Transform HandRoot(int h)
        {
            if (pc == null) return null;
            if (handRootPc != pcPtr)
            {
                handRootPc = pcPtr; handRoot[0] = handRoot[1] = null;
                var cam = pc.fp_camera != null ? pc.fp_camera.transform : null;
                if (cam != null)
                    for (int i = 0; i < cam.childCount; i++)
                    {
                        var c = cam.GetChild(i);
                        if (c.name.IndexOf("Left Hand", StringComparison.OrdinalIgnoreCase) >= 0) handRoot[0] = c;
                        else if (c.name.IndexOf("Right Hand", StringComparison.OrdinalIgnoreCase) >= 0) handRoot[1] = c;
                    }
                Plugin.L.LogInfo($"Gun roots: L={(handRoot[0] == null ? "fallback " + pc.left_hand_parent?.name : handRoot[0].name)} R={(handRoot[1] == null ? "fallback " + pc.right_hand_parent?.name : handRoot[1].name)}");
            }
            if (handRoot[h] != null) return handRoot[h];
            return h == 0 ? pc.left_hand_parent : pc.right_hand_parent;
        }

        static Transform FindChildNamed(Transform root, string name)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all) if (t != null && t.name == name) return t;
            return null;
        }

        void PlaceHands(float scale)
        {
            var grip = V(Plugin.GripOffsetX.Value, Plugin.GripOffsetY.Value, Plugin.GripOffsetZ.Value);
            for (int h = 0; h < 2; h++)
            {
                if (!handValid[h]) continue;
                var parent = HandRoot(h);
                if (parent == null) continue;

                var g = h == 0 ? Add(grip, V(Plugin.LeftGunOffsetX.Value, Plugin.LeftGunOffsetY.Value, Plugin.LeftGunOffsetZ.Value)) : grip;
                var target = Add(handPos[h], Rotate(handRot[h], Mul(g, scale)));
                // Same reference point for both hands ("Start Weapon" slot), so both arms come out equal length.
                Transform gt = FindChildNamed(parent, "Start Weapon");
                if (gt == null)
                {
                    var guns = parent.GetComponentsInChildren<global::Weapon>(false);
                    if (guns != null && guns.Length > 0) gt = guns[0].transform;
                }

                if (gt != null)
                {
                    var pr = parent.rotation;
                    var localRot = Mul(Inverse(pr), gt.rotation);
                    var localPos = parent.InverseTransformPoint(gt.position);
                    float ps = parent.lossyScale.x;
                    var newRot = Mul(handRot[h], Inverse(localRot));
                    var newPos = Sub(target, Rotate(newRot, Mul(localPos, ps)));
                    parent.SetPositionAndRotation(newPos, newRot);
                }
                else parent.SetPositionAndRotation(target, handRot[h]);
            }
        }

        readonly List<Transform> aimT = new List<Transform>();
        readonly List<Vector3> aimSavedPos = new List<Vector3>();
        readonly List<Quaternion> aimSavedRot = new List<Quaternion>();
        readonly List<Vector3> aimSetPos = new List<Vector3>();
        readonly List<Quaternion> aimSetRot = new List<Quaternion>();

        // Saves LOCAL pose so game changes to parents (body turning) survive the restore.
        void AddAim(Transform t)
        {
            if (t == null) return;
            foreach (var x in aimT) if (x.Pointer == t.Pointer) return;
            aimT.Add(t); aimSavedPos.Add(t.localPosition); aimSavedRot.Add(t.localRotation);
        }

        static bool Near(Quaternion a, Quaternion b) => Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) > 0.99999f;
        static bool Near(Vector3 a, Vector3 b) { var d = Sub(a, b); return d.x * d.x + d.y * d.y + d.z * d.z < 1e-8f; }

        // While a weapon fires, every camera the game might aim from points along the controller.
        // Rumble the controller holding the gun that just fired.
        readonly float[] lastPulse = { -1f, -1f };
        internal void ShotFired(global::Weapon w)
        {
            if (!vrOn || w == null) return;
            float strength = Mathf.Clamp01(Plugin.HapticStrength.Value);
            if (strength <= 0) return;
            // Only guns actually held in a hand: passive weapons/turrets also "shoot" and used to buzz the right hand.
            if (!weaponSlot.TryGetValue(w.Pointer, out int slot)) return;
            int h = HandOfSlot(slot);
            if (h < 0) return;
            var root = HandRoot(h);
            if (root == null || !w.transform.IsChildOf(root)) return;
            // Auto-shoot fires on its own; only rumble while you're actually pulling a trigger.
            if (Plugin.HapticOnlyWhenTrigger.Value && !btn[(int)B.RTrig] && !btn[(int)B.LTrig]) return;
            // One short tap per shot with a minimum gap: rapid fire becomes a pulse, not a constant buzz.
            float now = Time.unscaledTime;
            if (now - lastPulse[h] < Plugin.HapticMinGap.Value) return;
            lastPulse[h] = now;
            sHapticUs = (ushort)Math.Max(300, 3999 * strength);
            sHapticFrames[h] = Math.Max(sHapticFrames[h], Math.Max(1, (int)Math.Round(Plugin.HapticFrames.Value)));
        }

        internal void BeginAimPlayer(global::PlayerController p)
        {
            if (pc == null || p.Pointer != pcPtr) { aimDepth++; return; }
            BeginAimCore(1);
        }

        internal void BeginAim(global::Weapon w)
        {
            int h = 1;
            if (w != null && weaponSlot.TryGetValue(w.Pointer, out int slot)) { int hs = HandOfSlot(slot); if (hs >= 0) h = hs; }
            BeginAimCore(h);
        }

        int aimHand = -1;
        readonly Stack<(int depth, int prevHand, List<(Transform t, Vector3 p, Quaternion r)> saved)> nestedAim = new Stack<(int, int, List<(Transform, Vector3, Quaternion)>)>();

        void BeginAimCore(int h)
        {
            aimDepth++;
            if (aimDepth > 1)
            {
                // Inside player logic already aimed with the right hand: a left-hand gun re-aims with the left hand.
                if (aimSwapped && Plugin.HandAim.Value && h != aimHand && h >= 0 && handValid[h] && aimT.Count > 0)
                {
                    var saved = new List<(Transform, Vector3, Quaternion)>();
                    foreach (var t in aimT) if (t != null) saved.Add((t, t.position, t.rotation));
                    var keepHands = new List<(Transform, Vector3, Quaternion)>();
                    for (int k = 0; k < 2; k++) { var hr = HandRoot(k); if (hr != null) keepHands.Add((hr, hr.position, hr.rotation)); }
                    for (int pass = 0; pass < 2; pass++) foreach (var t in aimT) if (t != null) t.SetPositionAndRotation(handPos[h], handRot[h]);
                    foreach (var (t, p0, r0) in keepHands) t.SetPositionAndRotation(p0, r0);
                    nestedAim.Push((aimDepth, aimHand, saved));
                    aimHand = h;
                }
                return;
            }
            if (!vrOn || pc == null) return;
            aimHand = h;

            Vector3 p = headPos; Quaternion r = headRot;
            if (Plugin.HandAim.Value && handValid[h]) { p = handPos[h]; r = handRot[h]; }

            aimT.Clear(); aimSavedPos.Clear(); aimSavedRot.Clear();
            AddAim(src != null ? src.transform : null);
            var main = Camera.main; if (main != null && !main.name.StartsWith("VHVR")) AddAim(main.transform);
            var fp = pc.fp_camera; if (fp != null) AddAim(fp.transform);
            AddAim(pc.camera_transform);
            if (aimT.Count == 0) return;

            for (int k = 0; k < 2; k++)
            {
                var t = HandRoot(k);
                savedHand[k] = t;
                if (t != null) { savedHandPos[k] = t.position; savedHandRot[k] = t.rotation; }
            }
            // Two passes so children end up exact even if one target is parent of another.
            for (int pass = 0; pass < 2; pass++) foreach (var t in aimT) t.SetPositionAndRotation(p, r);
            aimSetPos.Clear(); aimSetRot.Clear();
            foreach (var t in aimT) { aimSetPos.Add(t.localPosition); aimSetRot.Add(t.localRotation); }
            RestoreHands();
            aimSwapped = true;
        }

        internal void EndAim()
        {
            if (nestedAim.Count > 0 && nestedAim.Peek().depth == aimDepth)
            {
                var (_, prevHand, saved) = nestedAim.Pop();
                var keepHands = new List<(Transform, Vector3, Quaternion)>();
                for (int k = 0; k < 2; k++) { var hr = HandRoot(k); if (hr != null) keepHands.Add((hr, hr.position, hr.rotation)); }
                for (int pass = 0; pass < 2; pass++) foreach (var (t, p0, r0) in saved) if (t != null) t.SetPositionAndRotation(p0, r0);
                foreach (var (t, p0, r0) in keepHands) t.SetPositionAndRotation(p0, r0);
                aimHand = prevHand;
            }
            if (aimDepth > 0) aimDepth--;
            if (aimDepth == 0 && aimSwapped) EndAimForce();
        }

        void EndAimForce()
        {
            if (!aimSwapped) return;
            aimSwapped = false; aimDepth = 0; nestedAim.Clear(); aimHand = -1;
            // Restore only what the game did not rewrite while we were swapped.
            for (int i = 0; i < aimT.Count; i++)
            {
                var t = aimT[i];
                if (t == null) continue;
                if (Near(t.localRotation, aimSetRot[i])) t.localRotation = aimSavedRot[i];
                if (Near(t.localPosition, aimSetPos[i])) t.localPosition = aimSavedPos[i];
            }
            RestoreHands();
            aimT.Clear();
        }

        void RestoreHands()
        {
            for (int h = 0; h < 2; h++)
                if (savedHand[h] != null) savedHand[h].SetPositionAndRotation(savedHandPos[h], savedHandRot[h]);
        }

        // =====================================================================
        // HUD: move screen-space canvases onto a floating panel
        // =====================================================================
        void ScanCanvases()
        {
            canvases.RemoveAll(r => r.c == null);
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>());
            if (all == null) return;
            foreach (var o in all)
            {
                var c = o.TryCast<Canvas>();
                if (c == null || !c.isRootCanvas) continue;
                var rec = canvases.Find(r => r.c.Pointer == c.Pointer);
                if (rec == null)
                {
                    var mode = c.renderMode;
                    if (mode == RenderMode.WorldSpace) continue;
                    if (mode == RenderMode.ScreenSpaceCamera && c.worldCamera != null && c.worldCamera.name.StartsWith("VHVR")) continue;
                    rec = Capture(c);
                    if (rec == null) continue;
                    canvases.Add(rec);
                    LogBigGraphics(c);
                    Plugin.L.LogInfo($"HUD: moved canvas '{c.name}' ({rec.mode}, {rec.size.x:0}x{rec.size.y:0}, active={c.isActiveAndEnabled}, order={c.sortingOrder}).");
                }
                SetLayers(rec);
                if (pointerCam != null) rec.c.worldCamera = pointerCam;
            }
        }

        CanvasRec Capture(Canvas c)
        {
            var rt = c.GetComponent<RectTransform>();
            if (rt == null) return null;
            var size = rt.rect.size;
            if (size.x < 1 || size.y < 1) return null;
            var rec = new CanvasRec
            {
                c = c, mode = c.renderMode, cam = c.worldCamera, plane = c.planeDistance,
                scale = rt.localScale, size = size,
                aMin = rt.anchorMin, aMax = rt.anchorMax, pivot = rt.pivot, sizeDelta = rt.sizeDelta
            };
            var scaler = c.GetComponent<UnityEngine.UI.CanvasScaler>();
            if (scaler != null) { rec.scaler = scaler; rec.scalerOn = scaler.enabled; scaler.enabled = false; }
            c.renderMode = RenderMode.WorldSpace;
            Vector2 half; half.x = 0.5f; half.y = 0.5f;
            rt.anchorMin = half; rt.anchorMax = half; rt.pivot = half;
            rt.sizeDelta = size;
            return rec;
        }

        void LogBigGraphics(Canvas c)
        {
            try
            {
                var all = c.GetComponentsInChildren<UnityEngine.UI.Graphic>(false);
                int active = 0; var names = new List<string>();
                foreach (var g in all)
                {
                    if (g == null || !g.isActiveAndEnabled) continue;
                    active++;
                    if (names.Count < 8) names.Add($"{g.name}[a={g.color.a:0.0}]");
                }
                var grp = c.GetComponent<CanvasGroup>();
                Plugin.L.LogInfo($"   '{c.name}' graphics active={active}/{all.Length} group alpha={(grp == null ? "none" : grp.alpha.ToString("0.00"))} sample: {string.Join(", ", names)}");
                var gs = c.GetComponentsInChildren<UnityEngine.UI.Graphic>(false);
                int n = 0;
                foreach (var g in gs)
                {
                    if (g == null || !g.isActiveAndEnabled) continue;
                    var r = g.rectTransform.rect;
                    var col = g.color;
                    if (r.width * r.height < 1920f * 1080f * 0.4f || col.a < 0.05f) continue;
                    Plugin.L.LogInfo($"   big graphic '{g.name}' {g.GetIl2CppType().Name} {r.width:0}x{r.height:0} rgba=({col.r:0.00},{col.g:0.00},{col.b:0.00},{col.a:0.00})");
                    if (++n > 12) break;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("graphic scan: " + e.Message); }
        }

        // Diagnostic: save what the left eye sees every 10 s (BepInEx/vhvr_eye_N.png).
        float nextSnap; int snapIndex;
        void Snapshot()
        {
            if (!Plugin.DebugSnapshots.Value || eyeRT[0] == null || Time.unscaledTime < nextSnap) return;
            nextSnap = Time.unscaledTime + 10f;
            try
            {
                var rt = eyeRT[0];
                int w = rt.width / 2, h = rt.height / 2;
                var small = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(rt, small);
                var prev = RenderTexture.active;
                RenderTexture.active = small;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                Rect rect = default; rect.width = w; rect.height = h;
                tex.ReadPixels(rect, 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(small);
                var png = ImageConversion.EncodeToPNG(tex);
                UnityEngine.Object.Destroy(tex);
                var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, $"vhvr_eye_{snapIndex % 6}.png");
                System.IO.File.WriteAllBytes(path, (byte[])png);
                foreach (var rec in canvases)
                    if (rec.c != null && rec.c.isActiveAndEnabled)
                    {
                        var ct = rec.c.transform; var p = ct.position;
                        Plugin.L.LogInfo($"   HUD '{rec.c.name}' mode={rec.c.renderMode} layer={rec.c.gameObject.layer} pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) scale={ct.lossyScale.x:0.00000} head=({headPos.x:0.00},{headPos.y:0.00},{headPos.z:0.00}) eyeMask={(eyes[0] != null ? eyes[0].cullingMask : 0):X}");
                    }
                Plugin.L.LogInfo($"   hook calls: player.Update={Patches.playerUpdateCalls} weapon={Patches.weaponCalls} hands L={handValid[0]} R={handValid[1]}");
                Plugin.L.LogInfo($"Snapshot {snapIndex % 6} saved (camera '{src?.name}', canvases {canvases.Count}, cursor {Cursor.visible}).");
                snapIndex++;
            }
            catch (Exception e) { Plugin.L.LogWarning("snapshot: " + e.Message); nextSnap = Time.unscaledTime + 60f; }
        }

        // Full-screen flat effects (shadows, hit flashes, vignettes) look wrong on a floating panel: hide them.
        // Full-screen backgrounds stay visible but stop blocking laser clicks.
        void HideClutter(CanvasRec rec)
        {
            var names = Plugin.HudHideNames.Value.Split(',');
            var gs = rec.c.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
            foreach (var g in gs)
            {
                if (g == null) continue;
                var r = g.rectTransform.rect;
                bool full = r.width >= rec.size.x * 0.85f && r.height >= rec.size.y * 0.85f;
                if (!full) continue;
                if (g.raycastTarget && rec.unblocked.Add(g.Pointer)) { rec.unblockedList.Add(g); g.raycastTarget = false; }
                if (!g.enabled) continue;
                foreach (var n in names)
                {
                    var k = n.Trim();
                    if (k.Length > 0 && g.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (rec.hidden.Add(g.Pointer)) { rec.hiddenList.Add(g); g.enabled = false; Plugin.L.LogInfo($"HUD: hid full-screen '{g.name}' in '{rec.c.name}'"); }
                        break;
                    }
                }
            }
        }

        void SetLayers(CanvasRec rec)
        {
            HideClutter(rec);
            rec.rects = new List<Transform>();
            foreach (var rt in rec.c.GetComponentsInChildren<RectTransform>(true)) if (rt != null) rec.rects.Add(rt);
            var ts = rec.c.GetComponentsInChildren<Transform>(true);
            foreach (var t in ts)
            {
                if (t == null) continue;
                var go = t.gameObject;
                if (go.layer == hudLayer) continue;
                if (rec.seen.Add(t.Pointer)) rec.layers.Add((t, go.layer));
                go.layer = hudLayer;
            }
        }

        void RestoreCanvases()
        {
            foreach (var rec in canvases)
            {
                if (rec.c == null) continue;
                foreach (var (t, layer) in rec.layers) if (t != null) t.gameObject.layer = layer;
                foreach (var g in rec.hiddenList) if (g != null) g.enabled = true;
                foreach (var g in rec.unblockedList) if (g != null) g.raycastTarget = true;

                rec.c.renderMode = rec.mode;
                rec.c.worldCamera = rec.cam;
                rec.c.planeDistance = rec.plane;
                var rt = rec.c.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.localScale = rec.scale;
                    rt.anchorMin = rec.aMin; rt.anchorMax = rec.aMax; rt.pivot = rec.pivot; rt.sizeDelta = rec.sizeDelta;
                }
                if (rec.scaler != null) rec.scaler.enabled = rec.scalerOn;
            }
            canvases.Clear();
        }

        // Crosshair graphics leave the panel and sit where the right-hand gun points.
        // Our own crosshair copies (one per hand) on our own canvases; the game's crosshair is hidden.
        // The game animates its crosshair scale every frame, so scaling the original made it grow.
        readonly GameObject[] crossCanvas = new GameObject[2];
        CanvasGroup hiddenCross; Transform crossSource;

        void PlaceCrosshairs(CanvasRec rec, float scale)
        {
            if (rec.crosshairs == null)
            {
                rec.crosshairs = new List<(Transform, Vector3)>();
                var all = rec.c.GetComponentsInChildren<Transform>(true);
                foreach (var t in all)
                {
                    if (t == null || t.Pointer == rec.c.transform.Pointer) continue;
                    if (t.name.IndexOf("cross", StringComparison.OrdinalIgnoreCase) < 0 || t.name.StartsWith("VHVR")) continue;
                    bool nested = false;
                    foreach (var (o, _) in rec.crosshairs) if (t.IsChildOf(o)) { nested = true; break; }
                    if (nested) continue;
                    rec.crosshairs.Add((t, t.localScale));
                }
            }
            if (rec.crosshairs.Count == 0 || !Plugin.HandAim.Value) return;
            var src0 = rec.crosshairs[0].t;
            if (src0 == null) return;

            if (crossSource == null || crossSource.Pointer != src0.Pointer || crossCanvas[0] == null || crossCanvas[1] == null)
            {
                crossSource = src0;
                for (int h = 0; h < 2; h++)
                {
                    if (crossCanvas[h] != null) UnityEngine.Object.Destroy(crossCanvas[h]);
                    var go = new GameObject(h == 0 ? "VHVR Cross L" : "VHVR Cross R");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    var c = go.AddComponent<Canvas>();
                    c.renderMode = RenderMode.WorldSpace;
                    c.sortingOrder = 31000;
                    var rt = go.GetComponent<RectTransform>();
                    Vector2 sz; sz.x = 100; sz.y = 100; rt.sizeDelta = sz;
                    var copy = UnityEngine.Object.Instantiate(src0.gameObject, go.transform);
                    copy.name = "Copy";
                    foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
                        if (mb != null && mb.TryCast<UnityEngine.UI.Graphic>() == null && mb.TryCast<UnityEngine.UI.Mask>() == null) mb.enabled = false;
                    foreach (var cg in copy.GetComponentsInChildren<CanvasGroup>(true)) if (cg != null) cg.alpha = 1f;
                    var ct = copy.GetComponent<RectTransform>();
                    if (ct != null) { Vector2 half; half.x = half.y = 0.5f; ct.anchorMin = half; ct.anchorMax = half; ct.anchoredPosition = default; }
                    copy.transform.localPosition = default; copy.transform.localRotation = Identity;
                    copy.transform.localScale = V(1, 1, 1);
                    foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = hudLayer;
                    crossCanvas[h] = go;
                }
                hiddenCross = src0.GetComponent<CanvasGroup>();
                if (hiddenCross == null) hiddenCross = src0.gameObject.AddComponent<CanvasGroup>();
                Plugin.L.LogInfo($"HUD: crosshair copies made from '{src0.name}'");
            }
            if (hiddenCross != null) hiddenCross.alpha = 0f;

            for (int h = 0; h < 2; h++)
            {
                var root = HandRoot(h);
                bool on = handValid[h] && root != null && root.gameObject.activeInHierarchy;
                if (crossCanvas[h].activeSelf != on) crossCanvas[h].SetActive(on);
                if (on) PlaceOneCrosshair(crossCanvas[h].transform, h, scale);
            }
        }

        void HideCrosshairCopies()
        {
            for (int h = 0; h < 2; h++) if (crossCanvas[h] != null) crossCanvas[h].SetActive(false);
            if (hiddenCross != null) hiddenCross.alpha = 1f;
        }

        void PlaceOneCrosshair(Transform t, int h, float scale)
        {
            var dir = Rotate(handRot[h], V(0, 0, 1));
            var start = Add(handPos[h], Mul(dir, 0.3f * scale));
            float maxD = 40f * scale, d = maxD;
            try
            {
                if (Physics.Raycast(start, dir, out RaycastHit hit, maxD, ~0, QueryTriggerInteraction.Ignore)) d = Math.Max(0.5f * scale, hit.distance);
            }
            catch { }
            var point = Add(start, Mul(dir, d));
            var fromHead = Sub(point, headPos);
            float dist = (float)Math.Sqrt(fromHead.x * fromHead.x + fromHead.y * fromHead.y + fromHead.z * fromHead.z);
            var rot = Quaternion.LookRotation(fromHead, Rotate(headRot, V(0, 1, 0)));
            // Same apparent size as on the HUD panel, at any distance.
            float unitAtHud = Plugin.HudWidth.Value * scale / 1920f;
            float k = unitAtHud * dist / Math.Max(0.1f, Plugin.HudDistance.Value * scale) * Mathf.Max(0.05f, Plugin.CrosshairSize.Value);
            t.SetPositionAndRotation(point, rot);
            t.localScale = V(k, k, k);
        }

        // ---------- Menus shown exactly like the flat game: screen capture on a big locked virtual screen ----------
        bool flatActive; GameObject screenGo; UnityEngine.UI.RawImage screenImg; RenderTexture screenRT;
        bool FlatMenu => vrOn && !introActive && Plugin.HudInVR.Value && Plugin.FlatScreenMenus.Value && Cursor.visible;

        void SetFlatMenu(bool on)
        {
            flatActive = on;
            if (on)
            {
                // Give the menus back to the game in normal screen mode; we show the finished screen image instead.
                HideCrosshairCopies();
                RestoreCanvases();
                if (reticle != null) reticle.SetActive(false);
                hoverGo = null; pendingGo = null; navSel = null;
                EnsureScreen();
                screenGo.SetActive(true);
                menuLocked = false;
                Plugin.L.LogInfo("Menu: flat screen mode");
            }
            else
            {
                if (screenGo != null) screenGo.SetActive(false);
                if (laser != null) laser.SetActive(false);
                if (reticle != null) reticle.SetActive(false);
                nextScan = 0;
                Plugin.L.LogInfo("Menu closed: HUD mode");
            }
        }

        void EnsureScreen()
        {
            int w = Math.Max(640, Screen.width), h = Math.Max(360, Screen.height);
            if (screenRT == null || screenRT.width != w || screenRT.height != h)
            {
                if (screenRT != null) { screenRT.Release(); UnityEngine.Object.Destroy(screenRT); }
                screenRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                screenRT.name = "VHVR Menu Screen";
                screenRT.Create();
            }
            if (screenGo == null)
            {
                screenGo = new GameObject("VHVR Screen");
                UnityEngine.Object.DontDestroyOnLoad(screenGo);
                var c = screenGo.AddComponent<Canvas>();
                c.renderMode = RenderMode.WorldSpace;
                c.sortingOrder = 30000;
                var imgGo = new GameObject("Image");
                imgGo.transform.SetParent(screenGo.transform, false);
                screenImg = imgGo.AddComponent<UnityEngine.UI.RawImage>();
                screenImg.raycastTarget = false;
                screenGo.layer = hudLayer; imgGo.layer = hudLayer;
            }
            Vector2 sz; sz.x = w; sz.y = h;
            screenGo.GetComponent<RectTransform>().sizeDelta = sz;
            var irt = screenImg.GetComponent<RectTransform>();
            irt.sizeDelta = sz;
            screenImg.texture = screenRT;
            Rect uv = default;
            bool flip = Plugin.FlipMenuScreen.Value;
            uv.x = 0; uv.width = 1; uv.y = flip ? 1 : 0; uv.height = flip ? -1 : 1;
            screenImg.uvRect = uv;
        }

        void PlaceScreen(float scale)
        {
            if (screenGo == null) return;
            if (!menuLocked)
            {
                menuLocked = true;
                var rot = Yaw(YawOf(headRot));
                var pos = Add(headPos, Rotate(rot, V(0, Plugin.HudHeightOffset.Value * scale, Plugin.MenuDistance.Value * scale)));
                var invBase = Inverse(lastBaseRot);
                menuLocalPos = Rotate(invBase, Sub(pos, lastBasePos));
                menuLocalRot = Mul(invBase, rot);
            }
            hudPos = Add(lastBasePos, Rotate(lastBaseRot, menuLocalPos));
            hudRot = Mul(lastBaseRot, menuLocalRot);
            screenGo.transform.SetPositionAndRotation(hudPos, hudRot);
            float sw = Math.Max(1, screenRT != null ? screenRT.width : Screen.width);
            float s = Plugin.MenuWidth.Value * scale / sw;
            screenGo.transform.localScale = V(s, s, s);
        }

        // Laser hits the virtual screen -> move the real mouse to that spot; trigger is a real left click.
        float smoothU, smoothV; bool smoothInit;
        void UpdateScreenPointer()
        {
            pointerHit = false;
            // Locked cursor ignores mouse moves; menus need it free (the game re-locks when the menu closes).
            if (Cursor.lockState == CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Confined;
            if (!handValid[1] || screenGo == null || screenRT == null) { if (laser != null) laser.SetActive(false); return; }
            var o = handPos[1];
            var dir = Rotate(handRot[1], V(0, 0, 1));
            var n = Rotate(hudRot, V(0, 0, 1));
            float denom = dir.x * n.x + dir.y * n.y + dir.z * n.z;
            float scale = Mathf.Max(0.01f, Plugin.WorldUnitsPerMeter.Value);
            if (denom > 1e-4f)
            {
                var toPlane = Sub(hudPos, o);
                float t = (toPlane.x * n.x + toPlane.y * n.y + toPlane.z * n.z) / denom;
                if (t > 0)
                {
                    var hit = Add(o, Mul(dir, t));
                    var local = Rotate(Inverse(hudRot), Sub(hit, hudPos));
                    float unit = screenGo.transform.lossyScale.x;
                    float u = local.x / unit / screenRT.width + 0.5f;
                    float v = local.y / unit / screenRT.height + 0.5f;
                    // Smooth hand tremor (magnified on a big screen); snaps when you move fast.
                    float k = Mathf.Clamp01(Plugin.MenuPointerSmoothing.Value);
                    if (smoothInit && k > 0)
                    {
                        float du = u - smoothU, dv = v - smoothV;
                        float jump = (float)Math.Sqrt(du * du + dv * dv);
                        float a = jump > 0.08f ? 1f : 1f - k;
                        u = smoothU + du * a; v = smoothV + dv * a;
                    }
                    smoothU = u; smoothV = v; smoothInit = true;
                    hit = Add(hudPos, Rotate(hudRot, V((u - 0.5f) * screenRT.width * unit, (v - 0.5f) * screenRT.height * unit, 0)));
                    if (u >= 0 && u <= 1 && v >= 0 && v <= 1)
                    {
                        pointerHit = true; pointerHitPos = hit;
                        if (InputEmu.GameFocused())
                        {
                            var mouse = UnityEngine.InputSystem.Mouse.current;
                            Vector2 mp; mp.x = u * Screen.width; mp.y = v * Screen.height;
                            try { if (mouse != null) mouse.WarpCursorPosition(mp); } catch { }
                        }
                    }
                }
            }
            var end = pointerHit ? pointerHitPos : Add(o, Mul(dir, 3f * scale));
            PlaceLaser(o, end);
        }

        Vector3 lastBasePos; Quaternion lastBaseRot = Identity;
        bool menuLocked; Vector3 menuLocalPos; Quaternion menuLocalRot;

        void PlaceHud(float scale)
        {
            if (canvases.Count == 0) return;
            if (Cursor.visible && Plugin.LockMenus.Value)
            {
                // Menus stay put (relative to your body) so you can look around and read them.
                if (!menuLocked)
                {
                    menuLocked = true;
                    var rot = Yaw(YawOf(headRot));
                    var pos = Add(headPos, Rotate(rot, V(0, Plugin.HudHeightOffset.Value * scale, Plugin.MenuDistance.Value * scale)));
                    var invBase = Inverse(lastBaseRot);
                    menuLocalPos = Rotate(invBase, Sub(pos, lastBasePos));
                    menuLocalRot = Mul(invBase, rot);
                }
                hudPos = Add(lastBasePos, Rotate(lastBaseRot, menuLocalPos));
                hudRot = Mul(lastBaseRot, menuLocalRot);
                hudInit = false;
            }
            else
            {
                menuLocked = false;
                float speed = Plugin.HudFollowSpeed.Value;
                if (!hudInit || speed <= 0) { hudRot = headRot; hudInit = true; }
                else hudRot = Nlerp(hudRot, headRot, 1f - (float)Math.Exp(-speed * Time.unscaledDeltaTime));
                hudPos = Add(headPos, Rotate(hudRot, V(0, Plugin.HudHeightOffset.Value * scale, Plugin.HudDistance.Value * scale)));
            }

            canvases.Sort((a, b) => (a.c == null ? 0 : a.c.sortingOrder).CompareTo(b.c == null ? 0 : b.c.sortingOrder));
            float width = (Cursor.visible ? Plugin.MenuWidth.Value : Plugin.HudWidth.Value) * scale;
            for (int i = 0; i < canvases.Count; i++)
            {
                var rec = canvases[i];
                if (rec.c == null) continue;
                var t = rec.c.transform;
                var p = Add(hudPos, Rotate(hudRot, V(0, 0, -0.004f * scale * i)));
                t.SetPositionAndRotation(p, hudRot);
                float s = width / rec.size.x;
                var parent = t.parent;
                float ps = parent == null ? 1f : Math.Max(1e-6f, parent.lossyScale.x);
                t.localScale = V(s / ps, s / ps, s / ps);
                if (rec.c.name.IndexOf("cross", StringComparison.OrdinalIgnoreCase) >= 0 && rec.c.isActiveAndEnabled) PlaceCrosshairs(rec, scale);
            }
        }

        // Laser pointer: when the game shows a cursor, aim at menus with the right controller.
        GameObject reticle; RectTransform reticleRect;
        GameObject hoverGo;
        bool pointerHit; Vector3 pointerHitPos;

        bool UiMode => Plugin.ControllerInput.Value && Plugin.HudInVR.Value && Cursor.visible && handValid[1] && canvases.Count > 0 && LaserMenus;
        static bool LaserMenus => !Plugin.MenuMode.Value.Equals("Stick", StringComparison.OrdinalIgnoreCase);
        static bool StickMenus => !Plugin.MenuMode.Value.Equals("Laser", StringComparison.OrdinalIgnoreCase);
        bool StickMenu => Plugin.ControllerInput.Value && Plugin.HudInVR.Value && Cursor.visible && canvases.Count > 0 && StickMenus;

        void UpdatePointer()
        {
            if (flatActive) { UpdateScreenPointer(); return; }
            if (!UiMode) { if (reticle != null) reticle.SetActive(false); if (laser != null) laser.SetActive(false); return; }
            if (pointerCam == null)
            {
                pointerCam = NewCam("VHVR Pointer");
                pointerCam.cullingMask = 1 << hudLayer;
                pointerCam.fieldOfView = 30f;
                pointerCam.enabled = false;
                foreach (var rec in canvases) if (rec.c != null) rec.c.worldCamera = pointerCam;
            }
            pointerCam.transform.SetPositionAndRotation(handPos[1], handRot[1]);
            pointerCam.nearClipPlane = 0.01f;
            pointerCam.farClipPlane = 50f * Mathf.Max(0.01f, Plugin.WorldUnitsPerMeter.Value);
            // Put the real cursor at the laser so the game's own UI input sees hover + clicks.

            PlaceReticle();
        }

        void EnsureReticle()
        {
            if (reticle != null) return;
            reticle = new GameObject("VHVR Reticle");
            UnityEngine.Object.DontDestroyOnLoad(reticle);
            var c = reticle.AddComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            c.sortingOrder = 32000;
            var dot = new GameObject("Dot");
            dot.transform.SetParent(reticle.transform, false);
            var img = dot.AddComponent<UnityEngine.UI.Image>();
            img.raycastTarget = false;
            Color col; col.r = 1f; col.g = 0.25f; col.b = 0.25f; col.a = 0.95f;
            img.color = col;
            var rt = dot.GetComponent<RectTransform>();
            Vector2 sz; sz.x = 1f; sz.y = 1f; rt.sizeDelta = sz;
            reticleRect = reticle.GetComponent<RectTransform>();
            reticleRect.sizeDelta = sz;
            reticle.layer = hudLayer; dot.layer = hudLayer;
        }

        GameObject laser;
        void PlaceLaser(Vector3 from, Vector3 to)
        {
            if (laser == null)
            {
                laser = new GameObject("VHVR Laser");
                UnityEngine.Object.DontDestroyOnLoad(laser);
                var c = laser.AddComponent<Canvas>();
                c.renderMode = RenderMode.WorldSpace;
                c.sortingOrder = 31999;
                var beam = new GameObject("Beam");
                beam.transform.SetParent(laser.transform, false);
                var img = beam.AddComponent<UnityEngine.UI.Image>();
                img.raycastTarget = false;
                Color col; col.r = 1f; col.g = 0.15f; col.b = 0.15f; col.a = 0.55f;
                img.color = col;
                Vector2 one; one.x = 1f; one.y = 1f;
                beam.GetComponent<RectTransform>().sizeDelta = one;
                laser.GetComponent<RectTransform>().sizeDelta = one;
                laser.layer = hudLayer; beam.layer = hudLayer;
            }
            var d = Sub(to, from);
            float len = (float)Math.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
            if (len < 1e-4f) { laser.SetActive(false); return; }
            laser.SetActive(true);
            var dir = Mul(d, 1f / len);
            // Flat strip along the ray, turned to face the head.
            var mid = Add(from, Mul(d, 0.5f));
            var away = Sub(mid, headPos);
            float along = away.x * dir.x + away.y * dir.y + away.z * dir.z;
            var f = Sub(away, Mul(dir, along));   // perpendicular to beam, pointing away from head
            if (f.x * f.x + f.y * f.y + f.z * f.z < 1e-8f) f = Rotate(headRot, V(0, 0, 1));
            var rot = Quaternion.LookRotation(f, dir); // local Y exactly along the beam
            laser.transform.SetPositionAndRotation(mid, rot);
            float w = 0.004f * Mathf.Max(0.01f, Plugin.WorldUnitsPerMeter.Value);
            laser.transform.localScale = V(w, len, 1f);
        }

        void PlaceReticle()
        {
            EnsureReticle();
            if (handValid[1])
            {
                float far = 3f * Mathf.Max(0.01f, Plugin.WorldUnitsPerMeter.Value);
                var end = pointerHit ? pointerHitPos : Add(handPos[1], Rotate(handRot[1], V(0, 0, far)));
                PlaceLaser(handPos[1], end);
            }
            if (!pointerHit && !navMarkerOn) { reticle.SetActive(false); return; }
            reticle.SetActive(true);
            float scale = Mathf.Max(0.01f, Plugin.WorldUnitsPerMeter.Value);
            var toward = Rotate(hudRot, V(0, 0, -0.01f * scale));
            reticle.transform.SetPositionAndRotation(Add(pointerHit ? pointerHitPos : navMarkerPos, toward), hudRot);
            float d = 0.012f * scale;
            reticle.transform.localScale = V(d, d, d);
        }

        static bool KeyboardMode => !Plugin.InputMode.Value.Equals("Direct", StringComparison.OrdinalIgnoreCase);
        bool turnLatch; float nextStickLog; bool focusedOnce; float nextScroll;
        readonly Vector2[] rawAxis = new Vector2[3];

        // Controllers -> keyboard/mouse. Works in hub, menus and runs.
        void EmulateKeys()
        {
            if (!Plugin.ControllerInput.Value || !KeyboardMode) return;
            if (!InputEmu.GameFocused())
            {
                InputEmu.ReleaseAll();
                if (Plugin.AutoFocus.Value)
                {
                    bool used = Math.Abs(lStick.x) + Math.Abs(lStick.y) + Math.Abs(rStick.x) + Math.Abs(rStick.y) > 0.5f;
                    for (int i = 0; i < btn.Length && !used; i++) used = btn[i];
                    if (used || !focusedOnce) InputEmu.TryFocus(Time.unscaledTime, used ? "controller used" : "VR start");
                }
                return;
            }
            focusedOnce = true;
            InputEmu.Begin();

            const float t = 0.2f;
            bool menu = Cursor.visible;
            if (menu)
            {
                // Menus: stick = arrow keys, A = Enter (game's UI Navigate/Submit bindings).
                foreach (var k in new[] { Plugin.KeyForward.Value, Plugin.KeyBack.Value, Plugin.KeyLeft.Value, Plugin.KeyRight.Value, Plugin.KeyA.Value }) InputEmu.Set(k, false);
                InputEmu.Set("Up", lStick.y > 0.5f);
                InputEmu.Set("Down", lStick.y < -0.5f);
                InputEmu.Set("Left", lStick.x < -0.5f);
                InputEmu.Set("Right", lStick.x > 0.5f);
                InputEmu.Set("Enter", btn[(int)B.RA]);
            }
            else
            {
                foreach (var k in new[] { "Up", "Down", "Left", "Right", "Enter" }) InputEmu.Set(k, false);
            }
            if (Time.unscaledTime >= nextStickLog && (Math.Abs(lStick.x) + Math.Abs(lStick.y) + Math.Abs(rStick.x) > 0.2f || btn[(int)B.RA]))
            {
                nextStickLog = Time.unscaledTime + 1f;
                Plugin.L.LogInfo($"Sticks L=({lStick.x:0.00},{lStick.y:0.00}) R=({rStick.x:0.00},{rStick.y:0.00}) raw L0=({rawAxis[0].x:0.00},{rawAxis[0].y:0.00}) L2=({rawAxis[1].x:0.00},{rawAxis[1].y:0.00}) L3=({rawAxis[2].x:0.00},{rawAxis[2].y:0.00})");
            }
            if (!menu)
            {
                // Rotate the stick from "where I look" into the game body's frame before turning it into WASD,
                // so forward stays forward even after you physically turn around.
                var mv = BodyRelativeStick(lStick);
                float m = (float)Math.Sqrt(mv.x * mv.x + mv.y * mv.y);
                if (m > 0.001f) { mv.x /= m; mv.y /= m; }
                bool on = m > t;
                // 8-way: each key needs at least ~22 degrees toward its direction.
                InputEmu.Set(Plugin.KeyForward.Value, on && mv.y > 0.38f);
                InputEmu.Set(Plugin.KeyBack.Value, on && mv.y < -0.38f);
                InputEmu.Set(Plugin.KeyLeft.Value, on && mv.x < -0.38f);
                InputEmu.Set(Plugin.KeyRight.Value, on && mv.x > 0.38f);
            }

            // Laser on a menu: the trigger clicks the menu directly, so no real mouse click.
            InputEmu.Set(Plugin.KeyRightTrigger.Value, btn[(int)B.RTrig] && !(UiMode && pointerHit) && !(StickMenu && navSel != null));
            InputEmu.Set(Plugin.KeyLeftTrigger.Value, btn[(int)B.LTrig]);
            if (!menu) InputEmu.Set(Plugin.KeyA.Value, btn[(int)B.RA]);
            InputEmu.Set(Plugin.KeyB.Value, btn[(int)B.RB]);
            InputEmu.Set(Plugin.KeyX.Value, btn[(int)B.LA]);
            InputEmu.Set(Plugin.KeyY.Value, btn[(int)B.LB]);
            InputEmu.Set(Plugin.KeyLeftGrip.Value, btn[(int)B.LGrip]);
            InputEmu.Set(Plugin.KeyRightGrip.Value, btn[(int)B.RGrip]);
            InputEmu.Set(Plugin.KeyLeftStickClick.Value, btn[(int)B.LStick]);
            InputEmu.Set(Plugin.KeyRightStickClick.Value, btn[(int)B.RStick]);
            InputEmu.End();

            if (menu && flatActive && Math.Abs(rStick.y) > 0.4f && Time.unscaledTime >= nextScroll)
            {
                nextScroll = Time.unscaledTime + 0.12f;
                InputEmu.Wheel(rStick.y > 0 ? 120 : -120);
            }
            // Turning moves the real mouse so the game body turns with you (not in menus: stick navigates).
            float x = menu ? 0f : rStick.x;
            var mode = Plugin.TurnMode.Value;
            if (mode.Equals("Snap", StringComparison.OrdinalIgnoreCase))
            {
                if (!turnLatch && Math.Abs(x) > 0.7f) { turnLatch = true; InputEmu.MouseMove(Math.Sign(x) * Plugin.SnapTurnMousePixels.Value); }
                else if (turnLatch && Math.Abs(x) < 0.3f) turnLatch = false;
            }
            else if (mode.Equals("Smooth", StringComparison.OrdinalIgnoreCase) && Math.Abs(x) > 0.15f)
                InputEmu.MouseMove(x * Plugin.SmoothTurnMousePixels.Value * Time.unscaledDeltaTime);
        }

        bool dumpedBindings; float dumpAt = -1;
        void DumpBindingsOnce()
        {
            if (dumpedBindings || !Plugin.DebugSnapshots.Value) return;
            if (dumpAt < 0) { dumpAt = Time.unscaledTime + 5f; return; }
            if (Time.unscaledTime < dumpAt) return;
            dumpedBindings = true;
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UnityEngine.InputSystem.InputActionAsset>());
                int n = 0;
                foreach (var o in all)
                {
                    var a = o.TryCast<UnityEngine.InputSystem.InputActionAsset>();
                    if (a == null) continue;
                    var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, $"vhvr_bindings_{n++}_{a.name}.json");
                    System.IO.File.WriteAllText(path, a.ToJson());
                }
                Plugin.L.LogInfo($"Dumped {n} input binding files.");
            }
            catch (Exception e) { Plugin.L.LogWarning("bindings dump: " + e.Message); }
        }

        float watchUntil, nextWatch;
        // Diagnostic after a menu click: what cameras/scene the game switches to.
        void WatchAfterClick()
        {
            if (Time.unscaledTime > watchUntil || Time.unscaledTime < nextWatch) return;
            nextWatch = Time.unscaledTime + 1f;
            try
            {
                var names = new List<string>();
                foreach (var c in Camera.allCameras)
                    if (c != null && !c.name.StartsWith("VHVR")) names.Add($"{c.name}{(c.CompareTag("MainCamera") ? "*" : "")}(d{c.depth:0},rt={(c.targetTexture != null)})");
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                Plugin.L.LogInfo($"  watch: scene={scene} main={(Camera.main == null ? "null" : Camera.main.name)} src={src?.name} cams=[{string.Join(", ", names)}] timeScale={Time.timeScale}");
            }
            catch (Exception e) { Plugin.L.LogWarning("watch: " + e.Message); watchUntil = 0; }
        }

        // Raycast the moved menu canvases from the controller and click with the trigger.
        // Shop cards tilt toward the mouse using flat-screen math; on the VR panel that spins them edge-on.
        void FlattenMenus()
        {
            if (!Cursor.visible) return;
            foreach (var rec in canvases)
            {
                if (rec.c == null || !rec.c.isActiveAndEnabled || rec.rects == null) continue;
                foreach (var t in rec.rects)
                {
                    if (t == null) continue;
                    var q = t.localRotation;
                    if (Math.Abs(q.x) < 1e-4f && Math.Abs(q.y) < 1e-4f) continue;
                    var e = t.localEulerAngles;
                    t.localEulerAngles = V(0, 0, e.z);
                }
            }
        }

        void HandleUiClicks()
        {
            FlattenMenus();
            pointerHit = false;
            if (!UiMode || pointerCam == null) { hoverGo = null; return; }
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return;

            var ped = new UnityEngine.EventSystems.PointerEventData(es);
            Vector2 center; center.x = pointerCam.pixelWidth * 0.5f; center.y = pointerCam.pixelHeight * 0.5f;
            ped.position = center;
            ped.pressPosition = center;
            var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(ped, results);

            GameObject hit = null;
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r.gameObject == null) continue;
                hit = r.gameObject; pointerHit = true; pointerHitPos = r.worldPosition;
                try { ped.pointerCurrentRaycast = r; ped.pointerPressRaycast = r; ped.pointerEnter = hit; ped.rawPointerPress = hit; } catch { }
                break;
            }

            // Hover: select nearest Selectable so the game highlights it (same as gamepad navigation).
            if (hit != null)
            {
                var sel = hit.GetComponentInParent<UnityEngine.UI.Selectable>();
                var target = sel != null ? sel.gameObject : hit;
                if (hoverGo == null || hoverGo.Pointer != target.Pointer)
                {
                    if (hoverGo != null) { try { UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hoverGo, ped, UnityEngine.EventSystems.ExecuteEvents.pointerExitHandler); } catch { } }
                    hoverGo = target;
                    try { UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hit, ped, UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler); } catch { }
                    if (sel != null && sel.IsInteractable()) { try { es.SetSelectedGameObject(target); } catch { } }
                }
            }
            else
            {
                if (hoverGo != null) { try { UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hoverGo, ped, UnityEngine.EventSystems.ExecuteEvents.pointerExitHandler); } catch { } }
                hoverGo = null;
            }

            bool down = btn[(int)B.RTrig] && !prevBtn[(int)B.RTrig];
            bool up = !btn[(int)B.RTrig] && prevBtn[(int)B.RTrig];
            if (down && hit != null)
            {
                if (IsLockBan(hit)) { pendingGo = hit; pendingTime = Time.unscaledTime; }
                else DoClick(hit, ped);
            }
            if (pendingGo != null && btn[(int)B.RTrig] && Time.unscaledTime - pendingTime >= 0.8f)
            {
                var g = pendingGo; pendingGo = null;
                Plugin.L.LogInfo("UI hold: lock/ban");
                DoClick(g, ped);
            }
            if (up && pendingGo != null)
            {
                // Quick tap on the hover Lock/Ban buttons = buy the card underneath.
                var g = pendingGo; pendingGo = null;
                var card = CardRoot(g);
                DoClick(card != null ? card : g, ped);
            }
        }

        GameObject pendingGo; float pendingTime;

        // ---------- Stick menu navigation: right stick moves highlight, right trigger picks ----------
        GameObject navSel; float navRepeat; bool navLatch; bool wasMenu;

        float navUsed = -100f; bool navMarkerOn; Vector3 navMarkerPos;

        void StickNavigate()
        {
            bool menu = StickMenu;
            navMarkerOn = false;
            if (!menu) { if (wasMenu) { SetHover(null); navSel = null; } wasMenu = false; return; }
            wasMenu = true;
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return;
            FlattenMenus();

            var st = rStick;
            float mag = Math.Max(Math.Abs(st.x), Math.Abs(st.y));
            if (mag < 0.35f) navLatch = false;
            else if (!navLatch || Time.unscaledTime >= navRepeat)
            {
                navRepeat = Time.unscaledTime + (navLatch ? 0.18f : 0.4f);
                navLatch = true;
                navUsed = Time.unscaledTime;
                var cands = NavCandidates();
                if (cands.Count > 0)
                {
                    int ci = navSel == null ? -1 : cands.FindIndex(c => c.go.Pointer == navSel.Pointer);
                    if (ci < 0)
                    {
                        // First push: start from what the laser points at, else the panel centre.
                        float rx = 0, ry = 0;
                        if (pointerHit) { var lp = Rotate(Inverse(hudRot), Sub(pointerHitPos, hudPos)); float u = canvases[0].c != null ? canvases[0].c.transform.lossyScale.x : 1f; rx = lp.x / u; ry = lp.y / u; }
                        float best = float.MaxValue; var first = cands[0];
                        foreach (var c in cands) { float d = (c.x - rx) * (c.x - rx) + (c.y - ry) * (c.y - ry); if (d < best) { best = d; first = c; } }
                        Select(first.go, es);
                    }
                    else
                    {
                        float dx = 0, dy = 0;
                        if (Math.Abs(st.x) >= Math.Abs(st.y)) dx = Math.Sign(st.x); else dy = Math.Sign(st.y);
                        var cur = cands[ci];
                        (GameObject go, float x, float y) pick = default; float bestScore = float.MaxValue;
                        foreach (var c in cands)
                        {
                            if (c.go.Pointer == navSel.Pointer) continue;
                            float ox = c.x - cur.x, oy = c.y - cur.y;
                            float along = ox * dx + oy * dy;
                            if (along <= 1f) continue;
                            float side = Math.Abs(ox * dy - oy * dx);
                            float score = along + side * 2.5f;
                            if (score < bestScore) { bestScore = score; pick = c; }
                        }
                        if (pick.go != null) Select(pick.go, es);
                    }
                }
            }

            bool navActive = navSel != null && navSel.activeInHierarchy && Time.unscaledTime - navUsed < 15f;
            if (!navActive) return;
            var rt = navSel.GetComponent<RectTransform>();
            if (rt != null) { navMarkerOn = true; navMarkerPos = rt.position; }

            // Trigger picks the stick selection unless the laser is on a menu item (then the laser click wins).
            if (btn[(int)B.RTrig] && !prevBtn[(int)B.RTrig] && !(UiMode && pointerHit))
            {
                var ped = new UnityEngine.EventSystems.PointerEventData(es);
                Vector2 c0; c0.x = Screen.width * 0.5f; c0.y = Screen.height * 0.5f;
                ped.position = c0; ped.pressPosition = c0;
                DoClick(navSel, ped);
            }
        }

        void Select(GameObject go, UnityEngine.EventSystems.EventSystem es)
        {
            navSel = go;
            try { es.SetSelectedGameObject(go); } catch { }
            SetHover(go);
        }

        GameObject navHover;
        void SetHover(GameObject go)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return;
            var ped = new UnityEngine.EventSystems.PointerEventData(es);
            if (navHover != null && (go == null || navHover.Pointer != go.Pointer))
                try { UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(navHover, ped, UnityEngine.EventSystems.ExecuteEvents.pointerExitHandler); } catch { }
            if (go != null && (navHover == null || navHover.Pointer != go.Pointer))
                try { UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(go, ped, UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler); } catch { }
            navHover = go;
        }

        // Interactable menu items with their position on the panel (canvas units, x right / y up).
        List<(GameObject go, float x, float y)> NavCandidates()
        {
            var list = new List<(GameObject, float, float)>();
            var inv = Inverse(hudRot);
            foreach (var rec in canvases)
            {
                if (rec.c == null || !rec.c.isActiveAndEnabled) continue;
                var sels = rec.c.GetComponentsInChildren<UnityEngine.UI.Selectable>(false);
                float unit = rec.c.transform.lossyScale.x;
                if (unit <= 0) continue;
                foreach (var sel in sels)
                {
                    if (sel == null || !sel.IsInteractable() || !sel.isActiveAndEnabled) continue;
                    var go = sel.gameObject;
                    if (IsLockBan(go)) continue;
                    var r = sel.GetComponent<RectTransform>();
                    if (r == null || r.rect.width < 4 || r.rect.height < 4) continue;
                    // Must actually be visible: no faded-out/blocked parent group, inside the panel.
                    bool hidden = false;
                    foreach (var g in sel.GetComponentsInParent<CanvasGroup>()) if (g != null && (g.alpha < 0.1f || !g.interactable || !g.blocksRaycasts)) { hidden = true; break; }
                    if (hidden) continue;
                    var lp = Rotate(inv, Sub(r.position, hudPos));
                    if (Math.Abs(lp.x / unit) > rec.size.x * 0.5f || Math.Abs(lp.y / unit) > rec.size.y * 0.5f) continue;
                    // Skip things nested inside another candidate card (prefer the card itself).
                    list.Add((go, lp.x / unit, lp.y / unit));
                }
            }
            // Drop children of shop cards so the stick moves card to card.
            list.RemoveAll(c => { var t = c.Item1.transform.parent; for (int i = 0; i < 8 && t != null; i++, t = t.parent) if (t.name.StartsWith("Choose_") && !t.name.Contains("child")) return true; return false; });
            return list;
        }

        static bool IsLockBan(GameObject g)
        {
            var t = g.transform;
            for (int i = 0; i < 8 && t != null; i++, t = t.parent)
            {
                var n = t.name;
                if (n.IndexOf("Button Lock", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Button Ban", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static GameObject CardRoot(GameObject g)
        {
            var t = g.transform;
            for (int i = 0; i < 10 && t != null; i++, t = t.parent)
                if (t.name.StartsWith("Choose_", StringComparison.OrdinalIgnoreCase) && t.name.IndexOf("child", StringComparison.OrdinalIgnoreCase) < 0) return t.gameObject;
            return null;
        }

        void DoClick(GameObject hit, UnityEngine.EventSystems.PointerEventData ped)
        {


            bool done = false;
            ped.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
            ped.clickCount = 1;
            ped.eligibleForClick = true;
            ped.clickTime = Time.unscaledTime;
            // Full press sequence: some game buttons react to down/up, not click.
            GameObject downGo = null, upGo = null;
            try { downGo = UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hit, ped, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler); } catch (Exception e) { Plugin.L.LogWarning("UI down: " + e.Message); }
            if (downGo != null) { ped.pointerPress = downGo; ped.rawPointerPress = hit; }
            try { upGo = UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hit, ped, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler); } catch (Exception e) { Plugin.L.LogWarning("UI up: " + e.Message); }
            try { done = UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hit, ped, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler) != null; }
            catch (Exception e) { Plugin.L.LogWarning("UI click: " + e.Message); }
            if (!done)
            {
                try { done = UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hit, ped, UnityEngine.EventSystems.ExecuteEvents.submitHandler) != null; }
                catch { }
            }
            if (!done)
            {
                var b = hit.GetComponentInParent<UnityEngine.UI.Button>();
                if (b != null && b.IsInteractable()) { b.onClick.Invoke(); done = true; }
            }
            string path = hit.name; var tr = hit.transform.parent;
            for (int i = 0; i < 6 && tr != null; i++, tr = tr.parent) path = tr.name + "/" + path;
            string comps = "";
            try
            {
                var tgt = downGo != null ? downGo : hit;
                foreach (var mb in tgt.GetComponents<MonoBehaviour>()) if (mb != null) comps += mb.GetIl2CppType().Name + " ";
            }
            catch { }
            Plugin.L.LogInfo($"UI click on '{path}' handled={done} down={(downGo == null ? "-" : downGo.name)} up={(upGo == null ? "-" : upGo.name)} comps=[{comps.Trim()}]");
            watchUntil = Time.unscaledTime + 12f; nextWatch = 0;
                }

        // =====================================================================
        // Controller input
        // =====================================================================
        void PollControllers()
        {
            Array.Copy(btn, prevBtn, btn.Length);
            for (int i = 0; i < btn.Length; i++) btn[i] = false;
            lStick = default; rStick = default;
            if (!Plugin.ControllerInput.Value || sys == null) return;

            for (int h = 0; h < 2; h++)
            {
                VRControllerState_t st; bool ok;
                lock (sDataLock) { st = sCtrl[h]; ok = sCtrlOk[h]; }
                if (!ok) continue;
                if (!loggedCtrl[h]) { loggedCtrl[h] = true; Plugin.L.LogInfo((h == 0 ? "Left" : "Right") + " controller connected."); }
                ulong pressed = st.ulButtonPressed;
                bool Btn(EVRButtonId id) => (pressed & (1ul << (int)id)) != 0;

                // Thumbstick may be reported on axis 0 or 2 depending on controller bindings.
                Vector2 a0; a0.x = st.rAxis0.x; a0.y = st.rAxis0.y;
                Vector2 a2; a2.x = st.rAxis2.x; a2.y = st.rAxis2.y;
                Vector2 a3; a3.x = st.rAxis3.x; a3.y = st.rAxis3.y;
                // Quest Touch over legacy bindings splits stick X/Y across axes; take the strongest per component.
                // Thumbstick is axis 0. Quest grips report their squeeze on axis 2, which used to look like a stick push.
                Vector2 stick = a0;
                if (Plugin.StickAxis.Value == 2) stick = a2;
                if (h == 0) { rawAxis[0] = a0; rawAxis[1] = a2; rawAxis[2] = a3; }
                bool trig = st.rAxis1.x > 0.55f || Btn(EVRButtonId.k_EButton_SteamVR_Trigger);
                bool a = Btn(EVRButtonId.k_EButton_A);
                bool b = Btn(EVRButtonId.k_EButton_ApplicationMenu);
                bool grip = Btn(EVRButtonId.k_EButton_Grip);
                bool click = Btn(EVRButtonId.k_EButton_SteamVR_Touchpad);

                if (h == 0)
                {
                    lStick = stick; btn[(int)B.LTrig] = trig; btn[(int)B.LA] = a; btn[(int)B.LB] = b;
                    btn[(int)B.LGrip] = grip; btn[(int)B.LStick] = click;
                }
                else
                {
                    rStick = stick; btn[(int)B.RTrig] = trig; btn[(int)B.RA] = a; btn[(int)B.RB] = b;
                    btn[(int)B.RGrip] = grip; btn[(int)B.RStick] = click;
                }
            }
            for (int i = 0; i < btn.Length; i++)
            {
                if (btn[i] && !prevBtn[i]) pendDown[i] = true;
                if (!btn[i] && prevBtn[i]) pendUp[i] = true;
            }
            // Both sticks clicked together = recenter.
            if (btn[(int)B.LStick] && btn[(int)B.RStick] && (!prevBtn[(int)B.LStick] || !prevBtn[(int)B.RStick])) needRecenter = true;
        }

        bool loggedPic; bool[] loggedCtrl = new bool[2];
        internal void OnInputUpdate(global::PlayerInputController pic)
        {
            if (!loggedPic) { loggedPic = true; Plugin.L.LogInfo("Input controller hooked: " + pic.name); }
            if (!vrOn) return;

            foreach (var a in clearNext) a();
            clearNext.Clear();

            if (Plugin.ControllerInput.Value && !KeyboardMode)
            {
                bool ui = UiMode;
                bool Down(B b) { int i = (int)b; bool v = pendDown[i]; pendDown[i] = false; return v; }
                bool Up(B b) { int i = (int)b; bool v = pendUp[i]; pendUp[i] = false; return v; }

                // Fire (right trigger) and secondary fire (left trigger).
                bool fd = Down(B.RTrig), fu = Up(B.RTrig);
                if (!ui)
                {
                    if (fd) { pic.firePressed = true; clearNext.Add(() => pic.firePressed = false); }
                    if (btn[(int)B.RTrig]) { pic.firePressing = true; injFireHold = true; }
                    if (fu && injFireHold) { pic.firePressing = false; pic.fireReleased = true; injFireHold = false; clearNext.Add(() => pic.fireReleased = false); }
                }
                bool f2d = Down(B.LTrig), f2u = Up(B.LTrig);
                if (f2d) { pic.fire2Pressed = true; clearNext.Add(() => pic.fire2Pressed = false); }
                if (btn[(int)B.LTrig]) { pic.fire2Pressing = true; injFire2Hold = true; }
                if (f2u && injFire2Hold) { pic.fire2Pressing = false; pic.fire2Released = true; injFire2Hold = false; clearNext.Add(() => pic.fire2Released = false); }

                if (Down(B.RA)) { pic.jumpPressed = true; pic.proceedPressed = true; clearNext.Add(() => { pic.jumpPressed = false; pic.proceedPressed = false; }); }
                if (Down(B.RB)) { pic.reloadPressed = true; pic.cancelPressed = true; clearNext.Add(() => { pic.reloadPressed = false; pic.cancelPressed = false; }); }
                if (Down(B.LA)) { pic.specialAbilityPressed = true; clearNext.Add(() => pic.specialAbilityPressed = false); }
                if (Down(B.LB)) { pic.startPressed = true; clearNext.Add(() => pic.startPressed = false); }
                if (Down(B.LGrip)) { pic.bloodyRushPressed = true; clearNext.Add(() => pic.bloodyRushPressed = false); }
                if (Down(B.RGrip)) { pic.interactPressed = true; clearNext.Add(() => pic.interactPressed = false); }
                if (Down(B.RStick)) { pic.manualLevelUpPressed = true; clearNext.Add(() => pic.manualLevelUpPressed = false); }
                Down(B.LStick); Up(B.LStick); Up(B.RA); Up(B.RB); Up(B.LA); Up(B.LB); Up(B.LGrip); Up(B.RGrip); Up(B.RStick);

                if (btn[(int)B.LStick]) { pic.runPressed = true; injRun = true; }
                else if (injRun) { pic.runPressed = false; injRun = false; }

                bool moving = lStick.x * lStick.x + lStick.y * lStick.y > 0.0225f;
                if (moving) { pic.moveDirection = lStick; pic.navigationDirection = lStick; injMove = injNav = true; }
                else
                {
                    if (injMove) { pic.moveDirection = default; injMove = false; }
                    if (injNav) { pic.navigationDirection = default; injNav = false; }
                }
            }

            RemapMove(pic);
        }

        Vector2 BodyRelativeStick(Vector2 st)
        {
            var mode = Plugin.MoveRelativeTo.Value;
            float refYaw;
            if (mode.Equals("LeftHand", StringComparison.OrdinalIgnoreCase) && handValid[0]) refYaw = YawOf(handRot[0]);
            else if (mode.Equals("Body", StringComparison.OrdinalIgnoreCase)) return st;
            else refYaw = YawOf(headRot);
            float a = (refYaw - bodyYaw) * Mathf.Deg2Rad;
            float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            Vector2 o; o.x = st.x * cs + st.y * sn; o.y = -st.x * sn + st.y * cs;
            return o;
        }

        // Rotate movement so "forward" follows head/hand instead of the game body.
        void RemapMove(global::PlayerInputController pic)
        {
            if (KeyboardMode) return; // keyboard mode already rotates the stick before pressing WASD
            var mode = Plugin.MoveRelativeTo.Value;
            float refYaw;
            if (mode.Equals("LeftHand", StringComparison.OrdinalIgnoreCase) && handValid[0]) refYaw = YawOf(handRot[0]);
            else if (mode.Equals("Body", StringComparison.OrdinalIgnoreCase)) refYaw = bodyYaw + extraYaw;
            else refYaw = YawOf(headRot);

            var cur = pic.moveDirection;
            Vector2 rawMove = haveWritten && cur.x == lastWrittenMove.x && cur.y == lastWrittenMove.y ? lastRawMove : cur;
            float a = (refYaw - bodyYaw) * Mathf.Deg2Rad;
            float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            Vector2 o; o.x = rawMove.x * cs + rawMove.y * sn; o.y = -rawMove.x * sn + rawMove.y * cs;
            pic.moveDirection = o;
            lastRawMove = rawMove; lastWrittenMove = o; haveWritten = true;
        }
    }
}
