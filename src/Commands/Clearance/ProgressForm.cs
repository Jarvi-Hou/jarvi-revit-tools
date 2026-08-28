using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace JarviTools.Commands.Clearance
{
    /// <summary>Win32 互操作：计算期间禁用 Revit 主窗口，防止 DoEvents 泵消息导致 API 重入。</summary>
    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool EnableWindow(IntPtr hWnd, bool bEnable);
    }

    /// <summary>把 Revit 主窗口句柄包装成 WinForms 的 IWin32Window，作为窗体 owner。</summary>
    internal class Win32Window : IWin32Window
    {
        private readonly IntPtr _handle;
        public Win32Window(IntPtr handle) { _handle = handle; }
        public IntPtr Handle { get { return _handle; } }
    }

    /// <summary>简易进度窗（非模态 + DoEvents 泵消息），带取消。</summary>
    internal class ProgressForm : Form
    {
        private readonly Label _label = new Label
        {
            AutoSize = true,
            Location = new System.Drawing.Point(16, 14)
        };
        private readonly ProgressBar _bar = new ProgressBar
        {
            Bounds = new System.Drawing.Rectangle(16, 42, 340, 22),
            Minimum = 0,
            Maximum = 100
        };

        public bool Cancelled { get; private set; }

        public ProgressForm(string title)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(372, 110);
            var cancel = new Button { Text = "取消", Bounds = new System.Drawing.Rectangle(280, 72, 76, 28) };
            cancel.Click += (o, e) => { Cancelled = true; };
            Controls.Add(_label);
            Controls.Add(_bar);
            Controls.Add(cancel);
        }

        public void Report(int current, int total)
        {
            if (total <= 0) total = 1;
            int pct = (int)(100L * current / total);
            if (pct > 100) pct = 100;
            _bar.Value = pct;
            _label.Text = "正在计算净高… " + current + " / " + total;
            System.Windows.Forms.Application.DoEvents();
        }
    }
}
