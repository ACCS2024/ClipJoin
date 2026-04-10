using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
        private readonly AiSortService _aiSortService = new();
        private string _lastOutputPath = "";

        // Cached analysis for sort operations (set when user clicks Start or sort buttons)
        private FolderAnalysis? _currentAnalysis;
        private FolderAnalysisWrapper? _sortedAnalysis;

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

            // Use AI/manual sorted result if available, otherwise use natural sort
            FolderAnalysis effectiveAnalysis;
            if (_sortedAnalysis != null)
            {
                effectiveAnalysis = _sortedAnalysis.ToAnalysis();
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ℹ️ 使用自定义排序结果");
            }
            else
            {
                effectiveAnalysis = analysis;
            }

            if (effectiveAnalysis.Groups.Count == 0 ||
                effectiveAnalysis.Groups.All(g => g.VideoFiles.Count == 0))
            {
                MessageBox.Show(
                    "未在指定文件夹中找到视频文件\n\n" +
                    "支持的格式: MP4, MKV, AVI, MOV, WMV, FLV, TS, M4V",
                    "未找到视频",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Notify user about subdirectory mode
            if (effectiveAnalysis.Mode == FolderMode.SubDirectory)
            {
                var dlg = new SubdirConfirmDialog(
                    effectiveAnalysis.Groups.Count,
                    effectiveAnalysis.Groups.Select(g => (g.Name, g.VideoFiles.Count)))
                {
                    Owner = this
                };
                dlg.ShowDialog();
                if (!dlg.Confirmed)
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

            // ── Pre-flight: check for output file conflicts ─────────────────────────
            var resolution = Services.ConflictResolution.Overwrite;
            var conflicts = Services.VideoMergeService.ScanConflicts(effectiveAnalysis, outputPath);
            if (conflicts.Count > 0)
            {
                var settings = AppSettings.Load();
                if (settings.DefaultConflictResolution != Services.ConflictResolution.Ask)
                {
                    // Use remembered preference — no dialog needed
                    resolution = settings.DefaultConflictResolution;
                    var label = resolution switch
                    {
                        Services.ConflictResolution.Skip => "跳过",
                        Services.ConflictResolution.Rename => "自动重命名",
                        _ => "覆盖"
                    };
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 发现 {conflicts.Count} 个已存在的输出文件，将按记住的设置执行：{label}");
                }
                else
                {
                    var dlg = new ConflictDialog(conflicts) { Owner = this };
                    dlg.ShowDialog();
                    if (!dlg.Confirmed)
                        return;

                    resolution = dlg.SelectedResolution;

                    if (dlg.RememberChoice)
                    {
                        settings.DefaultConflictResolution = resolution;
                        settings.Save();
                    }
                }
            }
            // ───────────────────────────────────────────────────────────────────────

            // Prepare UI
            _cts = new CancellationTokenSource();
            SetUIBusy(true);
            ProgressSection.Visibility = Visibility.Visible;
            OpenOutputBtn.Visibility = Visibility.Collapsed;
            LogTextBox.Document.Blocks.Clear();
            LogPlaceholder.Visibility = Visibility.Collapsed;

            AppendLog($"输入目录: {inputPath}");
            AppendLog($"输出目录: {outputPath}");
            AppendLog($"模式: {(effectiveAnalysis.Mode == FolderMode.SubDirectory ? "子目录模式" : "直接模式")}");
            AppendLog($"共 {effectiveAnalysis.Groups.Count} 个合并任务，" +
                      $"{effectiveAnalysis.Groups.Sum(g => g.VideoFiles.Count)} 个视频文件");
            AppendLog(new string('─', 50));

            try
            {
                var progress = new Progress<MergeProgress>(UpdateProgress);
                var summary = await _mergeService.MergeAsync(effectiveAnalysis, outputPath, progress, resolution, _cts.Token);

                // Clear sorted analysis after successful merge
                _sortedAnalysis = null;

                OpenOutputBtn.Visibility = Visibility.Visible;
                var resultDlg = new MergeResultDialog(summary, outputPath) { Owner = this };
                resultDlg.ShowDialog();
            }
            catch (OperationCanceledException)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠️ 用户取消了操作");
                StatusText.Text = "已取消";
                FooterText.Text = "已取消";
            }
            catch (Exception ex)
            {
                AppendLogError($"[{DateTime.Now:HH:mm:ss}] ❌ 错误: {ex.Message}");
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
                if (p.IsGroupError)
                    AppendLogError(p.LogMessage);
                else
                    AppendLog(p.LogMessage);
            }
        }

        private void AppendLog(string message)
        {
            LogPlaceholder.Visibility = Visibility.Collapsed;
            var para = new Paragraph(new Run(message))
            {
                Margin = new Thickness(0),
                LineHeight = 1.4 * LogTextBox.FontSize
            };
            LogTextBox.Document.Blocks.Add(para);
            LogTextBox.ScrollToEnd();
        }

        /// <summary>Appends a log line in red to highlight failures.</summary>
        private void AppendLogError(string message)
        {
            LogPlaceholder.Visibility = Visibility.Collapsed;
            var para = new Paragraph(new Run(message))
            {
                Margin      = new Thickness(0),
                LineHeight  = 1.4 * LogTextBox.FontSize,
                Foreground  = Brushes.Crimson
            };
            LogTextBox.Document.Blocks.Add(para);
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
            AiSortBtn.IsEnabled = !busy;
            ManualSortBtn.IsEnabled = !busy;
        }

        // ────── AI Sort ──────

        private FolderAnalysis? TryGetAnalysis()
        {
            var inputPath = InputPathTextBox.Text.Trim();
            if (string.IsNullOrEmpty(inputPath) || !Directory.Exists(inputPath))
            {
                MessageBox.Show("请先选择有效的输入文件夹", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            _currentAnalysis = _mergeService.AnalyzeFolder(inputPath);
            if (_currentAnalysis.Groups.Count == 0 ||
                _currentAnalysis.Groups.All(g => g.VideoFiles.Count == 0))
            {
                MessageBox.Show("未找到视频文件", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            return _currentAnalysis;
        }

        private async void AiSort_Click(object sender, RoutedEventArgs e)
        {
            var settings = AppSettings.Load();
            if (!settings.IsConfigured)
            {
                var result = MessageBox.Show(
                    "尚未配置 AI 接口，是否现在配置？", "AI 未配置",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                    Settings_Click(sender, e);
                return;
            }

            var analysis = TryGetAnalysis();
            if (analysis == null) return;

            AiSortBtn.IsEnabled = false;
            AiSortBtn.Content = "🤖 排序中...";
            LogPlaceholder.Visibility = Visibility.Collapsed;
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 🤖 开始 AI 智能排序...");

            var wrapper = FolderAnalysisWrapper.FromAnalysis(analysis);
            var success = true;

            try
            {
                foreach (var group in wrapper.Groups)
                {
                    if (group.VideoFiles.Count <= 1) continue;

                    AppendLog($"[{DateTime.Now:HH:mm:ss}]   排序: {group.Name} ({group.VideoFiles.Count} 个文件)");

                    try
                    {
                        group.VideoFiles = await _aiSortService.SortFilesAsync(
                            group.VideoFiles, settings);

                        AppendLog($"[{DateTime.Now:HH:mm:ss}]   ✅ {group.Name} 排序完成");
                        for (int i = 0; i < group.VideoFiles.Count; i++)
                            AppendLog($"[{DateTime.Now:HH:mm:ss}]     {i + 1,3}. {Path.GetFileName(group.VideoFiles[i])}");
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        AppendLog($"[{DateTime.Now:HH:mm:ss}]   ❌ {group.Name} 排序失败: {ex.Message}");
                    }
                }

                _sortedAnalysis = wrapper;

                if (success)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] 🎉 AI 排序全部完成！下次合并将使用 AI 排序结果");
                    MessageBox.Show(
                        "AI 排序完成！文件已按智能顺序排列。\n点击「开始合并」将使用新的排序。",
                        "排序完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠️ 部分任务排序失败，可查看日志或导出错误日志");
                    MessageBox.Show(
                        "部分文件夹排序失败，失败的将保持原始顺序。\n详情请查看日志或导出错误日志。",
                        "排序部分完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ AI 排序异常: {ex.Message}");
                MessageBox.Show($"AI 排序失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                AiSortBtn.IsEnabled = true;
                AiSortBtn.Content = "🤖 AI 排序";
            }
        }

        // ────── Manual Sort ──────

        private void ManualSort_Click(object sender, RoutedEventArgs e)
        {
            var analysis = TryGetAnalysis();
            if (analysis == null) return;

            var wrapper = _sortedAnalysis ?? FolderAnalysisWrapper.FromAnalysis(analysis);
            var sortWindow = new ManualSortWindow(wrapper) { Owner = this };

            if (sortWindow.ShowDialog() == true && sortWindow.Result != null)
            {
                _sortedAnalysis = sortWindow.Result;
                LogPlaceholder.Visibility = Visibility.Collapsed;
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✋ 手动排序已确认，下次合并将使用自定义顺序");
                foreach (var g in _sortedAnalysis.Groups)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}]   {g.Name}:");
                    for (int i = 0; i < g.VideoFiles.Count; i++)
                        AppendLog($"[{DateTime.Now:HH:mm:ss}]     {i + 1,3}. {Path.GetFileName(g.VideoFiles[i])}");
                }
            }
        }

        // ────── Settings ──────

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow { Owner = this };
            win.ShowDialog();
        }

        // ────── Export Log ──────

        private void ExportLog_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Title = "导出 AI 错误日志",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"ClipJoin_AI_Errors_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    AiSortService.ExportLogs(sfd.FileName);
                    MessageBox.Show($"日志已导出到:\n{sfd.FileName}", "导出成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}