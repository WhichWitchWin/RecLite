using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RecLite.Core;

namespace RecLite
{
    /// <summary>
    /// 录音文件列表窗口。
    /// 存在的意义：有些环境下调用 explorer.exe 会报 0xc0000142，
    /// 与其死磕，不如把文件直接列在程序里，双击就能听。
    /// </summary>
    public partial class FileListDialog : Window
    {
        private readonly Func<string, bool> _onPlay;
        private readonly string _dir;

        public class FileRow
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public string SizeText { get; set; }
            public string ModifiedText { get; set; }
            public long Size { get; set; }
            public DateTime Modified { get; set; }
            public FileRow FileRowSelf => this;
            public override string ToString() => Name;
        }

        public FileListDialog(string dir, Func<string, bool> onPlay)
        {
            InitializeComponent();
            _onPlay = onPlay;
            _dir = dir;
            PathText.Text = dir;
            LoadFiles();
        }

        private void LoadFiles()
        {
            var rows = new List<FileRow>();
            try
            {
                if (Directory.Exists(_dir))
                {
                    foreach (var f in Directory.GetFiles(_dir).OrderByDescending(x => File.GetLastWriteTime(x)))
                    {
                        var fi = new FileInfo(f);
                        rows.Add(new FileRow
                        {
                            Name = fi.Name,
                            FullPath = fi.FullName,
                            Size = fi.Length,
                            Modified = fi.LastWriteTime,
                            SizeText = FormatSize(fi.Length),
                            ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }
                }
            }
            catch (Exception ex) { Log.Error("LoadFiles", ex); }

            FileList.ItemsSource = rows;
            if (rows.Count > 0) FileList.SelectedIndex = 0;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }

        private FileRow Selected => FileList.SelectedItem as FileRow;

        private void Play_Click(object sender, RoutedEventArgs e) => PlaySelected();

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();

        private void PlaySelected()
        {
            var row = Selected;
            if (row == null || _onPlay == null) return;
            // 关闭列表窗口，回到主界面播放
            var ok = _onPlay(row.FullPath);
            if (ok) { DialogResult = true; Close(); }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadFiles();

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 先试系统 Shell 打开
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _dir,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                Log.Error("OpenFolder", ex);
                // 再试完整路径的 explorer
                try
                {
                    var winExplorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    System.Diagnostics.Process.Start(winExplorer, "/select,\"" + _dir + "\"");
                    return;
                }
                catch (Exception ex2) { Log.Error("OpenFolder-2", ex2); }
                MessageBox.Show("系统打开文件夹失败（0xc0000142 通常是系统环境问题）。\n\n" +
                                "路径如下，可手动复制到地址栏：\n" + _dir,
                    "RecLite", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    }
}
