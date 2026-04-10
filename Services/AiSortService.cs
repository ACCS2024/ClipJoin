using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ClipJoin.Services;

/// <summary>
/// AI-powered file sorting service using OpenAI-compatible chat completion APIs.
/// </summary>
public class AiSortService
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipJoin", "logs");

    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    /// <summary>
    /// Uses AI to sort video filenames into correct playback order.
    /// Returns the sorted list of full paths.
    /// </summary>
    public async Task<List<string>> SortFilesAsync(
        List<string> filePaths,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (filePaths.Count <= 1)
            return [.. filePaths];

        if (!settings.IsConfigured)
            throw new InvalidOperationException("请先在设置中配置 API Key 和 API 端点");

        // Build a map: filename -> full path
        var nameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new List<string>();
        foreach (var fp in filePaths)
        {
            var name = Path.GetFileName(fp);
            nameToPath[name] = fp;
            fileNames.Add(name);
        }

        var prompt = BuildSortPrompt(fileNames);

        string aiResponse;
        try
        {
            aiResponse = await CallChatCompletionAsync(settings, prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            LogError("AI_SORT_API_CALL", ex, fileNames);
            throw new InvalidOperationException($"AI 接口调用失败: {ex.Message}", ex);
        }

        try
        {
            var sortedNames = ParseSortResponse(aiResponse, fileNames);

            // Rebuild full paths in sorted order
            var result = new List<string>();
            foreach (var name in sortedNames)
            {
                if (nameToPath.TryGetValue(name, out var path))
                    result.Add(path);
            }

            // If AI missed some files, append them at the end
            var missing = filePaths.Where(fp => !result.Contains(fp)).ToList();
            result.AddRange(missing);

            return result;
        }
        catch (Exception ex)
        {
            LogError("AI_SORT_PARSE", ex, fileNames, aiResponse);
            throw new InvalidOperationException($"AI 返回结果解析失败: {ex.Message}", ex);
        }
    }

    private static string BuildSortPrompt(List<string> fileNames)
    {
        var fileList = string.Join("\n", fileNames.Select((n, i) => $"{i + 1}. {n}"));

        return $"""
你是一个视频文件排序专家，专门处理中国短剧、电视剧、综艺的视频文件排序。

## 任务
将以下视频文件名按**正确的播放顺序**从第一集到最后一集排列。

## 排序规则（优先级从高到低）
1. **明确集数编号**：识别并按集数升序排列
   - 纯数字前缀：`01_xxx`, `002-xxx`, `03 xxx`
   - 集数词：`第1集`, `第01集`, `EP01`, `E01`, `S01E01`
   - 括号内集数：`(01)xxx`, `[02]xxx`
2. **日期型命名**：`20260101`, `2026-01-01`, `0101` → 按日期升序
3. **分段标识**：`上/中/下`, `前/后`, `Part1/Part2`, `A/B` → 按逻辑顺序
4. **纯数字部分**：提取文件名中最显著的数字段，按数值升序（注意：10 > 9）
5. **中文数字**：一=1, 二=2, 三=3, 四=4, 五=5, 六=6, 七=7, 八=8, 九=9, 十=10
6. **无明显顺序**：保持原始顺序

## 注意事项
- 忽略剧名、分辨率（1080p/4K）、编码（H264/HEVC）、清晰度（蓝光/超清）等无关信息
- 前导零不影响数值比较：`001` = `1`, `02` = `2`
- 识别系列名后再排序，避免被剧名中的数字干扰（如"三生三世"中的"三"不是集数）
- 文件扩展名不参与排序判断

## 示例
输入：`["第10集.mp4", "第2集.mp4", "第1集.mp4"]`
输出：`["第1集.mp4", "第2集.mp4", "第10集.mp4"]`

输入：`["EP03_超清.mp4", "EP01_蓝光.mp4", "EP02_1080p.mp4"]`
输出：`["EP01_蓝光.mp4", "EP02_1080p.mp4", "EP03_超清.mp4"]`

## 待排序文件列表
{fileList}

## 输出格式
严格按照以下 JSON 格式返回排序后的文件名数组，**不添加任何解释文字、不使用 markdown 代码块**：
["文件名1", "文件名2", "文件名3"]

**约束**：
- 返回的文件名必须与输入列表中的文件名完全一致（含扩展名），不能修改
- 必须包含全部 {fileNames.Count} 个文件，不能遗漏
- 只返回纯 JSON 数组
""";
    }

    private static List<string> ParseSortResponse(string response, List<string> originalNames)
    {
        // Try to extract JSON array from response (handle markdown code blocks)
        var json = response.Trim();

        // Strip markdown code block
        if (json.Contains("```"))
        {
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start >= 0 && end > start)
                json = json[start..(end + 1)];
        }

        // Ensure it starts with [ and ends with ]
        var arrStart = json.IndexOf('[');
        var arrEnd = json.LastIndexOf(']');
        if (arrStart >= 0 && arrEnd > arrStart)
            json = json[arrStart..(arrEnd + 1)];

        var sorted = JsonSerializer.Deserialize<List<string>>(json)
                     ?? throw new InvalidOperationException("AI 返回了空结果");

        // Validate all names exist
        var originalSet = new HashSet<string>(originalNames, StringComparer.OrdinalIgnoreCase);
        var validSorted = sorted.Where(n => originalSet.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (validSorted.Count == 0)
            throw new InvalidOperationException("AI 返回的文件名与原始文件名不匹配");

        return validSorted;
    }

    private async Task<string> CallChatCompletionAsync(
        AppSettings settings,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = settings.Model,
            messages = new[]
            {
                new { role = "system", content = "你是一个视频文件排序专家，只输出纯 JSON 数组，不添加任何说明文字、不使用 markdown 代码块。" },
                new { role = "user", content = userMessage }
            },
            temperature = 0.0
        };

        var jsonContent = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiEndpoint)
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {settings.ApiKey}");

        using var response = await SharedClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"API 返回错误 {(int)response.StatusCode}: {TruncateForLog(responseBody, 500)}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? throw new InvalidOperationException("AI 返回了空的 content");
    }

    /// <summary>
    /// Logs errors to a file for later export/diagnosis.
    /// </summary>
    public static void LogError(string context, Exception ex, List<string>? fileNames = null, string? aiResponse = null)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var logFile = Path.Combine(LogDir, $"ai_error_{DateTime.Now:yyyyMMdd}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === {context} ===");
            sb.AppendLine($"  Error: {ex.Message}");
            if (ex.InnerException != null)
                sb.AppendLine($"  Inner: {ex.InnerException.Message}");
            if (fileNames != null)
            {
                sb.AppendLine($"  Files ({fileNames.Count}):");
                foreach (var f in fileNames)
                    sb.AppendLine($"    - {f}");
            }
            if (aiResponse != null)
                sb.AppendLine($"  AI Response: {TruncateForLog(aiResponse, 2000)}");
            sb.AppendLine();

            File.AppendAllText(logFile, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never throw
        }
    }

    /// <summary>
    /// Returns the path to the error log directory.
    /// </summary>
    public static string GetLogDirectory() => LogDir;

    /// <summary>
    /// Exports all error logs to a specified file.
    /// </summary>
    public static void ExportLogs(string destinationPath)
    {
        if (!Directory.Exists(LogDir))
        {
            File.WriteAllText(destinationPath, "暂无错误日志。\n", Encoding.UTF8);
            return;
        }

        var logFiles = Directory.GetFiles(LogDir, "ai_error_*.log")
            .OrderByDescending(f => f)
            .ToList();

        if (logFiles.Count == 0)
        {
            File.WriteAllText(destinationPath, "暂无错误日志。\n", Encoding.UTF8);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("ClipJoin AI 错误日志导出");
        sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('═', 60));
        sb.AppendLine();

        foreach (var logFile in logFiles)
        {
            sb.AppendLine($"--- {Path.GetFileName(logFile)} ---");
            sb.AppendLine(File.ReadAllText(logFile));
            sb.AppendLine();
        }

        File.WriteAllText(destinationPath, sb.ToString(), Encoding.UTF8);
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "...(truncated)";
    }
}
