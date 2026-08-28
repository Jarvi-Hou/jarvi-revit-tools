using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 列出所有链接（RevitLinkType）的状态：加载/未加载/未找到等。
    /// 帮助排查链接丢失问题。
    /// </summary>
    public class GetLinkStatusTool : IRevitTool
    {
        public string Name => "get_link_status";
        public string Description =>
            "列出 Revit 链接状态与路径类型。完整本机/网络路径默认隐藏，只有 includePaths=true 时才返回。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["includePaths"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "Return full link paths. May contain customer or workstation information."
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document ?? throw new InvalidOperationException("Active UIDocument has no Document.");
            bool includePaths = input != null && ((bool?)input["includePaths"]).GetValueOrDefault();

            var linksArr = new JArray();

            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .OrderBy(lt =>
                {
                    try { return lt.Name; } catch { return ""; }
                });

            foreach (var lt in linkTypes)
            {
                string name = null;
                try { name = lt.Name; } catch { }

                LinkedFileStatus lfs;
                bool statusOk = true;
                try { lfs = lt.GetLinkedFileStatus(); }
                catch { statusOk = false; lfs = LinkedFileStatus.Unloaded; }

                string pathType = null;
                string fullPath = null;
                try
                {
                    var extRef = lt.GetExternalFileReference();
                    if (extRef != null)
                    {
                        pathType = extRef.PathType.ToString();
                        var absPath = extRef.GetAbsolutePath();
                        if (absPath != null)
                            fullPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(absPath);
                    }
                }
                catch { }

                linksArr.Add(new JObject
                {
                    ["id"] = lt.Id.Value,
                    ["name"] = name ?? (JToken)JValue.CreateNull(),
                    ["status"] = lfs.ToString(),
                    ["statusKnown"] = statusOk,
                    ["isLoaded"] = statusOk && lfs == LinkedFileStatus.Loaded,
                    ["pathType"] = pathType ?? (JToken)JValue.CreateNull(),
                    ["fullPath"] = includePaths && !string.IsNullOrWhiteSpace(fullPath)
                        ? (JToken)fullPath
                        : JValue.CreateNull()
                });
            }

            return new JObject
            {
                ["links"] = linksArr,
                ["total"] = linksArr.Count,
                ["pathsIncluded"] = includePaths
            };
        }
    }
}
