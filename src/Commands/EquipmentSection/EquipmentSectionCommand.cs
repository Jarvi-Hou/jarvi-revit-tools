using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JarviTools.Commands.Common;
using JarviTools.Mcp.Server;

namespace JarviTools.Commands.EquipmentSection
{
    /// <summary>
    /// 批量生成设备检查剖面：每台设备 1 个、垂直于长边剖切（看短边立面）、
    /// 自动命名"前缀-类型名-ID"、整批可一次撤销。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class EquipmentSectionCommand : IExternalCommand
    {
        private const double M_TO_FT = 1 / 0.3048;
        private const double SQUARE_TOLERANCE = 0.05; // 长宽差 5% 以内视为方形

        private static readonly BuiltInCategory[] EquipCats =
        {
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_ElectricalEquipment
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("设备检查剖面", "请先打开一个 Revit 项目文件。");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            try
            {
                // 1. 目标设备：预选优先，否则进入框选
                List<FamilyInstance> equipment = GetTargetEquipment(uidoc);
                if (equipment == null) return Result.Cancelled; // 用户取消了框选
                if (equipment.Count == 0)
                {
                    TaskDialog.Show("设备检查剖面",
                        "所选构件中没有机械设备或电气设备。\n\n支持类别：机械设备、电气设备。");
                    return Result.Cancelled;
                }

                // 2. 设置（记住上次值）
                var settings = JsonSettingsStore.Load<SectionSettings>("EquipmentSection");
                using (var form = new SectionSettingsForm(settings))
                {
                    if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return Result.Cancelled;
                    form.Apply(settings);
                }
                JsonSettingsStore.Save("EquipmentSection", settings);

                // 3. 剖面视图类型
                var vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.Section);
                if (vft == null)
                {
                    TaskDialog.Show("设备检查剖面", "项目中没有剖面视图类型，无法创建剖面。");
                    return Result.Failed;
                }

                // 4. 逐台生成（TransactionGroup：整批一次撤销；单台失败不影响其他）
                var created = new List<string>();
                var failed = new List<string>();
                using (var group = new TransactionGroup(doc, "批量生成设备检查剖面"))
                {
                    bool groupCompleted = false;
                    try
                    {
                        JarviTools.Core.TransactionSafety.Start(
                            group,
                            "Batch equipment inspection sections");

                        foreach (FamilyInstance fi in equipment)
                        {
                            try
                            {
                                string createdViewName;
                                using (var tx = new Transaction(doc, "设备检查剖面"))
                                {
                                    try
                                    {
                                        JarviTools.Core.TransactionSafety.Start(
                                            tx,
                                            "Create equipment inspection section");
                                        View view = CreateSectionForEquipment(doc, vft.Id, fi, settings);
                                        createdViewName = view.Name;
                                        JarviTools.Core.TransactionSafety.Commit(
                                            tx,
                                            "Create equipment inspection section");
                                    }
                                    catch
                                    {
                                        if (tx.HasStarted() && !tx.HasEnded())
                                        {
                                            JarviTools.Core.TransactionSafety.RollBack(
                                                tx,
                                                "Create equipment inspection section");
                                        }
                                        throw;
                                    }
                                }
                                created.Add(createdViewName);
                            }
                            catch (Exception ex)
                            {
                                failed.Add(ElementLabel(fi) + "：" + ex.Message);
                            }
                        }

                        JarviTools.Core.TransactionSafety.Assimilate(
                            group,
                            "Batch equipment inspection sections");
                        groupCompleted = true;
                    }
                    catch
                    {
                        if (!groupCompleted && group.HasStarted() && !group.HasEnded())
                        {
                            JarviTools.Core.TransactionSafety.RollBack(
                                group,
                                "Batch equipment inspection sections");
                        }
                        throw;
                    }
                }

                // 5. 汇总
                var sb = new StringBuilder();
                sb.AppendLine("成功生成剖面：" + created.Count + " 个");
                if (failed.Count > 0)
                {
                    sb.AppendLine("失败：" + failed.Count + " 个");
                    foreach (string f in failed.Take(10)) sb.AppendLine("  · " + f);
                    if (failed.Count > 10) sb.AppendLine("  ……");
                }
                sb.AppendLine();
                sb.AppendLine("剖面已在项目浏览器中，可 Ctrl+Z 一次性撤销整批。");
                TaskDialog.Show("设备检查剖面 — 完成", sb.ToString());

                return created.Count > 0 ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                Logger.Error("EquipmentSectionCommand failed", ex);
                TaskDialog.Show("设备检查剖面 — 错误", "执行失败：\n" + ex.Message);
                return Result.Failed;
            }
        }

