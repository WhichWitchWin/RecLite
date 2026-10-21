using System;
using System.IO;
using System.Windows;

namespace RecLite
{
    public partial class InputDialog : Window
    {
        public string Value { get; private set; }

        public InputDialog(string title, string initial, string hint)
        {
            InitializeComponent();
            Title = title;
            Input.Text = initial ?? "";
            Hint.Text = hint ?? "";
            Loaded += (s, e) => { Input.Focus(); Input.SelectAll(); };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var v = (Input.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(v))
            {
                Hint.Text = "文件名不能为空";
                return;
            }
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                if (v.IndexOf(c) >= 0)
                {
                    Hint.Text = "文件名包含非法字符：" + c;
                    return;
                }
            }
            Value = v;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
