using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    /// <summary>True when the current group just failed (so UI can highlight it).</summary>
    public bool IsGroupError { get; init; }
}

/// <summary>
/// A single group-level failure recorded during the merge.
/// </summary>
public record FailedGroup(string GroupName, string ErrorMessage);

/// <summary>
/// Summary returned by <see cref="VideoMergeService.MergeAsync"/> after the whole batch completes.
/// </summary>
public class MergeSummary
{
    public int SucceededCount { get; init; }
    public int SkippedCount { get; init; }
    public IReadOnlyList<FailedGroup> Failed { get; init; } = [];
    public TimeSpan TotalElapsed { get; init; }

    public bool HasFailures => Failed.Count > 0;
    public bool IsFullSuccess => Failed.Count == 0 && SkippedCount == 0;
    public int TotalHandled => SucceededCount + SkippedCount + Failed.Count;
}

/// <summary>
/// Determines how to handle output files that already exist.
/// </summary>
public enum ConflictResolution
{
    /// <summary>Ask the user before starting.</summary>
    Ask,
    /// <summary>Skip groups whose output file already exists.</summary>
    Skip,
    /// <summary>Overwrite existing output files.</summary>
    Overwrite,
    /// <summary>Auto-rename the new output file (e.g. name_2.mp4).</summary>
    Rename
}

