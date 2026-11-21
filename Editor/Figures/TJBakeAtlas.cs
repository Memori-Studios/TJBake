using System.Collections.Generic;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>A mask laid over an atlas: where the mask is white, the atlas is multiplied by the colour.</summary>
    public struct TJBakeTint
    {
        public Texture2D Mask;

        /// <summary>sRGB, as a material holds it.</summary>
        public Color Color;
    }

    /// <summary>An atlas's flat From, and its darker edges, baked as To in the same shade.</summary>
    public struct TJBakeRecolour
    {
        public Color From;
        public Color To;
    }

    /// <summary>
    /// Copies of a figure's atlas made in memory before a bake: tinted through masks, as a pack's character shader colours
    /// skin and hair, or with some of its colours swapped. Each copy goes in the caller's owned list.
    /// </summary>
    public static class TJBakeAtlas
    {
        #region Masks
        /// <summary>The atlas with each tint's colour multiplied in where its mask is white, in order.</summary>
        public static Texture2D Masked(Texture2D atlas, IReadOnlyList<TJBakeTint> tints, List<Object> owned)
        {
            Color[] pixels = Pixels(atlas, atlas.width, atlas.height);
            foreach (TJBakeTint tint in tints) Lay(pixels, Pixels(tint.Mask, atlas.width, atlas.height), tint.Color);
            var masked = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false) { name = atlas.name };
            masked.SetPixels(pixels);
            masked.Apply();
            owned.Add(masked);
            return masked;
        }

        // The colours are sRGB, as the material holds them, so the multiply runs in linear light as the shader's does.
        private static void Lay(Color[] pixels, Color[] mask, Color color)
        {
            Color tint = color.linear;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color lit = (pixels[i].linear * tint).gamma;
                lit.a = pixels[i].a;
                pixels[i] = Color.Lerp(pixels[i], lit, mask[i].r);
            }
        }
        #endregion

        #region Recolours
        /// <summary>The atlas with each recolour's From, at any shade, turned to its To at that shade.</summary>
        public static Texture2D Recoloured(Texture2D atlas, IReadOnlyList<TJBakeRecolour> recolours, List<Object> owned)
        {
            Color[] pixels = Pixels(atlas, atlas.width, atlas.height);
            foreach (TJBakeRecolour recolour in recolours)
                for (int i = 0; i < pixels.Length; i++)
                    if (Shade(pixels[i], recolour.From, out float shade))
                    {
                        Color to = recolour.To * shade;
                        to.a = pixels[i].a;
                        pixels[i] = to;
                    }
            var recoloured = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false) { name = atlas.name };
            recoloured.SetPixels(pixels);
            recoloured.Apply();
            owned.Add(recoloured);
            return recoloured;
        }

        // Whether a pixel is the colour at some shade, as an edge against the black or the compression leaves it, and which.
        // Two near colours in one atlas differ in their mix far more than this.
        private static bool Shade(Color pixel, Color colour, out float shade)
        {
            const float Darkest = 0.15f;
            const float Mix = 0.04f;
            shade = (pixel.r * colour.r + pixel.g * colour.g + pixel.b * colour.b) / (colour.r * colour.r + colour.g * colour.g + colour.b * colour.b);
            if (shade < Darkest) return false;
            float off = Mathf.Max(Mathf.Abs(pixel.r - colour.r * shade), Mathf.Max(Mathf.Abs(pixel.g - colour.g * shade), Mathf.Abs(pixel.b - colour.b * shade)));
            shade = Mathf.Min(shade, 1f);
            return off < Mix * shade;
        }
        #endregion

        /// <summary>A texture's pixels at the given size. Pack textures are not readable, so it is drawn into a render texture and read back.</summary>
        public static Color[] Pixels(Texture texture, int width, int height)
        {
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(texture, rt);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            Color[] pixels = copy.GetPixels();
            Object.DestroyImmediate(copy);
            return pixels;
        }
    }
}
