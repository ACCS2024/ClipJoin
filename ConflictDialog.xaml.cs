using System.Collections.Generic;
using System.Windows;
using ClipJoin.Services;

namespace ClipJoin
{
    public partial class ConflictDialog : Window
    {
        public bool Confirmed { get; private set; }
        public ConflictResolution SelectedResolution { get; private set; } = ConflictResolution.Skip;
        public bool RememberChoice => RememberCheck.IsChecked == true;

        public ConflictDialog(IReadOnlyList<ConflictItem> conflicts)
        {
            InitializeComponent();

            TitleText.Text = $"发现 {conflicts.Count} 个输出文件已存在";
            SubtitleText.Text = $"再次合并将产生以下冲突，请决定如何处理";

            ConflictList.ItemsSource = conflicts;
            Loaded += (_, _) => MaxHeight = SystemParameters.WorkArea.Height * 0.88;
        }

        private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedResolution = RadioOverwrite.IsChecked == true
                ? ConflictResolution.Overwrite
                : RadioRename.IsChecked == true
                    ? ConflictResolution.Rename
                    : ConflictResolution.Skip;

            Confirmed = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }
    }
}
