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

    // Official build recommended by https://ffmpeg.org/download.html#build-windows (BtbN)
    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    // Official build recommended by https://ffmpeg.org/download.html#build-windows (gyan.dev)
    // FFmpeg 4.4 – compatible with Windows < 10 build 14393 (no SetThreadDescription call)
    private const string LegacyDownloadUrl =
        "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-4.4.1-essentials_build.zip";

    /// <summary>
    /// Returns true when the current OS is too old to support SetThreadDescription
    /// (requires Windows 10 build 14393 / Server 2016 with update).
    /// Modern FFmpeg builds call this API on startup and crash on older systems.
    /// </summary>
    public static bool NeedsLegacyBuild()
    {
        var ver = Environment.OSVersion.Version;
        // Major < 10  →  Win 7 / 8 / 8.1 / Server 2012
        // Major == 10, Build < 14393  →  Win 10 TH1/TH2 (rare), Server 2016 RTM without update
        return ver.Major < 10 || (ver.Major == 10 && ver.Build < 14393);
    }

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
    /// Automatically selects a legacy build for Windows Server 2012 / Windows &lt; 10 build 14393.
    /// </summary>
    public static async Task DownloadAndInstallAsync(
        IProgress<(string message, double percent)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(FFmpegDir);

        var zipPath = Path.Combine(AppDataDir, "ffmpeg-download.zip");

        var useLegacy = NeedsLegacyBuild();
        var url = useLegacy ? LegacyDownloadUrl : DownloadUrl;

        try
        {
            progress?.Report((useLegacy
                ? "检测到旧版系统，正在下载兼容版 FFmpeg 4.4..."
                : "正在下载 FFmpeg...", 0));

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);

            using var response = await httpClient.GetAsync(url,
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

            string? ffmpegSrc;
            string? ffprobeSrc;

            if (binDir != null)
            {
                // BtbN layout: <root>/<name>/bin/ffmpeg.exe
                ffmpegSrc  = Path.Combine(binDir, "ffmpeg.exe");
                ffprobeSrc = Path.Combine(binDir, "ffprobe.exe");
            }
            else
            {
                // gyan.dev layout: <root>/<name>/ffmpeg.exe  (no bin sub-dir)
                ffmpegSrc = Directory.GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                ffprobeSrc = Directory.GetFiles(extractDir, "ffprobe.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }

            if (ffmpegSrc != null && File.Exists(ffmpegSrc))
                File.Copy(ffmpegSrc, FFmpegExePath, true);
            if (ffprobeSrc != null && File.Exists(ffprobeSrc))
                File.Copy(ffprobeSrc, FFprobeExePath, true);

            // Validate both executables were installed
            if (!File.Exists(FFmpegExePath) || !File.Exists(FFprobeExePath))
                throw new InvalidOperationException(
                    "FFmpeg 安装不完整：" +
                    (!File.Exists(FFmpegExePath) ? "缺少 ffmpeg.exe " : "") +
                    (!File.Exists(FFprobeExePath) ? "缺少 ffprobe.exe" : ""));

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);

            // Add FFmpegDir to the user's PATH environment variable
            AddToUserPath(FFmpegDir);

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
    /// Gets key audio stream parameters (sample_rate, channels, codec_name) via FFprobe.
    /// Returns null if the file has no audio stream.
    /// </summary>
    public static async Task<AudioStreamInfo?> GetAudioInfoAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var ffprobe = FindFFprobe() ?? FFprobeExePath;

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = $"-v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels -of csv=p=0 \"{filePath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var output = (await proc.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
        await proc.WaitForExitAsync(cancellationToken);

        // Expected format: codec_name,sample_rate,channels  e.g. "aac,48000,2"
        var parts = output.Split(',');
        if (parts.Length < 3) return null;

        return new AudioStreamInfo(
            CodecName: parts[0].Trim(),
            SampleRate: parts[1].Trim(),
            Channels: parts[2].Trim());
    }

    /// <summary>
    /// Returns true when all files in <paramref name="filePaths"/> share the same audio
    /// codec, sample-rate and channel count. Files without an audio stream are ignored.
    /// </summary>
    public static async Task<bool> AudioStreamsCompatibleAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        AudioStreamInfo? reference = null;
        foreach (var path in filePaths)
        {
            var info = await GetAudioInfoAsync(path, cancellationToken);
            if (info == null) continue;          // no audio – skip
            if (reference == null) { reference = info; continue; }
            if (info != reference) return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the highest audio sample rate found across all files.
    /// Used to set a uniform <c>-ar</c> target when re-encoding mixed-rate audio.
    /// Falls back to 48000 when no audio streams are found.
    /// </summary>
    public static async Task<int> GetMaxSampleRateAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        int max = 44100;
        foreach (var path in filePaths)
        {
            var info = await GetAudioInfoAsync(path, cancellationToken);
            if (info == null) continue;
            if (int.TryParse(info.SampleRate, out var rate) && rate > max)
                max = rate;
        }
        return max;
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

    /// <summary>
    /// Adds <paramref name="directory"/> to the current user's PATH environment variable
    /// (HKCU) if it is not already present. This does not require administrator privileges.
    /// </summary>
    private static void AddToUserPath(string directory)
    {
        const string key = "PATH";
        var current = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User) ?? string.Empty;

        // Normalise for comparison
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(e => string.Equals(e.Trim(), directory, StringComparison.OrdinalIgnoreCase)))
            return; // already present

        var updated = current.TrimEnd(';') + ";" + directory;
        Environment.SetEnvironmentVariable(key, updated, EnvironmentVariableTarget.User);

        // Also update the current process so ffmpeg is usable immediately
        var processPath = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process) ?? string.Empty;
        if (!processPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Any(e => string.Equals(e.Trim(), directory, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.SetEnvironmentVariable(key,
                processPath.TrimEnd(';') + ";" + directory,
                EnvironmentVariableTarget.Process);
        }
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

/// <summary>
/// Audio stream parameters used to detect cross-segment incompatibilities.
/// </summary>
public record AudioStreamInfo(string CodecName, string SampleRate, string Channels);
