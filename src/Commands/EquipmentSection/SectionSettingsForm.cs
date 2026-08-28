using System.Windows.Forms;

namespace JarviTools.Commands.EquipmentSection
{
    internal class SectionSettingsForm : Form
    {
        private readonly NumericUpDown _side = MakeNum();
        private readonly NumericUpDown _vert = MakeNum();
        private readonly NumericUpDown _depth = MakeNum();
        private readonly TextBox _prefix = new TextBox { Dock = DockStyle.Fill };

        public SectionSettingsForm(SectionSettings s)
        {
            Text = "设备检查剖面 — 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(340, 210);

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(12)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
            for (int i = 0; i < 4; i++)
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));

            table.Controls.Add(MakeLabel("左右扩展 (m)"), 0, 0); table.Controls.Add(_side, 1, 0);
            table.Controls.Add(MakeLabel("上下扩展 (m)"), 0, 1); table.Controls.Add(_vert, 1, 1);
            table.Controls.Add(MakeLabel("剖面深度 (m)"), 0, 2); table.Controls.Add(_depth, 1, 2);
            table.Controls.Add(MakeLabel("命名前缀"), 0, 3); table.Controls.Add(_prefix, 1, 3);

            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons, 1, 4);

            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;

            _side.Value = ClampToRange(s.SideExtensionM, _side);
            _vert.Value = ClampToRange(s.VerticalExtensionM, _vert);
            _depth.Value = ClampToRange(s.DepthM, _depth);
            _prefix.Text = s.NamePrefix;
        }

        /// <summary>把界面值写回设置对象（仅在 DialogResult.OK 后调用）。</summary>
        public void Apply(SectionSettings s)
        {
            s.SideExtensionM = (double)_side.Value;
            s.VerticalExtensionM = (double)_vert.Value;
            s.DepthM = (double)_depth.Value;
            s.NamePrefix = string.IsNullOrWhiteSpace(_prefix.Text) ? "设备检查" : _prefix.Text.Trim();
        }

        private static decimal ClampToRange(double value, NumericUpDown num)
        {
            decimal v = (decimal)value;
            if (v < num.Minimum) return num.Minimum;
            if (v > num.Maximum) return num.Maximum;
            return v;
        }

        private static NumericUpDown MakeNum()
        {
            return new NumericUpDown
            {
                DecimalPlaces = 1,
                Increment = 0.1m,
                Minimum = 0.1m,
                Maximum = 50m,
                Dock = DockStyle.Fill
            };
        }

        private static Label MakeLabel(string t)
        {
            return new Label
            {
                Text = t,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            };
        }
    }
}
