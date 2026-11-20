using System;
using System.Collections.Generic;
using UnityEngine;

namespace MemoriStudios.TJBake
{
    /// <summary>
    /// One texture per file content and colour space, shared by every visual whose files hold it. A visual's owned list
    /// gets a lease instead of the texture: destroying the lease, as an owner destroys everything it owns, releases the
    /// texture, and the last release destroys it.
    /// </summary>
    public sealed class TJBakeTextureLease : ScriptableObject
    {
        private sealed class Shared
        {
            public Hash128 Key;
            public Texture2D Texture;
            public int Users;
        }

        private static readonly Dictionary<Hash128, Shared> Loaded = new();
        private Shared _shared;

        /// <summary>The texture for these file bytes, made by <paramref name="decode"/> only when no visual holds it yet.</summary>
        public static Texture2D Take(byte[] bytes, bool srgb, Func<Texture2D> decode, List<UnityEngine.Object> owned)
        {
            Hash128 key = Hash128.Compute(bytes);
            key.Append(srgb ? 1 : 0);
            if (!Loaded.TryGetValue(key, out Shared shared) || shared.Texture == null)
            {
                Texture2D texture = decode();
                // Owners destroy their leases explicitly; the unload sweep on every state change must not free the texture first.
                texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Loaded[key] = shared = new Shared { Key = key, Texture = texture };
            }
            shared.Users++;
            var lease = CreateInstance<TJBakeTextureLease>();
            lease.name = shared.Texture.name;
            lease.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            lease._shared = shared;
            owned.Add(lease);
            return shared.Texture;
        }

        private void OnDestroy()
        {
            Shared shared = _shared;
            _shared = null;
            if (shared == null || --shared.Users > 0) return;
            if (Loaded.TryGetValue(shared.Key, out Shared current) && current == shared) Loaded.Remove(shared.Key);
            TJBakeVisualLoader.DestroyNow(shared.Texture);
        }
    }
}
