using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClipJoin.Services;

/// <summary>
/// Handles FFmpeg detection, automatic download, and installation.
/// </summary>
public static class FFmpegHelper
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipJoin");

    private static readonly string FFmpegDir = Path.Combine(AppDataDir, "ffmpeg");

    public static string FFmpegExePath => Path.Combine(FFmpegDir, "ffmpeg.exe");
    public static string FFprobeExePath => Path.Combine(FFmpegDir, "ffprobe.exe");

    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    /// <summary>
    /// Finds the FFmpeg executable. Checks local app directory first, then system PATH.
    /// Returns the full path or the command name if found in PATH; null otherwise.
    /// </summary>
    public static string? FindFFmpeg()
    {
        if (File.Exists(FFmpegExePath))
            return FFmpegExePath;

        if (IsInPath("ffmpeg"))
            return "ffmpeg";

        return null;
    }

    /// <summary>
    /// Finds the FFprobe executable. Checks local app directory first, then system PATH.
    /// </summary>
    public static string? FindFFprobe()
    {
        if (File.Exists(FFprobeExePath))
            return FFprobeExePath;

        if (IsInPath("ffprobe"))
            return "ffprobe";

        return null;
    }

    /// <summary>
    /// Returns true if both FFmpeg and FFprobe are available.
    /// </summary>
    public static bool IsAvailable() => FindFFmpeg() != null && FindFFprobe() != null;

    /// <summary>
    /// Downloads and installs FFmpeg to the local app data directory.
    /// </summary>
    public static async Task DownloadAndInstallAsync(
        IProgress<(string message, double percent)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(FFmpegDir);

        var zipPath = Path.Combine(AppDataDir, "ffmpeg-download.zip");

        try
        {
            progress?.Report(("正在下载 FFmpeg...", 0));

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);

            using var response = await httpClient.GetAsync(DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(zipPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    var percent = (double)totalRead / totalBytes * 80;
                    var totalMB = totalBytes / 1024.0 / 1024.0;
                    var readMB = totalRead / 1024.0 / 1024.0;
                    progress?.Report((
                        $"正在下载 FFmpeg... ({readMB:F1}MB / {totalMB:F1}MB)",
                        percent));
                }
            }

            fileStream.Close();

            progress?.Report(("正在解压 FFmpeg...", 80));

            var extractDir = Path.Combine(AppDataDir, "ffmpeg-extract");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);

            ZipFile.ExtractToDirectory(zipPath, extractDir);

            progress?.Report(("正在安装 FFmpeg...", 90));

            var binDir = Directory.GetDirectories(extractDir, "bin", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (binDir != null)
            {
                var ffmpegSrc = Path.Combine(binDir, "ffmpeg.exe");
                var ffprobeSrc = Path.Combine(binDir, "ffprobe.exe");

                if (File.Exists(ffmpegSrc))
                    File.Copy(ffmpegSrc, FFmpegExePath, true);
                if (File.Exists(ffprobeSrc))
                    File.Copy(ffprobeSrc, FFprobeExePath, true);
            }
            else
            {
                throw new InvalidOperationException("解压后未找到 FFmpeg 可执行文件");
            }

            // Validate both executables were installed
            if (!File.Exists(FFmpegExePath) || !File.Exists(FFprobeExePath))
                throw new InvalidOperationException(
                    "FFmpeg 安装不完整：" +
                    (!File.Exists(FFmpegExePath) ? "缺少 ffmpeg.exe " : "") +
                    (!File.Exists(FFprobeExePath) ? "缺少 ffprobe.exe" : ""));

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);

            progress?.Report(("FFmpeg 安装完成！", 100));
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); }
                catch { /* ignored */ }
            }
        }
    }

    /// <summary>
    /// Gets the duration of a media file in seconds using FFprobe.
    /// </summary>
    public static async Task<double> GetVideoDurationAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var ffprobe = FindFFprobe() ?? FFprobeExePath;

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return 0;

        var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        if (double.TryParse(output.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var duration))
            return duration;

        return 0;
    }

    private static bool IsInPath(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = "-version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(5000);
                return proc.ExitCode == 0;
            }
        }
        catch
        {
            /* not found in PATH */
        }

        return false;
    }
}
