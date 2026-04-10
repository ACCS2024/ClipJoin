using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClipJoin.Services;

/// <summary>
/// Progress information reported during the merge operation.
/// </summary>
public class MergeProgress
{
    public string StatusMessage { get; init; } = "";
    public string CurrentTask { get; init; } = "";
    public int CompletedFolders { get; init; }
    public int TotalFolders { get; init; }
    public double FolderPercent { get; init; }
    public double TotalPercent { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan EstimatedRemaining { get; init; }
    public string? LogMessage { get; init; }
}

/// <summary>
/// Indicates whether the input folder contains video files directly or in subdirectories.
/// </summary>
public enum FolderMode
{
    /// <summary>Video files are directly in the selected folder.</summary>
    Direct,
    /// <summary>Multiple subdirectories each contain video files.</summary>
    SubDirectory
}

/// <summary>
/// Result of analyzing the input folder structure.
/// </summary>
public class FolderAnalysis
{
    public FolderMode Mode { get; init; }
    public List<MergeGroup> Groups { get; init; } = [];
}

/// <summary>
/// A group of video files to be merged into a single output file.
/// </summary>
public class MergeGroup
{
    public string Name { get; init; } = "";
    public string SourceDir { get; init; } = "";
    public List<string> VideoFiles { get; init; } = [];
    public List<string> ImageFiles { get; init; } = [];
}

/// <summary>
/// Core service for analyzing folders and merging video files using FFmpeg.
/// </summary>
public class VideoMergeService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".ts", ".m4v", ".mpg", ".mpeg"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    /// <summary>
    /// Analyzes the input folder to determine the mode and discover merge groups.
    /// </summary>
    public FolderAnalysis AnalyzeFolder(string inputPath)
    {
        var directVideos = GetVideoFiles(inputPath);
        var directImages = GetImageFiles(inputPath);
        var subDirs = Directory.GetDirectories(inputPath);

        // Check subdirectories for video files
        var subGroups = new List<MergeGroup>();
        foreach (var subDir in subDirs.OrderBy(d => d, NaturalStringComparer.Instance))
        {
            var videos = GetVideoFiles(subDir);
            if (videos.Count > 0)
            {
                subGroups.Add(new MergeGroup
                {
                    Name = Path.GetFileName(subDir),
                    SourceDir = subDir,
                    VideoFiles = videos,
                    ImageFiles = GetImageFiles(subDir)
                });
            }
        }

        // If subdirectories contain videos, use subdirectory mode
        if (subGroups.Count > 0)
        {
            // Also include root-level videos as a group if any
            if (directVideos.Count > 0)
            {
                subGroups.Insert(0, new MergeGroup
                {
                    Name = Path.GetFileName(inputPath.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    SourceDir = inputPath,
                    VideoFiles = directVideos,
                    ImageFiles = directImages
                });
            }

            return new FolderAnalysis
            {
                Mode = FolderMode.SubDirectory,
                Groups = subGroups
            };
        }

        // Direct mode: video files in the folder itself
        var folderName = Path.GetFileName(
            inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return new FolderAnalysis
        {
            Mode = FolderMode.Direct,
            Groups =
            [
                new MergeGroup
                {
                    Name = folderName,
                    SourceDir = inputPath,
                    VideoFiles = directVideos,
                    ImageFiles = directImages
                }
            ]
        };
    }

    /// <summary>
    /// Gets the default output path: a sibling directory with "_out" suffix.
    /// </summary>
    public static string GetDefaultOutputPath(string inputPath)
    {
        var trimmed = inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var folderName = Path.GetFileName(trimmed);
        return Path.Combine(parent ?? trimmed, folderName + "_out");
    }

    /// <summary>
    /// Executes the merge operation for all groups in the analysis.
    /// </summary>
    public async Task MergeAsync(
        FolderAnalysis analysis,
        string outputPath,
        IProgress<MergeProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = FFmpegHelper.FindFFmpeg()
                     ?? throw new InvalidOperationException("FFmpeg 未找到，请先安装 FFmpeg");

        var totalGroups = analysis.Groups.Count;
        var stopwatch = Stopwatch.StartNew();

        Directory.CreateDirectory(outputPath);

        for (int i = 0; i < totalGroups; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var group = analysis.Groups[i];
            var basePercent = (double)i / totalGroups * 100;

            progress.Report(new MergeProgress
            {
                StatusMessage = $"正在合并: {group.Name}",
                CurrentTask = $"正在处理第 {i + 1}/{totalGroups} 个任务: {group.Name}",
                CompletedFolders = i,
                TotalFolders = totalGroups,
                FolderPercent = 0,
                TotalPercent = basePercent,
                Elapsed = stopwatch.Elapsed,
                EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, i, totalGroups),
                LogMessage = $"[{DateTime.Now:HH:mm:ss}] 开始合并: {group.Name} ({group.VideoFiles.Count} 个视频文件)"
            });

            var outputFile = Path.Combine(outputPath, group.Name + ".mp4");

            if (group.VideoFiles.Count == 1)
            {
                progress.Report(new MergeProgress
                {
                    StatusMessage = $"正在复制: {group.Name}",
                    CurrentTask = $"单文件复制: {Path.GetFileName(group.VideoFiles[0])}",
                    CompletedFolders = i,
                    TotalFolders = totalGroups,
                    FolderPercent = 50,
                    TotalPercent = basePercent + 50.0 / totalGroups,
                    Elapsed = stopwatch.Elapsed,
                    EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, i + 0.5, totalGroups),
                    LogMessage = $"[{DateTime.Now:HH:mm:ss}] 单文件模式，直接复制: {Path.GetFileName(group.VideoFiles[0])}"
                });

                File.Copy(group.VideoFiles[0], outputFile, true);
            }
            else if (group.VideoFiles.Count > 1)
            {
                await ConcatVideosAsync(ffmpeg, group, outputFile, i, totalGroups,
                    stopwatch, progress, cancellationToken);
            }

            // Copy images from source to output
            CopyImages(group, outputPath, analysis.Mode);

            progress.Report(new MergeProgress
            {
                StatusMessage = $"已完成: {group.Name}",
                CurrentTask = $"已完成第 {i + 1}/{totalGroups} 个任务",
                CompletedFolders = i + 1,
                TotalFolders = totalGroups,
                FolderPercent = 100,
                TotalPercent = (double)(i + 1) / totalGroups * 100,
                Elapsed = stopwatch.Elapsed,
                EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, i + 1, totalGroups),
                LogMessage = $"[{DateTime.Now:HH:mm:ss}] ✅ 完成合并: {group.Name} → {Path.GetFileName(outputFile)}"
            });
        }

