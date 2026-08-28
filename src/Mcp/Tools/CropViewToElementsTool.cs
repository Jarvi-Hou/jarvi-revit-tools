using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 将视图裁剪框设置为包含指定元素的范围。
    /// 可选 padding 参数控制边距（默认 0.5 米）。
    /// </summary>
    public class CropViewToElementsTool : IRevitTool
    {
        public string Name => "crop_view_to_elements";
        public string Description =>
            "将活动视图裁剪到选定的元素范围。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewId"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "ElementId of the target view."
                },
                ["elementIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["description"] = "Array of ElementIds to fit the crop box to."
                },
                ["padding_m"] = new JObject
                {
                    ["type"] = "number",
                    ["description"] = "Padding around the elements in meters (default 0.5).",
                    ["default"] = 0.5
                }
            },
            ["required"] = new JArray { "viewId", "elementIds" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            var viewId = new ElementId((long)input["viewId"]);
            var view = doc.GetElement(viewId) as View;
            if (view == null)
                throw new ArgumentException("viewId does not refer to a View element.");

            // 检查 view template
            if (view.ViewTemplateId != ElementId.InvalidElementId)
            {
                var template = doc.GetElement(view.ViewTemplateId);
                throw new InvalidOperationException(
                    "View '" + view.Name + "' is controlled by template '" +
                    (template?.Name ?? "unknown") +
                    "'. Crop box cannot be modified directly on template-controlled views.");
            }

            // 解析 elementIds
            var elemIds = ((JArray)input["elementIds"])
                .Select(id => new ElementId((long)id))
                .ToList();
            if (elemIds.Count == 0)
                throw new ArgumentException("elementIds must contain at least one element.");

            // padding：默认 0.5 米，转英尺
            double padM = 0.5;
            var padToken = input["padding_m"];
            if (padToken != null && padToken.Type != JTokenType.Null)
                padM = (double)padToken;
            double padFt = padM / 0.3048;

            // 计算所有元素的 union BoundingBoxXYZ
            BoundingBoxXYZ union = null;
            foreach (var elemId in elemIds)
            {
                var elem = doc.GetElement(elemId);
                if (elem == null) continue;
                var bb = elem.get_BoundingBox(view);
                if (bb == null) continue;

                if (union == null)
                {
                    union = new BoundingBoxXYZ
                    {
                        Min = new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                        Max = new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
                    };
                }
                else
                {
                    union.Min = new XYZ(
                        Math.Min(union.Min.X, bb.Min.X),
                        Math.Min(union.Min.Y, bb.Min.Y),
                        Math.Min(union.Min.Z, bb.Min.Z));
                    union.Max = new XYZ(
                        Math.Max(union.Max.X, bb.Max.X),
                        Math.Max(union.Max.Y, bb.Max.Y),
                        Math.Max(union.Max.Z, bb.Max.Z));
                }
            }

            if (union == null)
                throw new InvalidOperationException("None of the specified elements have a bounding box in this view.");

            // 添加 padding（只在 XY 平面）
            union.Min = new XYZ(union.Min.X - padFt, union.Min.Y - padFt, union.Min.Z);
            union.Max = new XYZ(union.Max.X + padFt, union.Max.Y + padFt, union.Max.Z);

            string viewName = view.Name;

            using (var tx = new Transaction(doc, "Crop view to elements"))
            {
                tx.Start();
                try
                {
                    view.CropBoxActive = true;
                    view.CropBox = union;
                    view.CropBoxVisible = true;
                    JarviTools.Core.TransactionSafety.Commit(tx, "Crop view to elements");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to crop view '" + viewName + "': " + ex.Message, ex);
                }
            }

            return new JObject
            {
                ["viewName"] = viewName,
                ["cropBoxSet"] = true,
                ["bounds"] = new JObject
                {
                    ["min"] = new JObject
                    {
                        ["x"] = (double)Math.Round(union.Min.X * 0.3048, 4),
                        ["y"] = (double)Math.Round(union.Min.Y * 0.3048, 4),
                        ["z"] = (double)Math.Round(union.Min.Z * 0.3048, 4)
                    },
                    ["max"] = new JObject
                    {
                        ["x"] = (double)Math.Round(union.Max.X * 0.3048, 4),
                        ["y"] = (double)Math.Round(union.Max.Y * 0.3048, 4),
                        ["z"] = (double)Math.Round(union.Max.Z * 0.3048, 4)
                    }
                },
                ["paddingUsed_m"] = padM
            };
        }
    }
}
