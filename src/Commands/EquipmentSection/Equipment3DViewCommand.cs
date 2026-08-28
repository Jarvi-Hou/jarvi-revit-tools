using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JarviTools.Commands.Common;
using JarviTools.Mcp.Server;

namespace JarviTools.Commands.EquipmentSection
{
    /// <summary>
    /// 为每台选中设备（VRV 等）创建一个三维视图：
    /// 从设备的风管连接件沿风管网络追踪到末端（风口），
    /// 剖面框包住设备 + 整段风管，包裹距离可配置（0 = 贴紧）。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Equipment3DViewCommand : IExternalCommand
    {
        private const double M_TO_FT = 1 / 0.3048;
        private const int TRAVERSE_CAP = 500; // 防呆：单台设备追踪的连通构件数量上限

        private static readonly long[] DuctNetworkCats =
        {
            (long)BuiltInCategory.OST_DuctCurves,
            (long)BuiltInCategory.OST_FlexDuctCurves,
            (long)BuiltInCategory.OST_DuctFitting,
            (long)BuiltInCategory.OST_DuctAccessory,
            (long)BuiltInCategory.OST_DuctTerminal,
            (long)BuiltInCategory.OST_MechanicalEquipment,
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("设备三维检查", "请先打开一个 Revit 项目文件。");
                return Result.Cancelled;
            }
            Document doc = uidoc.Document;

            try
            {
                // 1. 目标设备：预选优先，否则框选（复用剖面命令的选择逻辑）
                List<FamilyInstance> equipment = EquipmentSectionCommand.GetTargetEquipment(uidoc);
                if (equipment == null) return Result.Cancelled;
                if (equipment.Count == 0)
                {
                    TaskDialog.Show("设备三维检查",
                        "所选构件中没有机械设备或电气设备。\n\n支持类别：机械设备、电气设备。");
                    return Result.Cancelled;
                }

                // 2. 设置
                var settings = JsonSettingsStore.Load<Equipment3DSettings>("Equipment3D");
                using (var form = new Equipment3DSettingsForm(settings))
                {
                    if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return Result.Cancelled;
                    form.Apply(settings);
                }
                JsonSettingsStore.Save("Equipment3D", settings);

                // 3. 三维视图类型
                var vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                if (vft == null)
                {
                    TaskDialog.Show("设备三维检查", "项目中没有三维视图类型，无法创建视图。");
                    return Result.Failed;
                }

                double padFt = settings.PaddingMm / 1000.0 * M_TO_FT;

                // 4. 逐台生成（整批一次撤销，单台失败不影响其他）
                var created = new List<string>();
                var failed = new List<string>();
                var truncatedOnes = new List<string>();
                using (var group = new TransactionGroup(doc, "批量生成设备三维检查视图"))
                {
                    try
                    {
                        group.Start();
                        foreach (FamilyInstance fi in equipment)
                        {
                            try
                            {
                                bool truncated;
                                string createdViewName;
                                using (var tx = new Transaction(doc, "设备三维检查视图"))
                                {
                                    try
                                    {
                                        tx.Start();
                                        View3D view = Create3DForEquipment(
                                            doc, vft.Id, fi, padFt, settings.NamePrefix, out truncated);
                                        createdViewName = view.Name;
                                        JarviTools.Core.TransactionSafety.Commit(
                                            tx,
                                            "Create equipment 3D inspection view");
                                    }
                                    catch
                                    {
                                        if (tx.HasStarted() && !tx.HasEnded())
                                            tx.RollBack();
                                        throw;
                                    }
                                }
                                created.Add(createdViewName);
                                if (truncated)
                                    truncatedOnes.Add(EquipmentSectionCommand.TypeLabel(fi) + "(ID " + fi.Id.Value + ")");
                            }
                            catch (Exception ex)
                            {
                                failed.Add(EquipmentSectionCommand.TypeLabel(fi)
                                    + " (ID " + fi.Id.Value + ")：" + ex.Message);
                            }
                        }
                        JarviTools.Core.TransactionSafety.Assimilate(
                            group,
                            "Batch equipment 3D inspection views");
                    }
                    catch
                    {
                        if (group.HasStarted() && !group.HasEnded())
                            group.RollBack();
                        throw;
                    }
                }

                // 5. 汇总
                var sb = new StringBuilder();
                sb.AppendLine("成功生成三维视图：" + created.Count + " 个");
                if (failed.Count > 0)
                {
                    sb.AppendLine("失败：" + failed.Count + " 个");
                    foreach (string f in failed.Take(10)) sb.AppendLine("  · " + f);
                    if (failed.Count > 10) sb.AppendLine("  ……");
                }
                if (truncatedOnes.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("以下设备的风管网络超过 " + TRAVERSE_CAP + " 个构件，剖面框只包住了已追踪部分"
                        + "（通常说明支管接入了大干管）：");
                    foreach (string t in truncatedOnes.Take(5)) sb.AppendLine("  · " + t);
                }
                sb.AppendLine();
                sb.AppendLine("视图已在项目浏览器的三维视图分组下，可 Ctrl+Z 一次性撤销整批。");
                TaskDialog.Show("设备三维检查 — 完成", sb.ToString());

                return created.Count > 0 ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                Logger.Error("Equipment3DViewCommand failed", ex);
                TaskDialog.Show("设备三维检查 — 错误", "执行失败：\n" + ex.Message);
                return Result.Failed;
            }
        }

