using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 对齐多个视口（水平或垂直）。
    /// anchor="first" = 以第一个视口为基准，anchor="center" = 以所有视口的中心为基准。
    /// </summary>
    public class AlignViewportsTool : IRevitTool
    {
        public string Name => "align_viewports";
        public string Description =>
            "对齐多个图纸视口。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["viewportIds"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "number" },
                    ["description"] = "Array of viewport ElementIds to align (at least 2)."
                },
                ["axis"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Alignment axis: 'horizontal' or 'vertical'.",
                    ["enum"] = new JArray { "horizontal", "vertical" }
                },
                ["anchor"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Anchor: 'first' (default, align to first) or 'center' (align to average).",
                    ["enum"] = new JArray { "first", "center" },
                    ["default"] = "first"
                }
            },
            ["required"] = new JArray { "viewportIds", "axis" },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            if (input == null) throw new ArgumentException("Input is required.");

            string axis = (string)input["axis"];
            if (axis != "horizontal" && axis != "vertical")
                throw new ArgumentException("axis must be 'horizontal' or 'vertical'.");

            string anchor = "first";
            var anchorToken = input["anchor"];
            if (anchorToken != null && anchorToken.Type != JTokenType.Null)
                anchor = (string)anchorToken;

            var ids = ((JArray)input["viewportIds"])
                .Select(id => new ElementId((long)id))
                .ToList();

            // 获取所有有效的视口
            var vps = ids
                .Select(id => doc.GetElement(id) as Viewport)
                .Where(v => v != null)
                .ToList();

            if (vps.Count < 2)
                throw new ArgumentException("At least 2 valid viewports are required for alignment.");

            // 计算锚点位置
            XYZ anchorCenter;
            if (anchor == "center")
            {
                anchorCenter = new XYZ(
                    vps.Average(v => v.GetBoxCenter().X),
                    vps.Average(v => v.GetBoxCenter().Y),
                    0);
            }
            else
            {
                anchorCenter = vps[0].GetBoxCenter();
            }

            using (var tx = new Transaction(doc, "Align viewports"))
            {
                tx.Start();
                try
                {
                    // anchor=="center" 时移动全部，否则跳过第一个
                    var toMove = anchor == "center" ? vps : vps.Skip(1).ToList();
                    foreach (var vp in toMove)
                    {
                        var c = vp.GetBoxCenter();
                        XYZ delta;
                        if (axis == "horizontal")
                        {
                            // 水平对齐 = Y 坐标统一
                            delta = new XYZ(0, anchorCenter.Y - c.Y, 0);
                        }
                        else
                        {
                            // 垂直对齐 = X 坐标统一
                            delta = new XYZ(anchorCenter.X - c.X, 0, 0);
                        }
                        ElementTransformUtils.MoveElement(doc, vp.Id, delta);
                    }
                    JarviTools.Core.TransactionSafety.Commit(tx, "Align viewports");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw new InvalidOperationException(
                        "Failed to align viewports: " + ex.Message, ex);
                }
            }

            return new JObject
            {
                ["aligned"] = vps.Count,
                ["axis"] = axis,
                ["alignedTo"] = new JObject
                {
                    ["x"] = Math.Round(anchorCenter.X * 0.3048, 4),
                    ["y"] = Math.Round(anchorCenter.Y * 0.3048, 4)
                }
            };
        }
    }
}
