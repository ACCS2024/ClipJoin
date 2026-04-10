using System.Collections.Generic;
using System.Windows;

namespace ClipJoin
{
    public partial class SubdirConfirmDialog : Window
    {
        public SubdirConfirmDialog(int groupCount, IEnumerable<(string Name, int VideoCount)> groups)
        {
            InitializeComponent();

            TitleText.Text = $"检测到子目录模式";
            SubtitleText.Text = $"共发现 {groupCount} 个包含视频的文件夹";

            var items = new List<GroupItem>();
            foreach (var (name, count) in groups)
                items.Add(new GroupItem { Name = name, CountLabel = $"{count} 个视频" });

            GroupList.ItemsSource = items;
            Loaded += (_, _) => MaxHeight = SystemParameters.WorkArea.Height * 0.88;
        }

        public bool Confirmed { get; private set; }

        private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        private class GroupItem
        {
            public string Name { get; set; } = "";
            public string CountLabel { get; set; } = "";
        }
    }
}
