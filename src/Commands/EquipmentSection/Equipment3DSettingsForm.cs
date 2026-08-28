using System.Windows.Forms;

namespace JarviTools.Commands.EquipmentSection
{
    internal class Equipment3DSettingsForm : Form
    {
        private readonly NumericUpDown _padding = new NumericUpDown
        {
            DecimalPlaces = 0,
            Increment = 50,
            Minimum = 0,
            Maximum = 5000,
            Dock = DockStyle.Fill
        };
        private readonly TextBox _prefix = new TextBox { Dock = DockStyle.Fill };

        public Equipment3DSettingsForm(Equipment3DSettings s)
        {
            Text = "设备三维检查 — 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(360, 150);

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(12)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));

            table.Controls.Add(MakeLabel("剖面框包裹距离 (mm，0=贴紧)"), 0, 0);
            table.Controls.Add(_padding, 1, 0);
            table.Controls.Add(MakeLabel("命名前缀"), 0, 1);
            table.Controls.Add(_prefix, 1, 1);

            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons, 1, 2);

            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;

            decimal pad = (decimal)s.PaddingMm;
            if (pad < _padding.Minimum) pad = _padding.Minimum;
            if (pad > _padding.Maximum) pad = _padding.Maximum;
            _padding.Value = pad;
            _prefix.Text = s.NamePrefix;
        }

        /// <summary>把界面值写回设置对象（仅在 DialogResult.OK 后调用）。</summary>
        public void Apply(Equipment3DSettings s)
        {
            s.PaddingMm = (double)_padding.Value;
            s.NamePrefix = string.IsNullOrWhiteSpace(_prefix.Text) ? "设备三维" : _prefix.Text.Trim();
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