        // ==================== 三维视图生成 ====================

        private static View3D Create3DForEquipment(
            Document doc, ElementId vftId, FamilyInstance fi, double padFt,
            string namePrefix, out bool truncated)
        {
            List<Element> network = CollectDuctNetwork(fi, out truncated);

            // 剖面框 = 所有构件包围盒的并集 + 包裹距离
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (Element e in network)
            {
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                for (int i = 0; i < 8; i++)
                {
                    double x = ((i & 1) == 0) ? bb.Min.X : bb.Max.X;
                    double y = ((i & 2) == 0) ? bb.Min.Y : bb.Max.Y;
                    double z = ((i & 4) == 0) ? bb.Min.Z : bb.Max.Z;
                    XYZ p = bb.Transform.OfPoint(new XYZ(x, y, z));
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                    if (p.Z > maxZ) maxZ = p.Z;
                }
            }
            if (minX == double.MaxValue)
                throw new InvalidOperationException("取不到设备几何范围。");

            View3D view = View3D.CreateIsometric(doc, vftId);
            view.SetSectionBox(new BoundingBoxXYZ
            {
                Min = new XYZ(minX - padFt, minY - padFt, minZ - padFt),
                Max = new XYZ(maxX + padFt, maxY + padFt, maxZ + padFt)
            });

            view.Name = EquipmentSectionCommand.UniqueViewName(doc,
                namePrefix + "-" + EquipmentSectionCommand.TypeLabel(fi) + "-" + fi.Id.Value);
            return view;
        }

        /// <summary>
        /// 从设备出发，沿风管域的连接件追踪整个连通网络
        /// （风管/软风管/管件/附件/风口/相连设备），直到末端或达到防呆上限。
        /// </summary>
        private static List<Element> CollectDuctNetwork(FamilyInstance root, out bool truncated)
        {
            truncated = false;
            var visited = new HashSet<ElementId> { root.Id };
            var result = new List<Element> { root };
            var queue = new Queue<Element>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                if (visited.Count >= TRAVERSE_CAP) { truncated = true; break; }
                Element cur = queue.Dequeue();
                ConnectorManager mgr = GetConnectorManager(cur);
                if (mgr == null) continue;

                foreach (Connector c in mgr.Connectors)
                {
                    try
                    {
                        if (c.Domain != Domain.DomainHvac) continue;
                        if (!c.IsConnected) continue;
                        foreach (Connector rc in c.AllRefs)
                        {
                            Element other = rc.Owner;
                            if (other == null || other.Id == cur.Id) continue;
                            if (other is MEPSystem) continue;
                            if (visited.Contains(other.Id)) continue;
                            if (other.Category == null
                                || !DuctNetworkCats.Contains(other.Category.Id.Value)) continue;
                            visited.Add(other.Id);
                            result.Add(other);
                            queue.Enqueue(other);
                        }
                    }
                    catch { /* 个别连接件信息不全，跳过 */ }
                }
            }
            return result;
        }

        private static ConnectorManager GetConnectorManager(Element e)
        {
            var mc = e as MEPCurve; // Duct / FlexDuct 等
            if (mc != null) return mc.ConnectorManager;
            var fi = e as FamilyInstance;
            if (fi != null && fi.MEPModel != null) return fi.MEPModel.ConnectorManager;
            return null;
        }
    }
}
