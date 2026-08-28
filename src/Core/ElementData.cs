using System;
using System.Collections.Generic;

namespace JarviTools.Core
{
    /// <summary>
    /// 图元数据类 - 用于存储图元的导出数据
    /// </summary>
    public class ElementData
    {
        public string ElementId { get; set; }
        public string Category { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string Level { get; set; }
        public string MajorName { get; set; }
        public string Subcontractor { get; set; }
        public string ShouldExport { get; set; }
        
        public Dictionary<string, string> Parameters { get; set; }

        public ElementData()
        {
            Parameters = new Dictionary<string, string>();
            ElementId = string.Empty;
            Category = string.Empty;
            FamilyName = string.Empty;
            TypeName = string.Empty;
            Level = string.Empty;
            MajorName = string.Empty;
            Subcontractor = string.Empty;
            ShouldExport = string.Empty;
        }
    }
}
