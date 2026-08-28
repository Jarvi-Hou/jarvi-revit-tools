namespace JarviTools.Commands.EquipmentSection
{
    /// <summary>设备检查剖面的可配置参数（米），JSON 持久化记住上次值。</summary>
    public class SectionSettings
    {
        public double SideExtensionM { get; set; } = 0.3;      // 左右扩展（设备沿气流范围外扩）
        public double VerticalExtensionM { get; set; } = 0.3;  // 上下扩展（设备高度外扩）
        public double DepthM { get; set; } = 0.3;              // 剖面深度（设备横向范围外扩）
        public string NamePrefix { get; set; } = "设备检查";
    }
}
