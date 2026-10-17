using System;
using UnityEngine;
using Valve.VR;

namespace VampireHuntersVR
{
    // Managed math so we avoid il2cpp round-trips and operator-interop quirks.
    internal static class VMath
    {
        public static Vector3 V(float x, float y, float z) { Vector3 v; v.x = x; v.y = y; v.z = z; return v; }
        public static Quaternion Q(float x, float y, float z, float w) { Quaternion q; q.x = x; q.y = y; q.z = z; q.w = w; return q; }
        public static readonly Quaternion Identity = Q(0, 0, 0, 1);

        public static Vector3 Add(Vector3 a, Vector3 b) => V(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 Sub(Vector3 a, Vector3 b) => V(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 Mul(Vector3 a, float s) => V(a.x * s, a.y * s, a.z * s);

        public static Quaternion Mul(Quaternion a, Quaternion b) => Q(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Quaternion Inverse(Quaternion q) => Q(-q.x, -q.y, -q.z, q.w);

        public static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            float tx = 2f * (q.y * v.z - q.z * v.y);
            float ty = 2f * (q.z * v.x - q.x * v.z);
            float tz = 2f * (q.x * v.y - q.y * v.x);
            return V(v.x + q.w * tx + (q.y * tz - q.z * ty),
                     v.y + q.w * ty + (q.z * tx - q.x * tz),
                     v.z + q.w * tz + (q.x * ty - q.y * tx));
        }

        public static Quaternion Yaw(float deg)
        {
            float h = deg * Mathf.Deg2Rad * 0.5f;
            return Q(0, (float)Math.Sin(h), 0, (float)Math.Cos(h));
        }

        public static Quaternion Pitch(float deg)
        {
            float h = deg * Mathf.Deg2Rad * 0.5f;
            return Q((float)Math.Sin(h), 0, 0, (float)Math.Cos(h));
        }

        public static float YawOf(Quaternion q)
        {
            var f = Rotate(q, V(0, 0, 1));
            if (f.x * f.x + f.z * f.z < 0.01f)
            {
                var u = Rotate(q, V(0, 1, 0));
                float s = f.y < 0 ? 1f : -1f;
                f = V(u.x * s, 0, u.z * s);
            }
            return (float)(Math.Atan2(f.x, f.z) * Mathf.Rad2Deg);
        }

        public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0) { b = Q(-b.x, -b.y, -b.z, -b.w); }
            var r = Q(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t);
            float n = (float)Math.Sqrt(r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w);
            return n < 1e-6f ? b : Q(r.x / n, r.y / n, r.z / n, r.w / n);
        }

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => V(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);

        // OpenVR (right-handed, -Z forward) to Unity (left-handed, +Z forward).
        public static Vector3 PosOf(HmdMatrix34_t m) => V(m.m3, m.m7, -m.m11);

        public static Quaternion RotOf(HmdMatrix34_t m)
        {
            float r00 = m.m0, r01 = m.m1, r02 = -m.m2;
            float r10 = m.m4, r11 = m.m5, r12 = -m.m6;
            float r20 = -m.m8, r21 = -m.m9, r22 = m.m10;
            float tr = r00 + r11 + r22;
            float x, y, z, w;
            if (tr > 0)
            {
                float s = (float)Math.Sqrt(tr + 1f) * 2f;
                w = 0.25f * s; x = (r21 - r12) / s; y = (r02 - r20) / s; z = (r10 - r01) / s;
            }
            else if (r00 > r11 && r00 > r22)
            {
                float s = (float)Math.Sqrt(1f + r00 - r11 - r22) * 2f;
                w = (r21 - r12) / s; x = 0.25f * s; y = (r01 + r10) / s; z = (r02 + r20) / s;
            }
            else if (r11 > r22)
            {
                float s = (float)Math.Sqrt(1f + r11 - r00 - r22) * 2f;
                w = (r02 - r20) / s; x = (r01 + r10) / s; y = 0.25f * s; z = (r12 + r21) / s;
            }
            else
            {
                float s = (float)Math.Sqrt(1f + r22 - r00 - r11) * 2f;
                w = (r10 - r01) / s; x = (r02 + r20) / s; y = (r12 + r21) / s; z = 0.25f * s;
            }
            float n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            return Q(x / n, y / n, z / n, w / n);
        }

        // OpenGL-style projection from OpenVR raw tangents, same layout as IVRSystem::GetProjectionMatrix.
        public static Matrix4x4 Projection(float l, float r, float t, float b, float n, float f)
        {
            Matrix4x4 m = default;
            m.m00 = 2f / (r - l);
            m.m02 = (r + l) / (r - l);
            m.m11 = 2f / (b - t);
            m.m12 = (t + b) / (b - t);
            m.m22 = -(f + n) / (f - n);
            m.m23 = -2f * f * n / (f - n);
            m.m32 = -1f;
            return m;
        }
    }
}
