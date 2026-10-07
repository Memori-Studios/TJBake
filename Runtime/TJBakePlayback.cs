using System.Collections.Generic;
using Unity.Mathematics;

namespace MemoriStudios.TJBake
{
    /// <summary>Playback position of one visual, outside any ECS. The vectors are what the shader reads.</summary>
    public struct TJBakePlaybackState
    {
        public int CurrentSlot;
        // Absolute row in the matrix texture, fractional between rows.
        public float CurrentFrame;
        public int PrevSlot;
        public float PrevFrame;
        public float BlendLeft;
        public float BlendTotal;
        public bool Returned;
        // (rowA, rowB, lerp, enabled) and (rowA, rowB, lerp, weight of the previous clip).
        public float4 Current;
        public float4 Previous;

        public static TJBakePlaybackState Stopped => new TJBakePlaybackState { CurrentSlot = -1, PrevSlot = -1 };
    }

    /// <summary>The playback rules every TJBake host follows: loop, hold, return to idle, and the blend from the previous slot.</summary>
    public static class TJBakePlayback
    {
        public const float ReturnBlendSeconds = 0.25f;

        /// <summary>Starts a slot, blending from whatever plays now. Playing the same slot again restarts it.</summary>
        public static void Play(ref TJBakePlaybackState s, IReadOnlyList<TJBakeSlot> slots, int slot, float transitionSeconds = 0.25f, float startNormalizedTime = 0f)
        {
            if (slots.Count == 0) return;
            if (s.CurrentSlot >= 0 && transitionSeconds > 0f)
            {
                s.PrevSlot = s.CurrentSlot;
                s.PrevFrame = s.CurrentFrame;
                s.BlendTotal = transitionSeconds;
                s.BlendLeft = transitionSeconds;
            }
            else
            {
                s.PrevSlot = -1;
                s.BlendLeft = 0f;
                s.BlendTotal = 0f;
            }
            s.CurrentSlot = math.clamp(slot, 0, slots.Count - 1);
            TJBakeSlot started = slots[s.CurrentSlot];
            // A random phase is for loops; a one-shot always plays from its first frame.
            float startNormalized = started.Loop ? math.saturate(startNormalizedTime) : 0f;
            s.CurrentFrame = started.Start + startNormalized * math.max(started.Count - 1, 0);
            s.Returned = false;
        }

        /// <summary>Advances by dt seconds. Returns true on the frame a returning slot hands back to <paramref name="idleSlot"/>.</summary>
        public static bool Step(ref TJBakePlaybackState s, IReadOnlyList<TJBakeSlot> slots, float dt, int idleSlot = 0)
        {
            if (slots.Count == 0 || s.CurrentSlot < 0) return false;
            TJBakeSlot slot = slots[s.CurrentSlot];
            float local = Advance(s.CurrentFrame - slot.Start, slot, dt);
            s.CurrentFrame = slot.Start + local;
            s.Current = Vector(slot, local, 1f);

            if (s.PrevSlot >= 0 && s.BlendLeft > 0f)
            {
                TJBakeSlot prev = slots[math.clamp(s.PrevSlot, 0, slots.Count - 1)];
                float prevLocal = Advance(s.PrevFrame - prev.Start, prev, dt);
                s.PrevFrame = prev.Start + prevLocal;
                s.BlendLeft = math.max(s.BlendLeft - dt, 0f);
                float weight = s.BlendTotal > 0f ? s.BlendLeft / s.BlendTotal : 0f;
                s.Previous = Vector(prev, prevLocal, weight);
                if (s.BlendLeft <= 0f) s.PrevSlot = -1;
            }
            else
            {
                s.Previous = float4.zero;
            }

            if (!slot.Loop && slot.ReturnToIdle && !s.Returned && slot.Count > 1 && local / (slot.Count - 1) >= slot.ReturnAt)
            {
                s.Returned = true;
                Play(ref s, slots, idleSlot, ReturnBlendSeconds);
                return true;
            }
            return false;
        }

        // Local frame position after dt, wrapped for a loop, clamped for a one-shot.
        private static float Advance(float local, TJBakeSlot slot, float dt)
        {
            if (slot.Count <= 1) return 0f;
            local += dt * slot.Fps;
            if (slot.Loop)
            {
                local = local % slot.Count;
                if (local < 0f) local += slot.Count;
                return local;
            }
            return math.clamp(local, 0f, slot.Count - 1);
        }

        // Two texture rows and the lerp between them; a loop wraps its last row to its first.
        private static float4 Vector(TJBakeSlot slot, float local, float weight)
        {
            int a = (int)math.floor(local);
            int b = slot.Loop ? (a + 1) % math.max(slot.Count, 1) : math.min(a + 1, slot.Count - 1);
            return new float4(slot.Start + a, slot.Start + b, local - a, weight);
        }

        /// <summary>The anchor transform for this frame, blended the way the shader blends bone rows.</summary>
        public static float3x4 SampleAnchor(TJBakeVisual visual, int anchor, in TJBakePlaybackState s)
        {
            float3x4 m = SampleAnchorRows(visual, anchor, s.Current);
            if (s.Previous.w > 0f) m = Lerp(m, SampleAnchorRows(visual, anchor, s.Previous), s.Previous.w);
            return m;
        }

        private static float3x4 SampleAnchorRows(TJBakeVisual visual, int anchor, float4 v)
        {
            int count = visual.AnchorCount;
            int frames = count > 0 ? visual.AnchorMatrices.Length / count : 0;
            if (frames == 0 || anchor < 0 || anchor >= count) return new float3x4(new float3(1, 0, 0), new float3(0, 1, 0), new float3(0, 0, 1), float3.zero);
            int rowA = math.clamp((int)v.x, 0, frames - 1);
            int rowB = math.clamp((int)v.y, 0, frames - 1);
            return Lerp(visual.AnchorMatrices[rowA * count + anchor], visual.AnchorMatrices[rowB * count + anchor], v.z);
        }

        private static float3x4 Lerp(float3x4 a, float3x4 b, float t) =>
            new float3x4(math.lerp(a.c0, b.c0, t), math.lerp(a.c1, b.c1, t), math.lerp(a.c2, b.c2, t), math.lerp(a.c3, b.c3, t));
    }
}
