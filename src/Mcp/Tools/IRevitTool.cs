using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// Contract for every Revit MCP tool.
    /// Execute() is always called on the Revit main thread (via ExternalEvent).
    /// </summary>
    public interface IRevitTool
    {
        /// <summary>MCP tool name (e.g. "get_model_info"). Must be unique and snake_case.</summary>
        string Name { get; }

        /// <summary>Human-readable description shown to the LLM in tools/list.</summary>
        string Description { get; }

        /// <summary>JSON Schema describing the input object.</summary>
        JObject InputSchema { get; }

        /// <summary>
        /// Execute on Revit main thread.
        /// Throw to signal an error (caller wraps it into the {ok:false,error} envelope).
        /// Return any JObject as the success payload.
        /// </summary>
        JObject Execute(UIApplication uiapp, JObject input);
    }
}
