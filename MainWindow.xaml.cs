using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ClipJoin.Services;
using Microsoft.Win32;

namespace ClipJoin
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;
        private readonly VideoMergeService _mergeService = new();
        private string _lastOutputPath = "";

        public MainWindow()
        {
            InitializeComponent();
            CheckFFmpegStatus();
        }

        /// <summary>
        /// Checks FFmpeg availability and updates the UI status indicators.
        /// </summary>
        private void CheckFFmpegStatus()
        {
            if (FFmpegHelper.IsAvailable())
            {
                FFmpegOkBorder.Visibility = Visibility.Visible;
                FFmpegWarningBorder.Visibility = Visibility.Collapsed;
                StartBtn.IsEnabled = true;
            }
            else
            {
                FFmpegOkBorder.Visibility = Visibility.Collapsed;
                FFmpegWarningBorder.Visibility = Visibility.Visible;
                StartBtn.IsEnabled = false;
            }
        }

        /// <summary>
        /// Auto-fills the output path based on the input path.
        /// Called whenever the input path changes (typing, browse, or drag-drop).
        /// </summary>
        private void AutoFillOutputPath(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
                return;
            OutputPathTextBox.Text = VideoMergeService.GetDefaultOutputPath(inputPath);
        }

        private void InputPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            AutoFillOutputPath(InputPathTextBox.Text.Trim());
        }

        /// <summary>
        /// Handles drag-over on the input TextBox to accept folders and files.
        /// </summary>
        private void InputPath_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// Handles drop on the input TextBox. Accepts folders directly,
        /// and for video files uses their parent directory.
        /// </summary>
        private void InputPath_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0)
                return;

            var first = paths[0];
            if (Directory.Exists(first))
            {
                InputPathTextBox.Text = first;
            }
            else if (File.Exists(first))
            {
                var parentDir = Path.GetDirectoryName(first);
                if (!string.IsNullOrEmpty(parentDir))
                    InputPathTextBox.Text = parentDir;
            }

            e.Handled = true;
        }

        private void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择包含视频文件的文件夹"
            };

            if (dialog.ShowDialog() == true)
            {
                InputPathTextBox.Text = dialog.FolderName;
            }
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择输出文件夹（留空则自动生成）"
            };

            if (dialog.ShowDialog() == true)
            {
                OutputPathTextBox.Text = dialog.FolderName;
            }
        }

        private async void InstallFFmpeg_Click(object sender, RoutedEventArgs e)
        {
            InstallFFmpegBtn.IsEnabled = false;
            FFmpegStatusText.Text = "正在下载安装 FFmpeg，请稍候...";
            ProgressSection.Visibility = Visibility.Visible;

            try
            {
                var progress = new Progress<(string message, double percent)>(p =>
                {
                    FFmpegStatusText.Text = p.message;
                    TotalProgressBar.Value = p.percent;
                    StatusText.Text = p.message;
                    TotalPercentText.Text = $"{p.percent:F0}%";
                });

                await FFmpegHelper.DownloadAndInstallAsync(progress);
                CheckFFmpegStatus();
                AppendLog("✅ FFmpeg 安装成功！");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"FFmpeg 安装失败:\n{ex.Message}", "安装失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                FFmpegStatusText.Text = "FFmpeg 安装失败，请重试";
                InstallFFmpegBtn.IsEnabled = true;
            }
            finally
            {
                ProgressSection.Visibility = Visibility.Collapsed;
                TotalProgressBar.Value = 0;
                TotalPercentText.Text = "0%";
            }
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            var inputPath = InputPathTextBox.Text.Trim();

            if (string.IsNullOrEmpty(inputPath))
            {
                MessageBox.Show("请选择输入文件夹", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(inputPath))
            {
                MessageBox.Show("输入文件夹不存在，请检查路径是否正确", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Analyze the folder structure
            var analysis = _mergeService.AnalyzeFolder(inputPath);

            if (analysis.Groups.Count == 0 ||
                analysis.Groups.All(g => g.VideoFiles.Count == 0))
            {
                MessageBox.Show(
                    "未在指定文件夹中找到视频文件\n\n" +
                    "支持的格式: MP4, MKV, AVI, MOV, WMV, FLV, TS, M4V",
                    "未找到视频",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Notify user about subdirectory mode
            if (analysis.Mode == FolderMode.SubDirectory)
            {
                var groupList = string.Join("\n",
                    analysis.Groups.Select(g => $"  📁 {g.Name}  ({g.VideoFiles.Count} 个视频)"));

                var result = MessageBox.Show(
                    $"📂 检测到子目录模式\n\n" +
                    $"共发现 {analysis.Groups.Count} 个包含视频的文件夹：\n{groupList}\n\n" +
                    $"每个文件夹中的视频将被分别合并为独立的 MP4 文件，\n" +
                    $"所有输出文件将放在同一输出目录下（不创建子文件夹）。\n\n" +
                    $"是否继续？",
                    "子目录模式确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // Determine output path
            var outputPath = OutputPathTextBox.Text.Trim();
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = VideoMergeService.GetDefaultOutputPath(inputPath);
            }

            // Validate: input and output must not be the same directory
            try
            {
                var normalizedInput = Path.GetFullPath(inputPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedOutput = Path.GetFullPath(outputPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(normalizedInput, normalizedOutput, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("输入文件夹和输出文件夹不能相同，否则可能覆盖源文件", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Prevent output inside input (would cause recursive processing)
                if (normalizedOutput.StartsWith(normalizedInput + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("输出文件夹不能位于输入文件夹内部，否则可能导致递归处理", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"路径无效: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _lastOutputPath = outputPath;

            // Prepare UI
            _cts = new CancellationTokenSource();
            SetUIBusy(true);
            ProgressSection.Visibility = Visibility.Visible;
            OpenOutputBtn.Visibility = Visibility.Collapsed;
            LogTextBox.Clear();
            LogPlaceholder.Visibility = Visibility.Collapsed;

            AppendLog($"输入目录: {inputPath}");
            AppendLog($"输出目录: {outputPath}");
            AppendLog($"模式: {(analysis.Mode == FolderMode.SubDirectory ? "子目录模式" : "直接模式")}");
            AppendLog($"共 {analysis.Groups.Count} 个合并任务，" +
                      $"{analysis.Groups.Sum(g => g.VideoFiles.Count)} 个视频文件");
            AppendLog(new string('─', 50));

            try
            {
                var progress = new Progress<MergeProgress>(UpdateProgress);
                await _mergeService.MergeAsync(analysis, outputPath, progress, _cts.Token);

                OpenOutputBtn.Visibility = Visibility.Visible;
                MessageBox.Show(
                    $"🎉 全部合并完成！\n\n输出目录: {outputPath}",
                    "完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠️ 用户取消了操作");
                StatusText.Text = "已取消";
                FooterText.Text = "已取消";
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ 错误: {ex.Message}");
                MessageBox.Show($"合并过程中出现错误:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUIBusy(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                CancelBtn.IsEnabled = false;
                StatusText.Text = "正在取消...";
            }
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(_lastOutputPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastOutputPath,
                    UseShellExecute = true
                });
            }
        }

        /// <summary>
        /// Handles drag-over events to show drop feedback.
        /// Accepts both folders and files (files will use their parent directory).
        /// </summary>
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// Handles drop onto the window. Accepts folders directly,
        /// and for files uses their parent directory as the input path.
        /// </summary>
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0)
                return;

            var first = paths[0];
            if (Directory.Exists(first))
            {
                InputPathTextBox.Text = first;
            }
            else if (File.Exists(first))
            {
                var parentDir = Path.GetDirectoryName(first);
                if (!string.IsNullOrEmpty(parentDir))
                    InputPathTextBox.Text = parentDir;
            }
        }

        /// <summary>
        /// Updates all progress indicators from a MergeProgress object.
        /// </summary>
        private void UpdateProgress(MergeProgress p)
        {
            StatusText.Text = p.StatusMessage;
            CurrentTaskText.Text = p.CurrentTask;
            FolderProgressBar.Value = p.FolderPercent;
            FolderPercentText.Text = $"{p.FolderPercent:F0}%";
            TotalProgressBar.Value = p.TotalPercent;
            TotalPercentText.Text = $"{p.TotalPercent:F0}%";
            ElapsedTimeText.Text = $"已用时: {VideoMergeService.FormatTimeSpan(p.Elapsed)}";
            RemainingTimeText.Text = p.EstimatedRemaining > TimeSpan.Zero
                ? $"预计剩余: {VideoMergeService.FormatTimeSpan(p.EstimatedRemaining)}"
                : "预计剩余: --";
            FooterText.Text = $"进度: {p.CompletedFolders}/{p.TotalFolders} 个任务 | {p.TotalPercent:F1}%";

            if (!string.IsNullOrEmpty(p.LogMessage))
            {
                AppendLog(p.LogMessage);
            }
        }

        private void AppendLog(string message)
        {
            LogPlaceholder.Visibility = Visibility.Collapsed;
            LogTextBox.AppendText(message + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        }

        /// <summary>
        /// Toggles UI elements between busy and idle states.
        /// </summary>
        private void SetUIBusy(bool busy)
        {
            StartBtn.IsEnabled = !busy && FFmpegHelper.IsAvailable();
            CancelBtn.IsEnabled = busy;
            InputPathTextBox.IsEnabled = !busy;
            OutputPathTextBox.IsEnabled = !busy;
        }
    }
}