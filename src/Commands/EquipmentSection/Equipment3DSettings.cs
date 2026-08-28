namespace JarviTools.Commands.EquipmentSection
{
    /// <summary>设备三维检查视图的可配置参数，JSON 持久化记住上次值。</summary>
    public class Equipment3DSettings
    {
        /// <summary>剖面框与设备+风管几何的包裹距离（毫米），0 = 贴紧。</summary>
        public double PaddingMm { get; set; } = 500;
        public string NamePrefix { get; set; } = "设备三维";
    }
}