/// <summary>
/// Describes a single output file conflict detected in pre-flight.
/// </summary>
public record ConflictItem(string GroupName, string OutputFile);

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
                var rootName = Path.GetFileName(inputPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                subGroups.Insert(0, new MergeGroup
                {
                    Name = SafeFileName(rootName),
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
        var folderName = SafeFileName(Path.GetFileName(
            inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

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
    /// Sanitizes a string for use as a file/folder name by replacing illegal characters.
    /// </summary>
    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "output";

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "output" : safe;
    }

    /// <summary>
    /// Gets the default output path: a sibling directory with "_out" suffix.
    /// </summary>
    public static string GetDefaultOutputPath(string inputPath)
    {
        var trimmed = inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(inputPath);

        // Special-case drive/UNC roots: e.g. "D:\" → "D:\root_out"
        if (string.Equals(trimmed, root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            var driveLetter = trimmed.Replace(":", "").Replace("\\", "").Replace("/", "");
            var safeName = string.IsNullOrWhiteSpace(driveLetter) ? "root" : driveLetter;
            return Path.Combine(root!, safeName + "_out");
        }

        var parent = Path.GetDirectoryName(trimmed);
        var folderName = Path.GetFileName(trimmed);
        return Path.Combine(parent ?? trimmed, folderName + "_out");
    }

    /// <summary>
    /// Scans expected output paths and returns any that already exist (pre-flight check).
    /// </summary>
    public static List<ConflictItem> ScanConflicts(FolderAnalysis analysis, string outputPath)
    {
        var result = new List<ConflictItem>();
        foreach (var group in analysis.Groups)
        {
            var outputFile = Path.Combine(outputPath, group.Name + ".mp4");
            if (File.Exists(outputFile))
                result.Add(new ConflictItem(group.Name, outputFile));
        }
        return result;
    }

    /// <summary>
    /// Returns a non-conflicting path by appending _2, _3, … until free.
    /// </summary>
    private static string GetNonConflictingPath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var nameNoExt = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int n = 2;
        string candidate;
        do { candidate = Path.Combine(dir, $"{nameNoExt}_{n++}{ext}"); }
        while (File.Exists(candidate));
        return candidate;
    }

    /// <summary>
    /// Executes the merge operation for all groups in the analysis.
    /// </summary>
    public async Task<MergeSummary> MergeAsync(
        FolderAnalysis analysis,
        string outputPath,
        IProgress<MergeProgress> progress,
        ConflictResolution conflictResolution = ConflictResolution.Overwrite,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = FFmpegHelper.FindFFmpeg()
                     ?? throw new InvalidOperationException("FFmpeg 未找到，请先安装 FFmpeg");

        if (FFmpegHelper.FindFFprobe() == null)
            throw new InvalidOperationException("FFprobe 未找到，请确保 FFmpeg 完整安装（需包含 ffprobe）");

        var totalGroups = analysis.Groups.Count;
        var stopwatch = Stopwatch.StartNew();
        var sessionLog = new StringBuilder();
        var failedGroups = new List<FailedGroup>();
        int succeededCount = 0;
        int skippedCount = 0;

        Directory.CreateDirectory(outputPath);

        // Session log header
        var sessionStart = DateTime.Now;
        sessionLog.AppendLine($"ClipJoin 合并日志");
        sessionLog.AppendLine($"时间: {sessionStart:yyyy-MM-dd HH:mm:ss}");
        sessionLog.AppendLine($"输出目录: {outputPath}");
        sessionLog.AppendLine($"模式: {analysis.Mode}");
        sessionLog.AppendLine($"任务数: {totalGroups}");
        sessionLog.AppendLine($"FFmpeg: {ffmpeg}");
        sessionLog.AppendLine(new string('═', 60));

        for (int i = 0; i < totalGroups; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var group = analysis.Groups[i];
            var basePercent = (double)i / totalGroups * 100;

            // --- Detailed file order log ---
            sessionLog.AppendLine();
            sessionLog.AppendLine($"[任务 {i + 1}/{totalGroups}] {group.Name}");
            sessionLog.AppendLine($"  源目录: {group.SourceDir}");
            sessionLog.AppendLine($"  文件数: {group.VideoFiles.Count}");
            sessionLog.AppendLine($"  排序后文件列表 (自然排序):");
            for (int fi = 0; fi < group.VideoFiles.Count; fi++)
            {
                sessionLog.AppendLine($"    {fi + 1,4}. {Path.GetFileName(group.VideoFiles[fi])}");
            }

            // Report file order to UI log
            var orderLogLines = new StringBuilder();
            orderLogLines.AppendLine($"[{DateTime.Now:HH:mm:ss}] ─── 任务 {i + 1}/{totalGroups}: {group.Name} ───");
            orderLogLines.Append($"[{DateTime.Now:HH:mm:ss}] 文件顺序 ({group.VideoFiles.Count} 个):");
            for (int fi = 0; fi < group.VideoFiles.Count; fi++)
            {
                orderLogLines.Append($"\n[{DateTime.Now:HH:mm:ss}]   {fi + 1,3}. {Path.GetFileName(group.VideoFiles[fi])}");
            }

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
                LogMessage = orderLogLines.ToString()
            });

            var outputFile = Path.Combine(outputPath, group.Name + ".mp4");

            // ── Conflict resolution ────────────────────────────────────────
            if (File.Exists(outputFile))
            {
                switch (conflictResolution)
                {
                    case ConflictResolution.Skip:
                        sessionLog.AppendLine($"[任务 {i + 1}/{totalGroups}] {group.Name} — 跳过（文件已存在）");
                        progress.Report(new MergeProgress
                        {
                            StatusMessage = $"跳过: {group.Name}",
                            CurrentTask = $"文件已存在，跳过: {group.Name}.mp4",
                            CompletedFolders = i + 1,
                            TotalFolders = totalGroups,
                            FolderPercent = 100,
                            TotalPercent = (double)(i + 1) / totalGroups * 100,
                            Elapsed = stopwatch.Elapsed,
                            EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, i + 1, totalGroups),
                            LogMessage = $"[{DateTime.Now:HH:mm:ss}] ⏭ 跳过已存在: {group.Name}.mp4"
                        });
                        skippedCount++;
                        continue;

                    case ConflictResolution.Rename:
                        var renamedPath = GetNonConflictingPath(outputFile);
                        progress.Report(new MergeProgress
                        {
                            StatusMessage = $"重命名输出: {group.Name}",
                            CurrentTask = $"输出已存在，将写入: {Path.GetFileName(renamedPath)}",
                            CompletedFolders = i,
                            TotalFolders = totalGroups,
                            FolderPercent = 0,
                            TotalPercent = (double)i / totalGroups * 100,
                            Elapsed = stopwatch.Elapsed,
                            EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, i, totalGroups),
                            LogMessage = $"[{DateTime.Now:HH:mm:ss}] 📄 输出重命名: {group.Name}.mp4 → {Path.GetFileName(renamedPath)}"
                        });
                        sessionLog.AppendLine($"  输出重命名: {group.Name}.mp4 → {Path.GetFileName(renamedPath)}");
                        outputFile = renamedPath;
                        break;

                    // ConflictResolution.Overwrite: fall through, -y flag handles overwrite
                }
            }
            // ──────────────────────────────────────────────────────────────

            try
            {
            if (group.VideoFiles.Count == 1)
            {
                sessionLog.AppendLine($"  操作: 单文件复制");
                sessionLog.AppendLine($"  输出: {outputFile}");

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
                sessionLog.AppendLine($"  结果: 复制成功");
            }
            else if (group.VideoFiles.Count > 1)
            {
                await ConcatVideosAsync(ffmpeg, group, outputFile, i, totalGroups,
                    stopwatch, progress, sessionLog, cancellationToken);
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

            sessionLog.AppendLine($"  完成时间: {DateTime.Now:HH:mm:ss}");
            succeededCount++;
            }
            catch (OperationCanceledException)
            {
                // User cancelled — rethrow immediately, do not swallow
                throw;
            }
            catch (Exception groupEx)
            {
                failedGroups.Add(new FailedGroup(group.Name, groupEx.Message));
                sessionLog.AppendLine($"  结果: 失败 — {groupEx.Message}");

                progress.Report(new MergeProgress
                {
                    StatusMessage = $"失败: {group.Name}",
                    CurrentTask = $"任务失败，继续处理下一个...",
                    CompletedFolders = i + 1,
                    TotalFolders = totalGroups,
                    FolderPercent = 0,
                    TotalPercent = (double)(i + 1) / totalGroups * 100,
                    Elapsed = stopwatch.Elapsed,
                    EstimatedRemaining = EstimateRemaining(stopwatch.Elapsed, i + 1, totalGroups),
                    LogMessage = $"[{DateTime.Now:HH:mm:ss}] ❌ 任务失败: {group.Name} — {groupEx.Message}",
                    IsGroupError = true
                });
            }
        }

        stopwatch.Stop();

        var failedSummary = failedGroups.Count > 0
            ? $"\n失败任务 ({failedGroups.Count}):\n" + string.Join("\n", failedGroups.Select(f => $"  • {f.GroupName}: {f.ErrorMessage}"))
            : "";

        sessionLog.AppendLine();
        sessionLog.AppendLine(new string('═', 60));
        sessionLog.AppendLine($"全部完成，总耗时: {FormatTimeSpan(stopwatch.Elapsed)}");
        sessionLog.AppendLine($"成功: {succeededCount}，跳过: {skippedCount}，失败: {failedGroups.Count}");
        if (failedGroups.Count > 0)
        {
            sessionLog.AppendLine("失败任务:");
            foreach (var f in failedGroups)
                sessionLog.AppendLine($"  • {f.GroupName}: {f.ErrorMessage}");
        }

        // Save session log file to output directory
        SaveSessionLog(outputPath, sessionStart, sessionLog);

        var summaryIcon = failedGroups.Count == 0 ? "🎉" : failedGroups.Count < totalGroups ? "⚠️" : "❌";
        var summaryMsg = failedGroups.Count == 0
            ? $"全部完成！共处理 {totalGroups} 个任务"
            : $"完成 {succeededCount}，失败 {failedGroups.Count}，跳过 {skippedCount}；详情见日志";

        progress.Report(new MergeProgress
        {
            StatusMessage = $"{summaryIcon} 批处理完成",
            CurrentTask = summaryMsg,
            CompletedFolders = totalGroups,
            TotalFolders = totalGroups,
            FolderPercent = 100,
            TotalPercent = 100,
            Elapsed = stopwatch.Elapsed,
            EstimatedRemaining = TimeSpan.Zero,
            LogMessage = $"[{DateTime.Now:HH:mm:ss}] {summaryIcon} {summaryMsg}，总耗时: {FormatTimeSpan(stopwatch.Elapsed)}\n" +
                         $"[{DateTime.Now:HH:mm:ss}] 📄 日志已保存到输出目录"
        });

        return new MergeSummary
        {
            SucceededCount = succeededCount,
            SkippedCount = skippedCount,
            Failed = failedGroups,
            TotalElapsed = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Saves a detailed session log to the output directory for post-mortem debugging.
    /// </summary>
    private static void SaveSessionLog(string outputPath, DateTime sessionStart, StringBuilder sessionLog)
    {
        try
        {
            var logFileName = $"ClipJoin_log_{sessionStart:yyyyMMdd_HHmmss}.txt";
            var logFilePath = Path.Combine(outputPath, logFileName);
            File.WriteAllText(logFilePath, sessionLog.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging failure should never block the main workflow
        }
    }

    private async Task ConcatVideosAsync(
        string ffmpegPath,
        MergeGroup group,
        string outputFile,
        int groupIndex,
        int totalGroups,
        Stopwatch stopwatch,
        IProgress<MergeProgress> progress,
        StringBuilder sessionLog,
        CancellationToken cancellationToken)
    {
        var listFile = Path.Combine(Path.GetTempPath(), $"clipjoin_{Guid.NewGuid():N}.txt");

        try
        {
            // Write FFmpeg concat file list
            var lines = group.VideoFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'").ToList();
            await File.WriteAllLinesAsync(listFile, lines, cancellationToken);

            // Log concat list content to session log
            sessionLog.AppendLine($"  操作: FFmpeg concat 合并");
            sessionLog.AppendLine($"  concat 列表文件: {listFile}");
            sessionLog.AppendLine($"  concat 列表内容:");
            foreach (var line in lines)
            {
                sessionLog.AppendLine($"    {line}");
            }

            // Compute total duration for progress tracking
            double totalDuration = 0;
            foreach (var video in group.VideoFiles)
            {
                totalDuration += await FFmpegHelper.GetVideoDurationAsync(video, cancellationToken);
            }

            var ffmpegArgs = $"-hide_banner -nostdin -f concat -safe 0 -i \"{listFile}\" -c copy -movflags +faststart -y \"{outputFile}\"";

            // Log the full FFmpeg command
            sessionLog.AppendLine($"  输出文件: {outputFile}");
            sessionLog.AppendLine($"  总时长: {FormatTimeSpan(TimeSpan.FromSeconds(totalDuration))}");
            sessionLog.AppendLine($"  FFmpeg 命令: {ffmpegPath} {ffmpegArgs}");

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
                Arguments = ffmpegArgs,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 FFmpeg 进程");

            // Parse stderr for progress updates
            _ = Task.Run(async () =>
            {
                try
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
                }
                catch (OperationCanceledException) { /* expected on cancel */ }
                catch { /* ignore stderr parsing errors to avoid unobserved task exceptions */ }
            }, CancellationToken.None);

            try
            {
                await proc.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }

                try
                {
                    await proc.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException) { }

                // Delete the partial/corrupt output file left by the killed FFmpeg process
                try { if (File.Exists(outputFile)) File.Delete(outputFile); }
                catch { /* best-effort cleanup */ }

                throw;
            }

            if (proc.ExitCode != 0)
            {
                sessionLog.AppendLine($"  结果: 失败 (退出代码: {proc.ExitCode})");
                throw new InvalidOperationException(
                    $"FFmpeg 合并失败 (退出代码: {proc.ExitCode})，请确认视频文件格式一致");
            }

            sessionLog.AppendLine($"  结果: 成功");
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
/// Handles: leading zeros, very long numbers, mixed alpha-numeric segments,
/// and Unicode file names (Chinese, etc.).
/// </summary>
public class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            var cx = x[ix];
            var cy = y[iy];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                // Skip leading zeros, but count them for tie-breaking
                int zx = 0, zy = 0;
                while (ix < x.Length && x[ix] == '0') { zx++; ix++; }
                while (iy < y.Length && y[iy] == '0') { zy++; iy++; }

                // Extract numeric digits (without leading zeros)
                int startX = ix, startY = iy;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;

                int lenX = ix - startX;
                int lenY = iy - startY;

                // More digits (ignoring leading zeros) = larger number
                if (lenX != lenY) return lenX.CompareTo(lenY);

                // Same digit count: compare digit by digit
                for (int i = 0; i < lenX; i++)
                {
                    if (x[startX + i] != y[startY + i])
                        return x[startX + i].CompareTo(y[startY + i]);
                }

                // Identical numeric value: fewer leading zeros sorts first
                // e.g., "2" before "02" before "002"
                if (zx != zy) return zx.CompareTo(zy);
            }
            else
            {
                // Case-insensitive character comparison
                var ux = char.ToUpperInvariant(cx);
                var uy = char.ToUpperInvariant(cy);
                if (ux != uy) return ux.CompareTo(uy);
                ix++;
                iy++;
            }
        }

        // Shorter string comes first
        return (x.Length - ix).CompareTo(y.Length - iy);
    }
}
