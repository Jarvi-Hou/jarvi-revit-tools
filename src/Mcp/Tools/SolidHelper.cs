using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 静态辅助方法，供 RunClashDetectionTool 及其他碰撞检测工具共享。
    /// </summary>
    internal static class SolidHelper
    {
        /// <summary>
        /// 获取元素的主体 Solid（排除小体积废屑）。
        /// 递归遍历 GeometryInstance 以获取 Symbol 几何。
        /// </summary>
        public static Solid GetMainSolid(Element e)
        {
            try
            {
                var opt = new Options
                {
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false,
                    DetailLevel = ViewDetailLevel.Medium
                };

                var geo = e.get_Geometry(opt);
                if (geo == null) return null;

                return ExtractMainSolid(geo);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从 GeometryElement 中递归提取体积最大的 Solid
        /// </summary>
        private static Solid ExtractMainSolid(GeometryElement geo)
        {
            Solid best = null;
            double maxVol = 0;

            foreach (var g in geo)
            {
                if (g is Solid s)
                {
                    if (s.Volume > 1e-6 && s.Volume > maxVol)
                    {
                        best = s;
                        maxVol = s.Volume;
                    }
                }
                else if (g is GeometryInstance gi)
                {
                    // 遍历实例几何（Symbol geometry）
                    var inner = ExtractMainSolid(gi.GetInstanceGeometry());
                    if (inner != null && inner.Volume > maxVol)
                    {
                        best = inner;
                        maxVol = inner.Volume;
                    }
                    // 也检查 Symbol 几何
                    var sym = ExtractMainSolid(gi.GetSymbolGeometry());
                    if (sym != null && sym.Volume > maxVol)
                    {
                        best = sym;
                        maxVol = sym.Volume;
                    }
                }
                else if (g is GeometryElement ge)
                {
                    var inner = ExtractMainSolid(ge);
                    if (inner != null && inner.Volume > maxVol)
                    {
                        best = inner;
                        maxVol = inner.Volume;
                    }
                }
            }

            return best;
        }
    }
}
