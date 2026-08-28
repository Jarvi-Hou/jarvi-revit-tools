using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace JarviTools.Commands.Common
{
    /// <summary>
    /// 基于真实 Solid 几何的通用工具（区别于包围盒）：
    /// 收集实体、求最低点、取采样点。供设备剖面与净高分析共用。
    /// </summary>
    internal static class GeometryUtil
    {
        /// <summary>收集元素全部实体（含族实例几何，已到宿主文档世界坐标），忽略废屑。</summary>
        public static List<Solid> CollectSolids(Element e)
        {
            var result = new List<Solid>();
            var opt = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Medium
            };
            GeometryElement geo;
            try { geo = e.get_Geometry(opt); } catch { return result; }
            if (geo == null) return result;
            Collect(geo, result);
            return result;
        }

        private static void Collect(GeometryElement geo, List<Solid> result)
        {
            foreach (GeometryObject g in geo)
            {
                if (g is Solid s)
                {
                    if (s.Volume > 1e-9 && s.Faces.Size > 0) result.Add(s);
                }
                else if (g is GeometryInstance gi)
                {
                    Collect(gi.GetInstanceGeometry(), result);
                }
                else if (g is GeometryElement ge)
                {
                    Collect(ge, result);
                }
            }
        }

        /// <summary>
        /// 求元素实体几何的最低点（面三角化后取最小 Z 顶点）。
        /// tf 用于链接模型（传 link.GetTotalTransform()），宿主文档传 Transform.Identity。
        /// 返回 false = 元素没有可用实体。
        /// </summary>
        public static bool TryGetLowestPoint(Element e, Transform tf, out XYZ lowest)
        {
            lowest = XYZ.Zero;
            double minZ = double.MaxValue;
            foreach (Solid s in CollectSolids(e))
            {
                foreach (Face f in s.Faces)
                {
                    Mesh mesh;
                    // 0.5 的细分精度让圆管/弯头底部的弦高误差控制在毫米级
                    try { mesh = f.Triangulate(0.5); } catch { continue; }
                    if (mesh == null) continue;
                    for (int i = 0; i < mesh.Vertices.Count; i++)
                    {
                        XYZ p = tf.OfPoint(mesh.Vertices[i]);
                        if (p.Z < minZ) { minZ = p.Z; lowest = p; }
                    }
                }
            }
            return minZ != double.MaxValue;
        }

        /// <summary>
        /// 元素几何采样点（实体边线细分点），供投影求长短边。
        /// 取不到实体时退回包围盒 8 角点。
        /// </summary>
        public static List<XYZ> GetSamplePoints(Element e)
        {
            var pts = new List<XYZ>();
            foreach (Solid s in CollectSolids(e))
            {
                foreach (Edge edge in s.Edges)
                {
                    IList<XYZ> tess;
                    try { tess = edge.Tessellate(); } catch { continue; }
                    if (tess != null) pts.AddRange(tess);
                }
            }
            if (pts.Count == 0)
            {
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb != null)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        double x = ((i & 1) == 0) ? bb.Min.X : bb.Max.X;
                        double y = ((i & 2) == 0) ? bb.Min.Y : bb.Max.Y;
                        double z = ((i & 4) == 0) ? bb.Min.Z : bb.Max.Z;
                        pts.Add(bb.Transform.OfPoint(new XYZ(x, y, z)));
                    }
                }
            }
            return pts;
        }
    }
}
