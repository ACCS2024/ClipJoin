using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ClipJoin.Services;

namespace ClipJoin
{
    public partial class MergeResultDialog : Window
    {
        private readonly string _outputPath;

        public MergeResultDialog(MergeSummary summary, string outputPath)
        {
            InitializeComponent();
            _outputPath = outputPath;

            SuccessCount.Text = summary.SucceededCount.ToString();
            SkipCount.Text    = summary.SkippedCount.ToString();
            FailCount.Text    = summary.Failed.Count.ToString();

            var elapsed = VideoMergeService.FormatTimeSpan(summary.TotalElapsed);

            if (summary.IsFullSuccess)
            {
                // All good ─────────────────────────────────────────────
                IconBorder.Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xFF, 0xED));
                IconText.Text         = "🎉";
                TitleText.Text        = "全部合并完成！";
                SubtitleText.Text     = $"共 {summary.SucceededCount} 个任务，耗时 {elapsed}";
                FailCount.Foreground  = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));
                OpenFolderBtn.Visibility = Visibility.Visible;
            }
            else if (summary.SucceededCount > 0)
            {
                // Partial failure ────────────────────────────────────
                IconBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF7, 0xE6));
                IconText.Text         = "⚠";
                TitleText.Text        = $"合并完成（{summary.Failed.Count} 个任务失败）";
                SubtitleText.Text     = $"成功 {summary.SucceededCount} 个，失败 {summary.Failed.Count} 个，耗时 {elapsed}";
                OpenFolderBtn.Visibility = Visibility.Visible;
            }
            else
            {
                // All failed ─────────────────────────────────────────
                IconBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xF0));
                IconText.Text         = "❌";
                TitleText.Text        = "所有任务失败";
                SubtitleText.Text     = $"共 {summary.Failed.Count} 个任务均未成功，请查看详情";
                FailCount.Foreground  = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F));
            }

            if (summary.HasFailures)
            {
                FailedList.ItemsSource = summary.Failed;
                FailedSection.Visibility = Visibility.Visible;
            }
        }

        private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = _outputPath,
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
