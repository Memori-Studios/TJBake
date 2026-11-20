using System;
using System.IO;
using MemoriStudios.TJBake.Format;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>Writes a bake's textures as *.tex.bin: at most <see cref="MaxSize"/> on a side, BC7 with every mip.</summary>
    public static class TJBakeTextureExport
    {
        // A unit stands about 100 px tall at 1080p; a close-up portrait still reads at 1024, a sixteenth of a 4096 map's memory.
        public const int MaxSize = 1024;

        /// <summary>The file name a material's texture gets, such as mat_0_base.tex.bin.</summary>
        public static string FileName(int material, string role) => $"mat_{material}_{role}{TexBin.Extension}";

        public static void Write(Texture2D texture, string path, bool linear, bool dropAlpha = false)
        {
            Texture2D decoded = DecodeSourceFile(texture, linear);
            Texture source = decoded != null ? decoded : texture;
            try
            {
                // Unity compresses a texture with mips to BC7 only at power-of-two sizes.
                int width = FloorPowerOfTwo(source.width), height = FloorPowerOfTwo(source.height);
                while (Math.Max(width, height) > MaxSize) { width /= 2; height /= 2; }
                width = Math.Max(4, width);
                height = Math.Max(4, height);
                bool srgb = decoded != null ? !linear : GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
                Texture2D sized = Resample(source, width, height, srgb, linear);
                if (dropAlpha)
                {
                    Color32[] pixels = sized.GetPixels32();
                    for (int p = 0; p < pixels.Length; p++) pixels[p].a = 255;
                    sized.SetPixels32(pixels);
                }
                Save(sized, path);
            }
            finally
            {
                if (decoded != null) UnityEngine.Object.DestroyImmediate(decoded);
            }
        }

        /// <summary>A 4x4 texture of one colour, for a material with no map.</summary>
        public static void WriteSolid(string path, Color color)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, true);
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = color;
            tex.SetPixels(px);
            Save(tex, path);
        }

        // The file on disk, not the import: the import may be capped below full size, and a normal map's import is swizzled.
        private static Texture2D DecodeSourceFile(Texture2D texture, bool linear)
        {
            string assetPath = AssetDatabase.GetAssetPath(texture);
            string extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") return null;
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { filterMode = FilterMode.Trilinear };
            if (decoded.LoadImage(File.ReadAllBytes(assetPath)))
            {
                decoded.Apply(true);
                return decoded;
            }
            UnityEngine.Object.DestroyImmediate(decoded);
            return null;
        }

        // A blit to a fraction of the source's size samples its matching mip, so a 4096 map comes out box-filtered, not aliased.
        private static Texture2D Resample(Texture source, int width, int height, bool srgb, bool linear)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            Graphics.Blit(source, rt);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var sized = new Texture2D(width, height, TextureFormat.RGBA32, true, linear);
            sized.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return sized;
        }

        private static void Save(Texture2D texture, string path)
        {
            try
            {
                texture.Apply(true);
                EditorUtility.CompressTexture(texture, TextureFormat.BC7, TextureCompressionQuality.Normal);
                if (texture.format != TextureFormat.BC7) throw new InvalidOperationException($"{Path.GetFileName(path)} did not compress to BC7");
                TexBin.Write(path, texture.width, texture.height, texture.mipmapCount, texture.GetRawTextureData());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static int FloorPowerOfTwo(int size)
        {
            int power = 1;
            while (power * 2 <= size) power *= 2;
            return power;
        }
    }
}
