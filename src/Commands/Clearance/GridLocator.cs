using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace JarviTools.Commands.Clearance
{
    /// <summary>
    /// 把一个点定位成"D×5"这样的轴网交点描述。
    /// 取平面距离最近的轴线 + 与其方向差 30° 以上的最近轴线。
    /// 没有轴网时退回坐标文本。
    /// </summary>
    internal class GridLocator
    {
        private class GridInfo
        {
            public string Name;
            public Curve Curve;
        }

        private readonly List<GridInfo> _grids;

        public GridLocator(Document doc)
        {
            _grids = new List<GridInfo>();
            AddGrids(doc, Transform.Identity);

            // 轴网常在结构/建筑链接里（机电宿主模型往往只有零星几条）。
            // 读取所有已载入链接的轴网，曲线变换到宿主坐标。未载入的链接跳过。
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document ldoc = link.GetLinkDocument();
                if (ldoc == null) continue;
                AddGrids(ldoc, link.GetTotalTransform());
            }
        }

        /// <summary>轴网总数（供命令层判断定位是否可用）。</summary>
        public int Count { get { return _grids.Count; } }

        private void AddGrids(Document d, Transform tf)
        {
            foreach (Grid g in new FilteredElementCollector(d).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (g.Curve == null) continue;
                Curve c = tf.IsIdentity ? g.Curve : g.Curve.CreateTransformed(tf);
                _grids.Add(new GridInfo { Name = g.Name, Curve = c });
            }
        }

        public string Locate(XYZ point)
        {
            if (_grids.Count == 0) return CoordText(point);

            GridInfo g1 = null, g2 = null;
            XYZ dir1 = null;
            double d1 = double.MaxValue, d2 = double.MaxValue;

            foreach (GridInfo g in _grids)
            {
                double d;
                XYZ dir;
                if (!TryProject(g.Curve, point, out d, out dir)) continue;
                if (d < d1) { d1 = d; g1 = g; dir1 = dir; }
            }
            if (g1 == null) return CoordText(point);

            foreach (GridInfo g in _grids)
            {
                if (ReferenceEquals(g, g1)) continue;
                double d;
                XYZ dir;
                if (!TryProject(g.Curve, point, out d, out dir)) continue;
                if (Math.Abs(dir.DotProduct(dir1)) > 0.866) continue; // 夹角 < 30°，视为同向
                if (d < d2) { d2 = d; g2 = g; }
            }

            if (g2 == null) return g1.Name + "轴附近";

            // 字母轴排前面，更符合施工习惯
            string a = g1.Name, b = g2.Name;
            bool aDigit = a.Length > 0 && char.IsDigit(a[0]);
            bool bDigit = b.Length > 0 && char.IsDigit(b[0]);
            if (aDigit && !bDigit) { string tmp = a; a = b; b = tmp; }
            return a + "×" + b;
        }

        private static bool TryProject(Curve curve, XYZ point, out double planDist, out XYZ planDir)
        {
            planDist = 0;
            planDir = null;
            try
            {
                IntersectionResult proj = curve.Project(point);
                if (proj == null) return false;
                XYZ q = proj.XYZPoint;
                planDist = Math.Sqrt(
                    (point.X - q.X) * (point.X - q.X) + (point.Y - q.Y) * (point.Y - q.Y));
                Transform deriv = curve.ComputeDerivatives(proj.Parameter, false);
                XYZ t = deriv.BasisX;
                var flat = new XYZ(t.X, t.Y, 0);
                if (flat.GetLength() < 1e-9) return false;
                planDir = flat.Normalize();
                return true;
            }
            catch { return false; }
        }

        private static string CoordText(XYZ p)
        {
            return "(" + (p.X * 0.3048).ToString("0.0") + ", " + (p.Y * 0.3048).ToString("0.0") + ")m";
        }
    }
}