        // ==================== 选择 ====================

        internal static List<FamilyInstance> GetTargetEquipment(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            var pre = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<FamilyInstance>()
                .Where(IsEquipment)
                .ToList();
            if (pre.Count > 0) return pre;

            try
            {
                IList<Reference> refs = uidoc.Selection.PickObjects(
                    ObjectType.Element, new EquipmentSelectionFilter(),
                    "框选或点选设备（机械设备/电气设备），完成后点选项栏的\"完成\"");
                return refs.Select(r => doc.GetElement(r)).OfType<FamilyInstance>().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        private static bool IsEquipment(Element e)
        {
            if (e == null || e.Category == null) return false;
            long cid = e.Category.Id.Value;
            foreach (BuiltInCategory c in EquipCats)
                if ((long)c == cid) return true;
            return false;
        }

        private class EquipmentSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is FamilyInstance && IsEquipment(elem);
            }
            public bool AllowReference(Reference reference, XYZ position) { return false; }
        }

        // ==================== 剖面生成 ====================

        private static View CreateSectionForEquipment(
            Document doc, ElementId vftId, FamilyInstance fi, SectionSettings s)
        {
            // 优先按风口连接件定位（VRV 等风管设备的可靠做法）：
            // 视线沿风管轴线正对进出风口面，剖切面过连接件，宽高以连接件为中心展开。
            // 这样不受族建模时长短边方向对调的影响，连接件偏左偏右也自动跟随。
            Connector conn = FindMainDuctConnector(fi);
            if (conn != null)
            {
                XYZ outDir = FlattenToHorizontal(conn.CoordinateSystem.BasisZ);
                if (outDir != null)
                    return CreateConnectorAnchoredSection(doc, vftId, fi, conn, s);
            }

            // —— 兜底：没有可用风管连接件的设备，按几何长短边 ——
            // 1. 设备局部水平轴（族朝向优先，取不到退回世界轴）
            XYZ hand = FlattenToHorizontal(fi.HandOrientation);
            XYZ facing = FlattenToHorizontal(fi.FacingOrientation);
            if (hand == null || facing == null || Math.Abs(hand.DotProduct(facing)) > 0.7)
            {
                hand = XYZ.BasisX;
                facing = XYZ.BasisY;
            }
            else
            {
                // 施密特正交化，保证 hand ⊥ facing（个别族两轴不严格垂直）
                facing = (facing - hand * hand.DotProduct(facing)).Normalize();
            }

            // 2. 采样点投影 → 长短边尺寸与几何中心
            List<XYZ> pts = GeometryUtil.GetSamplePoints(fi);
            if (pts.Count == 0)
                throw new InvalidOperationException("取不到设备几何。");

            double minH = double.MaxValue, maxH = double.MinValue;
            double minF = double.MaxValue, maxF = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (XYZ p in pts)
            {
                double h = p.DotProduct(hand);
                double f = p.DotProduct(facing);
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
                if (f < minF) minF = f;
                if (f > maxF) maxF = f;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }
            double lenHand = maxH - minH;
            double lenFacing = maxF - minF;
            XYZ center = hand * ((minH + maxH) / 2)
                       + facing * ((minF + maxF) / 2)
                       + XYZ.BasisZ * ((minZ + maxZ) / 2);

            // 3. 视线方向 = 长边方向；方形设备统一用"面向"方向
            XYZ viewDir;
            double shortLen;
            double maxLen = Math.Max(lenHand, lenFacing);
            bool square = maxLen < 1e-9
                || Math.Abs(lenHand - lenFacing) / maxLen < SQUARE_TOLERANCE;
            if (square) { viewDir = facing; shortLen = lenHand; }
            else if (lenHand >= lenFacing) { viewDir = hand; shortLen = lenFacing; }
            else { viewDir = facing; shortLen = lenHand; }

            // 4. 剖面框。Revit 实测约定：视线方向 = BasisZ，剖切面在 Min.Z，
            //    远裁剪在 Max.Z。剖切面过设备中心（Min.Z=0），深度沿视线延伸。
            double sideFt = s.SideExtensionM * M_TO_FT;
            double vertFt = s.VerticalExtensionM * M_TO_FT;
            double depthFt = s.DepthM * M_TO_FT;
            double halfW = shortLen / 2 + sideFt;
            double upExt = (maxZ - center.Z) + vertFt;
            double downExt = (center.Z - minZ) + vertFt;

            var t = Transform.Identity;
            t.Origin = center;
            t.BasisZ = viewDir;
            t.BasisY = XYZ.BasisZ;
            t.BasisX = t.BasisY.CrossProduct(t.BasisZ);

            var box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-halfW, -downExt, 0),
                Max = new XYZ(halfW, upExt, depthFt)
            };

            ViewSection view = ViewSection.CreateSection(doc, vftId, box);
            view.Name = UniqueViewName(doc,
                s.NamePrefix + "-" + TypeLabel(fi) + "-" + fi.Id.Value);
            return view;
        }

        /// <summary>
        /// 找设备上最大的水平风管连接件（通常是主送风口）。
        /// 朝上/朝下的连接件（如下回风）不用于定位。没有则返回 null。
        /// </summary>
        private static Connector FindMainDuctConnector(FamilyInstance fi)
        {
            var mep = fi.MEPModel;
            if (mep == null || mep.ConnectorManager == null) return null;

            Connector best = null;
            double bestArea = -1;
            foreach (Connector c in mep.ConnectorManager.Connectors)
            {
                try
                {
                    if (c.Domain != Domain.DomainHvac) continue;
                    XYZ dir = c.CoordinateSystem.BasisZ;
                    if (Math.Abs(dir.Z) > 0.7) continue; // 接近竖直

                    double area;
                    switch (c.Shape)
                    {
                        case ConnectorProfileType.Round:
                            area = Math.PI * c.Radius * c.Radius;
                            break;
                        case ConnectorProfileType.Rectangular:
                        case ConnectorProfileType.Oval:
                            area = c.Width * c.Height;
                            break;
                        default:
                            area = 0;
                            break;
                    }
                    if (area > bestArea) { best = c; bestArea = area; }
                }
                catch { /* 个别逻辑连接件取不到坐标系/尺寸，跳过 */ }
            }
            return best;
        }

        /// <summary>
        /// 以风口连接件定位创建【纵切】剖面：剖切面平行于两个相对风口中心的连线（气流轴），
        /// 沿设备侧向观察，一次看全送/回风路径。
        /// 裁剪范围 = 设备实际几何范围 + 设置里的外扩尺寸（左右/上下/进深）。
        /// </summary>
        private static View CreateConnectorAnchoredSection(
            Document doc, ElementId vftId, FamilyInstance fi, Connector main, SectionSettings s)
        {
            double sideFt = s.SideExtensionM * M_TO_FT;     // 左右外扩（沿气流方向）
            double vertFt = s.VerticalExtensionM * M_TO_FT;  // 上下外扩
            double depthFt = s.DepthM * M_TO_FT;             // 进深外扩（横向看穿设备）

            // 气流轴：优先取两个相对风口中心的连线；取不到相对风口时退回主风口朝向。
            XYZ mainDir = FlattenToHorizontal(main.CoordinateSystem.BasisZ) ?? XYZ.BasisX;
            XYZ airflow = mainDir;
            Connector opp = FindOppositeConnector(fi, main, mainDir);
            if (opp != null)
            {
                XYZ f = FlattenToHorizontal(opp.Origin - main.Origin);
                if (f != null) airflow = f;
            }

            // 纵切局部坐标系：X=气流（屏幕左右）、Y=竖直（屏幕上下）、Z=横向（视线法向）。
            XYZ bx = airflow;
            XYZ by = XYZ.BasisZ;
            XYZ bz = bx.CrossProduct(by);

            // 设备真实范围：实体采样点投影到局部轴（比世界包围盒更贴合旋转设备）。
            List<XYZ> pts = GeometryUtil.GetSamplePoints(fi);
            if (pts.Count == 0)
                throw new InvalidOperationException("取不到设备几何。");
            XYZ anchor = main.Origin;
            ProjectExtent(pts, anchor, bx, by, bz,
                out double minX, out double maxX, out double minY, out double maxY, out double minZ, out double maxZ);

            var t = Transform.Identity;
            t.Origin = anchor;
            t.BasisX = bx;
            t.BasisY = by;
            t.BasisZ = bz;
            var box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(minX - sideFt, minY - vertFt, minZ - depthFt),
                Max = new XYZ(maxX + sideFt, maxY + vertFt, maxZ + depthFt)
            };

            ViewSection view = ViewSection.CreateSection(doc, vftId, box);

            // 关键：ViewSection.CreateSection 不会采用传入框的裁剪 X/Y（实测会被放大到数米），
            // 必须生成后按裁剪框自身坐标系重算设备范围并显式设回，裁剪才会贴合设备。
            doc.Regenerate();
            BoundingBoxXYZ cb = view.CropBox;
            Transform ct = cb.Transform;
            ProjectExtent(pts, ct.Origin, ct.BasisX, ct.BasisY, ct.BasisZ,
                out double cMinX, out double cMaxX, out double cMinY, out double cMaxY, out double cMinZ, out double cMaxZ);
            cb.Min = new XYZ(cMinX - sideFt, cMinY - vertFt, cMinZ - depthFt);
            cb.Max = new XYZ(cMaxX + sideFt, cMaxY + vertFt, cMaxZ + depthFt);
            view.CropBox = cb;
            view.CropBoxActive = true;
            view.CropBoxVisible = true;

            view.Name = UniqueViewName(doc,
                s.NamePrefix + "-" + TypeLabel(fi) + "-" + fi.Id.Value);
            return view;
        }

        /// <summary>找与 main 大致相反方向、面积最大的另一个水平风口连接件；没有则返回 null。</summary>
        private static Connector FindOppositeConnector(FamilyInstance fi, Connector main, XYZ mainDir)
        {
            var mep = fi.MEPModel;
            if (mep == null || mep.ConnectorManager == null || mainDir == null) return null;

            Connector best = null;
            double bestArea = -1;
            foreach (Connector c in mep.ConnectorManager.Connectors)
            {
                try
                {
                    if (c.Domain != Domain.DomainHvac) continue;
                    XYZ dir = FlattenToHorizontal(c.CoordinateSystem.BasisZ);
                    if (dir == null) continue;
                    if (dir.DotProduct(mainDir) > -0.7) continue; // 需与主风口大致相反（自身会被此条排除）
                    double area = ConnectorArea(c);
                    if (area > bestArea) { bestArea = area; best = c; }
                }
                catch { /* 个别逻辑连接件取不到坐标系/尺寸，跳过 */ }
            }
            return best;
        }

        /// <summary>连接件截面积（圆/矩形/椭圆），取不到返回 0。</summary>
        private static double ConnectorArea(Connector c)
        {
            switch (c.Shape)
            {
                case ConnectorProfileType.Round:
                    return Math.PI * c.Radius * c.Radius;
                case ConnectorProfileType.Rectangular:
                case ConnectorProfileType.Oval:
                    return c.Width * c.Height;
                default:
                    return 0;
            }
        }

        /// <summary>把一组世界点投影到局部轴 (ax,ay,az)（相对 origin），求各轴 min/max。</summary>
        private static void ProjectExtent(List<XYZ> pts, XYZ origin, XYZ ax, XYZ ay, XYZ az,
            out double minX, out double maxX, out double minY, out double maxY, out double minZ, out double maxZ)
        {
            minX = minY = minZ = double.MaxValue;
            maxX = maxY = maxZ = double.MinValue;
            foreach (XYZ p in pts)
            {
                XYZ d = p - origin;
                double x = d.DotProduct(ax), y = d.DotProduct(ay), z = d.DotProduct(az);
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
        }

        /// <summary>向量投影到水平面并归一化；接近竖直（水平分量过小）返回 null。</summary>
        private static XYZ FlattenToHorizontal(XYZ v)
        {
            if (v == null) return null;
            var flat = new XYZ(v.X, v.Y, 0);
            return flat.GetLength() < 1e-6 ? null : flat.Normalize();
        }

        internal static string TypeLabel(FamilyInstance fi)
        {
            string name = fi.Symbol != null ? fi.Symbol.Name : fi.Name;
            return SanitizeViewName(name);
        }

        private static string ElementLabel(FamilyInstance fi)
        {
            string cat = fi.Category != null ? fi.Category.Name : "?";
            return cat + " " + TypeLabel(fi) + " (ID " + fi.Id.Value + ")";
        }

        /// <summary>去掉 Revit 视图名不允许的字符。</summary>
        private static string SanitizeViewName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "未命名";
            var sb = new StringBuilder(name.Length);
            const string bad = "\\:{}[]|;<>?`~/\r\n\t";
            foreach (char c in name)
                sb.Append(bad.IndexOf(c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        internal static string UniqueViewName(Document doc, string desired)
        {
            desired = SanitizeViewName(desired);
            string name = desired;
            int suffix = 2;
            while (ViewNameExists(doc, name))
                name = desired + " (" + (suffix++) + ")";
            return name;
        }

        private static bool ViewNameExists(Document doc, string name)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(v => string.Equals(v.Name, name, StringComparison.Ordinal));
        }
    }
}
