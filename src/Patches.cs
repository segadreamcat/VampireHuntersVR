using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace VampireHuntersVR
{
    internal static class Patches
    {
        static readonly string[] AimMethods =
        {
            // Weapon.shoot shares native code with other functions (identical-code folding): never hook it.
            "shoot_forward", "shoot_projectile", "update"
        };

        static Dictionary<IntPtr, int> codeUse;
        internal static bool PlayerHooked;

        // IL2CPP builds fold identical tiny functions into one body. Hooking a shared body
        // hijacks unrelated methods and crashes the game, so skip any target that shares code.
        static unsafe IntPtr NativeCode(MethodBase m)
        {
            try
            {
                var f = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
                if (f == null) return IntPtr.Zero;
                var info = (IntPtr)f.GetValue(null);
                return info == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)info;
            }
            catch { return IntPtr.Zero; }
        }

        static void BuildCodeUse()
        {
            codeUse = new Dictionary<IntPtr, int>();
            foreach (var t in typeof(global::Weapon).Assembly.GetTypes())
            {
                MethodInfo[] ms;
                try { ms = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var m in ms)
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
                    var p = NativeCode(m);
                    if (p == IntPtr.Zero) continue;
                    codeUse[p] = codeUse.TryGetValue(p, out int n) ? n + 1 : 1;
                }
            }
        }

        public static void Apply(Harmony h)
        {
            // No whole-assembly scan: touching every game class ran their static initializers before the
            // save loaded, which broke PlayerStatsFactory and made Play crash. Only check our own targets.
            codeUse = new Dictionary<IntPtr, int>();
            PlayerHooked = Patch(h, typeof(global::PlayerController), "LateUpdate", null, nameof(PlayerLateUpdatePostfix));
            Patch(h, typeof(global::PlayerInputController), "Update", null, nameof(InputUpdatePostfix));
            // Player logic picks the shooting target from the camera: aim it along the right hand meanwhile.
            Patch(h, typeof(global::PlayerController), "Update", nameof(PlayerUpdatePrefix), nameof(PlayerUpdatePostfix));
            foreach (var name in AimMethods)
                Patch(h, typeof(global::Weapon), name, name == "update" ? nameof(WeaponPrefix) : nameof(WeaponShootPrefix), nameof(WeaponPostfix));
        }

        static bool Patch(Harmony h, Type t, string name, string prefix, string postfix)
        {
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == name).ToArray();
            if (methods.Length == 0) { Plugin.L.LogWarning($"Patch target not found: {t.Name}.{name}"); return false; }
            bool any = false;
            foreach (var m in methods)
            {
                var code = NativeCode(m);
                if (code != IntPtr.Zero && codeUse.ContainsKey(code))
                {
                    Plugin.L.LogWarning($"Skipping hook {t.Name}.{name}: shares native code with another hook target.");
                    continue;
                }
                if (code != IntPtr.Zero) codeUse[code] = 1;
                if (code == IntPtr.Zero)
                {
                    Plugin.L.LogWarning($"Skipping hook {t.Name}.{name}: shared or unknown native code.");
                    continue;
                }
                try
                {
                    h.Patch(m,
                        prefix == null ? null : new HarmonyMethod(typeof(Patches).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix == null ? null : new HarmonyMethod(typeof(Patches).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
                    any = true;
                }
                catch (Exception e) { Plugin.L.LogWarning($"Failed to patch {t.Name}.{name}: {e.Message}"); }
            }
            return any;
        }

        static void PlayerLateUpdatePostfix(global::PlayerController __instance)
        {
            try { VrDriver.Instance?.OnPlayerLateUpdate(__instance); }
            catch (Exception e) { VrDriver.Instance?.Fail(e); }
        }

        static void InputUpdatePostfix(global::PlayerInputController __instance)
        {
            try { VrDriver.Instance?.OnInputUpdate(__instance); }
            catch (Exception e) { Plugin.L.LogWarning("Input inject: " + e.Message); }
        }

        internal static int playerUpdateCalls, weaponCalls;
        static void PlayerUpdatePrefix(global::PlayerController __instance)
        {
            playerUpdateCalls++;
            try { VrDriver.Instance?.BeginAimPlayer(__instance); }
            catch (Exception e) { Plugin.L.LogWarning("Aim player begin: " + e.Message); }
        }

        static void PlayerUpdatePostfix()
        {
            try { VrDriver.Instance?.EndAim(); }
            catch (Exception e) { Plugin.L.LogWarning("Aim player end: " + e.Message); }
        }

        static void WeaponShootPrefix(global::Weapon __instance)
        {
            WeaponPrefix(__instance);
            try { VrDriver.Instance?.ShotFired(__instance); } catch { }
        }

        static void WeaponPrefix(global::Weapon __instance)
        {
            weaponCalls++;
            try { VrDriver.Instance?.BeginAim(__instance); }
            catch (Exception e) { Plugin.L.LogWarning("Aim begin: " + e.Message); }
        }

        static void WeaponPostfix()
        {
            try { VrDriver.Instance?.EndAim(); }
            catch (Exception e) { Plugin.L.LogWarning("Aim end: " + e.Message); }
        }
    }
}
