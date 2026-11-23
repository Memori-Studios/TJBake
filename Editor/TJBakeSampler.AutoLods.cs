using System;
using System.Collections.Generic;
using MemoriStudios.TJBake.Format;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    public static partial class TJBakeSampler
    {
        #region Automatic LODs
        /// <summary>
        /// Adds the request's automatic LOD1, and its LOD2 when it asks for one, after every near body, with their switch
        /// heights. Returns the LOD count. Runs after the rebind, so each far mesh starts from its near mesh in the first
        /// rig's bind space.
        /// </summary>
        private static int AddAutoLods(TJBakeRequest r, Skeleton skeleton, List<BodyPart> bodies, UnitJson json)
        {
            int near = bodies.Count;
            for (int b = 0; b < near; b++)
                bodies.Add(new BodyPart
                {
                    Mesh = TJBakeAutoLod.Simplify(bodies[b].Mesh, r.AutoLodKeep), Material = bodies[b].Material, Lod = 1, Variant = bodies[b].Variant,
                    BindPoses = bodies[b].BindPoses, BoneMap = bodies[b].BoneMap,
                });
            json.lodSwitch.Add(r.AutoLodSwitch);
            if (r.AutoLod2Vertices <= 0) return 2;

            int[] into = FoldMap(skeleton, r.AutoLod2Folds);
            for (int v = 0; v <= r.ExtraVariants.Count; v++)
            {
                var look = new List<BodyPart>();
                for (int b = 0; b < near; b++) if (bodies[b].Variant == v) look.Add(bodies[b]);
                MeshBinData[] far = TJBakeAutoLod.SimplifyTo(look.ConvertAll(part => part.Mesh), r.AutoLodKeep, r.AutoLod2Vertices);
                for (int i = 0; i < far.Length; i++)
                {
                    TJBakeAutoLod.Fold(far[i], into);
                    bodies.Add(new BodyPart { Mesh = far[i], Material = look[i].Material, Lod = 2, Variant = v, BindPoses = look[i].BindPoses, BoneMap = look[i].BoneMap });
                }
            }
            json.lodSwitch.Add(r.AutoLod2Switch);
            return 3;
        }

        // Each bone's own index, or for a bone the fold names, its nearest skeleton ancestor's that the fold keeps.
        private static int[] FoldMap(Skeleton skeleton, Func<string, bool> folds)
        {
            var into = new int[skeleton.Bones.Count];
            for (int b = 0; b < into.Length; b++)
            {
                into[b] = b;
                if (folds == null || !folds(skeleton.Bones[b].name)) continue;
                for (Transform up = skeleton.Bones[b].parent; up != null; up = up.parent)
                    if (skeleton.TryByTransform(up, out int kept) && !folds(up.name))
                    {
                        into[b] = kept;
                        break;
                    }
            }
            return into;
        }
        #endregion
    }
}
