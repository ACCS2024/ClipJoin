using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ClipJoin.Services;

namespace ClipJoin
{
    public partial class SettingsWindow : Window
    {
        private bool _keyVisible;
        private CancellationTokenSource? _redeployCts;

        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
            UpdateFFmpegPathLabel();
        }

        private void UpdateFFmpegPathLabel()
        {
            var path = FFmpegHelper.FindFFmpeg();
            FFmpegPathText.Text = path != null
                ? $"当前路径：{path}"
                : "未检测到 FFmpeg，请重新部署";
        }

        private void LoadSettings()
        {
            var settings = AppSettings.Load();
            EndpointTextBox.Text = settings.ApiEndpoint;
            ApiKeyPasswordBox.Password = settings.ApiKey;
            ApiKeyTextBox.Text = settings.ApiKey;
            ModelTextBox.Text = settings.Model;

            switch (settings.DefaultConflictResolution)
            {
                case ConflictResolution.Skip:      ConflictSkipRadio.IsChecked      = true; break;
                case ConflictResolution.Overwrite: ConflictOverwriteRadio.IsChecked = true; break;
                case ConflictResolution.Rename:    ConflictRenameRadio.IsChecked    = true; break;
                default:                           ConflictAskRadio.IsChecked       = true; break;
            }
        }

        private string GetCurrentApiKey()
        {
            return _keyVisible ? ApiKeyTextBox.Text.Trim() : ApiKeyPasswordBox.Password.Trim();
        }

        private void ToggleKeyVisibility_Click(object sender, RoutedEventArgs e)
        {
            _keyVisible = !_keyVisible;
            if (_keyVisible)
            {
                ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
                ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
                ApiKeyTextBox.Visibility = Visibility.Visible;
                ToggleKeyBtn.Content = "🙈";
            }
            else
            {
                ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
                ApiKeyTextBox.Visibility = Visibility.Collapsed;
                ApiKeyPasswordBox.Visibility = Visibility.Visible;
                ToggleKeyBtn.Content = "👁";
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            var endpoint = EndpointTextBox.Text.Trim();
            var apiKey = GetCurrentApiKey();
            var model = ModelTextBox.Text.Trim();

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
            {
                ShowStatus("请先填写 API 端点和 API Key", false);
                return;
            }

            TestBtn.IsEnabled = false;
            TestBtn.Content = "测试中...";
            ShowStatus("正在测试连接...", true);

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                var requestBody = new
                {
                    model = string.IsNullOrEmpty(model) ? "deepseek-v3" : model,
                    messages = new[]
                    {
                        new { role = "user", content = "回复OK" }
                    },
                    max_tokens = 10
                };

                var json = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Authorization", $"Bearer {apiKey}");

                using var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    ShowStatus("✅ 连接成功！API 可正常使用", true);
                }
                else
                {
                    ShowStatus($"❌ API 返回 {(int)response.StatusCode}，请检查配置", false);
                }
            }
            catch (TaskCanceledException)
            {
                ShowStatus("❌ 连接超时，请检查网络或端点地址", false);
            }
            catch (Exception ex)
            {
                ShowStatus($"❌ {ex.Message}", false);
            }
            finally
            {
                TestBtn.IsEnabled = true;
                TestBtn.Content = "测试连接";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var endpoint = EndpointTextBox.Text.Trim();
            var apiKey = GetCurrentApiKey();
            var model = ModelTextBox.Text.Trim();

            if (string.IsNullOrEmpty(endpoint))
            {
                ShowStatus("API 端点不能为空", false);
                return;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                ShowStatus("API Key 不能为空", false);
                return;
            }

            var resolution = ConflictSkipRadio.IsChecked      == true ? ConflictResolution.Skip
                           : ConflictOverwriteRadio.IsChecked == true ? ConflictResolution.Overwrite
                           : ConflictRenameRadio.IsChecked    == true ? ConflictResolution.Rename
                           : ConflictResolution.Ask;

            var settings = new AppSettings
            {
                ApiEndpoint = endpoint,
                ApiKey = apiKey,
                Model = string.IsNullOrEmpty(model) ? "deepseek-v3" : model,
                DefaultConflictResolution = resolution
            };

            try
            {
                settings.Save();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowStatus($"保存失败: {ex.Message}", false);
            }
        }

        private async void RedeployFFmpeg_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "将删除本地 FFmpeg 并重新下载安装，过程中无法进行合并操作。\n\n确定继续？",
                "重新部署 FFmpeg",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK) return;

            RedeployFFmpegBtn.IsEnabled = false;
            RedeployProgress.Visibility = Visibility.Visible;
            RedeployStatusText.Visibility = Visibility.Visible;
            RedeployProgress.Value = 0;
            RedeployStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xD4, 0x6B, 0x08));

            _redeployCts = new CancellationTokenSource();

            try
            {
                // Delete existing ffmpeg directory
                var ffmpegDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClipJoin", "ffmpeg");

                if (Directory.Exists(ffmpegDir))
                {
                    RedeployStatusText.Text = "正在删除旧版本...";
                    await System.Threading.Tasks.Task.Run(() => Directory.Delete(ffmpegDir, true));
                }

                var progress = new Progress<(string message, double percent)>(report =>
                {
                    RedeployStatusText.Text = report.message;
                    RedeployProgress.Value = report.percent;
                });

                await FFmpegHelper.DownloadAndInstallAsync(progress, _redeployCts.Token);

                RedeployProgress.Value = 100;
                RedeployStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x52, 0xC4, 0x1A));
                RedeployStatusText.Text = "✓ FFmpeg 部署成功！";
                UpdateFFmpegPathLabel();
            }
            catch (OperationCanceledException)
            {
                RedeployStatusText.Text = "已取消";
            }
            catch (Exception ex)
            {
                RedeployStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x4F));
                RedeployStatusText.Text = $"部署失败：{ex.Message}";
            }
            finally
            {
                RedeployFFmpegBtn.IsEnabled = true;
                _redeployCts = null;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _redeployCts?.Cancel();
            DialogResult = false;
            Close();
        }

        private void ShowStatus(string message, bool success)
        {
            StatusText.Text = message;
            StatusText.Foreground = success
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x52, 0xC4, 0x1A))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x4F));
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