        stopwatch.Stop();

        progress.Report(new MergeProgress
        {
            StatusMessage = "全部任务完成！",
            CurrentTask = $"已完成全部 {totalGroups} 个任务",
            CompletedFolders = totalGroups,
            TotalFolders = totalGroups,
            FolderPercent = 100,
            TotalPercent = 100,
            Elapsed = stopwatch.Elapsed,
            EstimatedRemaining = TimeSpan.Zero,
            LogMessage = $"[{DateTime.Now:HH:mm:ss}] 🎉 全部完成！共处理 {totalGroups} 个任务，总耗时: {FormatTimeSpan(stopwatch.Elapsed)}"
        });
    }

    private async Task ConcatVideosAsync(
        string ffmpegPath,
        MergeGroup group,
        string outputFile,
        int groupIndex,
        int totalGroups,
        Stopwatch stopwatch,
        IProgress<MergeProgress> progress,
        CancellationToken cancellationToken)
    {
        var listFile = Path.Combine(Path.GetTempPath(), $"clipjoin_{Guid.NewGuid():N}.txt");

        try
        {
            // Write FFmpeg concat file list
            var lines = group.VideoFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(listFile, lines, cancellationToken);

            // Compute total duration for progress tracking
            double totalDuration = 0;
            foreach (var video in group.VideoFiles)
            {
                totalDuration += await FFmpegHelper.GetVideoDurationAsync(video, cancellationToken);
            }

            progress.Report(new MergeProgress
            {
                StatusMessage = $"正在合并: {group.Name}",
                CurrentTask = $"总时长: {FormatTimeSpan(TimeSpan.FromSeconds(totalDuration))}，正在拼接...",
                CompletedFolders = groupIndex,
                TotalFolders = totalGroups,
                FolderPercent = 0,
                TotalPercent = (double)groupIndex / totalGroups * 100,
                Elapsed = stopwatch.Elapsed,
                EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, groupIndex, totalGroups),
                LogMessage = $"[{DateTime.Now:HH:mm:ss}] 合并 {group.VideoFiles.Count} 个文件，总时长 {FormatTimeSpan(TimeSpan.FromSeconds(totalDuration))}"
            });

            // Run FFmpeg with concat demuxer (no re-encoding)
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-f concat -safe 0 -i \"{listFile}\" -c copy -y \"{outputFile}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 FFmpeg 进程");

            // Parse stderr for progress updates
            _ = Task.Run(async () =>
            {
                using var reader = proc.StandardError;
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    var timeIdx = line.IndexOf("time=", StringComparison.Ordinal);
                    if (timeIdx >= 0 && totalDuration > 0)
                    {
                        var timeStr = line[(timeIdx + 5)..].Split(' ')[0];
                        if (TryParseFFmpegTime(timeStr, out var currentTime))
                        {
                            var folderPercent = Math.Min(100, currentTime / totalDuration * 100);
                            var totalPercent = (groupIndex + folderPercent / 100.0) / totalGroups * 100;

                            progress.Report(new MergeProgress
                            {
                                StatusMessage = $"正在合并: {group.Name}",
                                CurrentTask = $"合并进度: {FormatTimeSpan(TimeSpan.FromSeconds(currentTime))} / {FormatTimeSpan(TimeSpan.FromSeconds(totalDuration))}",
                                CompletedFolders = groupIndex,
                                TotalFolders = totalGroups,
                                FolderPercent = folderPercent,
                                TotalPercent = totalPercent,
                                Elapsed = stopwatch.Elapsed,
                                EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, groupIndex + folderPercent / 100.0, totalGroups)
                            });
                        }
                    }
                }
            }, cancellationToken);

            await proc.WaitForExitAsync(cancellationToken);

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg 合并失败 (退出代码: {proc.ExitCode})，请确认视频文件格式一致");
            }
        }
        finally
        {
            if (File.Exists(listFile))
            {
                try { File.Delete(listFile); }
                catch { /* ignored */ }
            }
        }
    }

    /// <summary>
    /// Copies image files from the source group to the output directory.
    /// In subdirectory mode, images are prefixed with the group name to avoid conflicts.
    /// </summary>
    private static void CopyImages(MergeGroup group, string outputPath, FolderMode mode)
    {
        foreach (var imageFile in group.ImageFiles)
        {
            var fileName = Path.GetFileName(imageFile);
            var destName = mode == FolderMode.SubDirectory
                ? $"{group.Name}_{fileName}"
                : fileName;

            var destPath = Path.Combine(outputPath, destName);

            // Handle name collisions
            if (File.Exists(destPath))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(destName);
                var ext = Path.GetExtension(destName);
                int counter = 1;
                do
                {
                    destPath = Path.Combine(outputPath, $"{nameWithoutExt}_{counter}{ext}");
                    counter++;
                } while (File.Exists(destPath));
            }

            File.Copy(imageFile, destPath, false);
        }
    }

    private static List<string> GetVideoFiles(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return [];

        return Directory.GetFiles(dirPath)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, NaturalStringComparer.Instance)
            .ToList();
    }

    private static List<string> GetImageFiles(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return [];

        return Directory.GetFiles(dirPath)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, NaturalStringComparer.Instance)
            .ToList();
    }

    private static bool TryParseFFmpegTime(string timeStr, out double seconds)
    {
        seconds = 0;
        var parts = timeStr.Split(':');
        if (parts.Length != 3) return false;

        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            seconds = h * 3600 + m * 60 + s;
            return true;
        }

        return false;
    }

    private static TimeSpan EstimateRemaining(TimeSpan elapsed, double completedUnits, int totalUnits)
    {
        if (completedUnits <= 0 || totalUnits <= 0) return TimeSpan.Zero;

        var rate = elapsed.TotalSeconds / completedUnits;
        var remaining = rate * (totalUnits - completedUnits);
        return TimeSpan.FromSeconds(Math.Max(0, remaining));
    }

    /// <summary>
    /// Formats a TimeSpan as HH:MM:SS or MM:SS.
    /// </summary>
    public static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}

/// <summary>
/// Natural string comparer that sorts numeric portions of strings numerically.
/// For example: "1.mp4", "2.mp4", "10.mp4" instead of "1.mp4", "10.mp4", "2.mp4".
/// </summary>
public class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                // Extract and compare numeric portions
                long nx = 0;
                while (ix < x.Length && char.IsDigit(x[ix]))
                    nx = nx * 10 + (x[ix++] - '0');

                long ny = 0;
                while (iy < y.Length && char.IsDigit(y[iy]))
                    ny = ny * 10 + (y[iy++] - '0');

                if (nx != ny) return nx.CompareTo(ny);
            }
            else
            {
                var cx = char.ToUpperInvariant(x[ix]);
                var cy = char.ToUpperInvariant(y[iy]);
                if (cx != cy) return cx.CompareTo(cy);
                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
