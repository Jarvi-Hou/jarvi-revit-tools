using System;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Returns metadata about the currently active Revit document.
    /// Inputs: none.
    /// </summary>
    public class GetModelInfoTool : IRevitTool
    {
        public string Name => "get_model_info";

        public string Description =>
            "获取当前 Revit 文档的基本信息。完整本机/网络路径默认隐藏，只有 includePath=true 时才返回。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["includePath"] = new JObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "Return the full local/network model path. May contain customer or workstation information."
                }
            },
            ["additionalProperties"] = false
        };

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null)
                throw new InvalidOperationException("UIApplication is null.");

            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null)
                throw new InvalidOperationException("No active Revit document.");

            var doc = uidoc.Document;
            if (doc == null)
                throw new InvalidOperationException("Active UIDocument has no Document.");

            Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;

            string title = doc.Title ?? string.Empty;
            string path = doc.PathName ?? string.Empty;
            bool includePath = input != null && ((bool?)input["includePath"]).GetValueOrDefault();
            bool isWorkshared = doc.IsWorkshared;
            bool isFamilyDocument = doc.IsFamilyDocument;
            string revitVersion = app != null ? (app.VersionNumber ?? "unknown") : "unknown";

            string activeViewName = null;
            try
            {
                var activeView = uidoc.ActiveView;
                if (activeView != null)
                    activeViewName = activeView.Name;
            }
            catch
            {
                activeViewName = null;
            }

            return new JObject
            {
                ["title"] = title,
                ["path"] = includePath && !string.IsNullOrEmpty(path) ? (JToken)path : JValue.CreateNull(),
                ["pathIncluded"] = includePath,
                ["isWorkshared"] = isWorkshared,
                ["isFamilyDocument"] = isFamilyDocument,
                ["revitVersion"] = revitVersion,
                ["activeViewName"] = activeViewName == null ? (JToken)JValue.CreateNull() : activeViewName
            };
        }
    }
}
