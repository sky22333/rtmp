using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StreamCapturePro.Core.Models;

namespace StreamCapturePro.Core.Extractors
{
    public class DouyinLogExtractor : IStreamExtractor
    {
        public string ExtractorName => "Douyin Log Tailing";
        private const int TailReadBytes = 1048576; // 1024 KB
        private const int PollingDelayMs = 1000;
        private const int MaxScanFiles = 5;
        
        private static readonly Regex UrlPattern = new(
            @"""url""\s*:\s*""(?<url>rtmp://[^""]+)""", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex KeyPattern = new(
            @"""key""\s*:\s*""(?<key>[^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TimestampPattern = new(
            @"""timestamp""\s*:\s*""?(?<ts>\d+)""?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // %APPDATA%\webcast_mate\logs
        private readonly string[] _logDirectories =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "webcast_mate", "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "webcast_mate", "logs")
        ];

        private readonly Utils.ProcessScanOptionsService _optionsService;

        public DouyinLogExtractor(Utils.ProcessScanOptionsService optionsService)
        {
            _optionsService = optionsService;
        }

        public async Task<StreamInfo?> ExtractAsync(CancellationToken cancellationToken)
        {
            if (!IsDouyinProcessTargeted())
            {
                return null;
            }

            if (!_logDirectories.Any(Directory.Exists))
            {
                throw new DirectoryNotFoundException("未找到直播伴侣日志目录，请确认是否已安装抖音直播伴侣。");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var logFile in GetRecentLogFiles())
                {
                    var result = await ParseLogFileAsync(logFile, cancellationToken);
                    if (result != null && result.IsValid)
                    {
                        return result;
                    }
                }

                await Task.Delay(PollingDelayMs, cancellationToken);
            }

            return null;
        }

        private bool IsDouyinProcessTargeted()
        {
            var keywords = _optionsService.GetKeywords();
            if (keywords.Length == 0) return false;

            foreach (var kw in keywords)
            {
                if (kw.Contains("直播伴侣", StringComparison.OrdinalIgnoreCase) ||
                    kw.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
                    kw.Contains("MediaSDK_Server", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private string[] GetRecentLogFiles()
        {
            var now = DateTime.UtcNow;
            return _logDirectories
                .Where(Directory.Exists)
                .SelectMany(directory => new DirectoryInfo(directory).EnumerateFiles())
                .Where(f => IsCandidateLogName(f.Name))
                .Where(f => f.Length > 0)
                .Where(f => (now - f.LastWriteTimeUtc).TotalMinutes <= 5)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxScanFiles)
                .Select(f => f.FullName)
                .ToArray();
        }

        private async Task<StreamInfo?> ParseLogFileAsync(string filePath, CancellationToken token)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                if (fs.Length > TailReadBytes)
                {
                    fs.Seek(-TailReadBytes, SeekOrigin.End);
                }

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string content = await reader.ReadToEndAsync(token);

                if (TryExtractFromStartStreamSuccess(content, out var server, out var key))
                {
                    return new StreamInfo
                    {
                        Server = server,
                        Key = key,
                        Source = ExtractorName
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            return null;
        }

        private static bool IsCandidateLogName(string fileName)
        {
            if (fileName.EndsWith("-client.txt", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && fileName.Contains("client", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && fileName.Contains('-', StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool TryExtractFromStartStreamSuccess(string content, out string server, out string key)
        {
            server = string.Empty;
            key = string.Empty;

            var anchorIndex = content.LastIndexOf("[startStream]success", StringComparison.OrdinalIgnoreCase);
            if (anchorIndex < 0) return false;

            int length = Math.Min(content.Length - anchorIndex, 4000);
            string contextWindow = content.Substring(anchorIndex, length);

            contextWindow = contextWindow.Replace("\\", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");

            var urlMatch = UrlPattern.Match(contextWindow);
            var keyMatch = KeyPattern.Match(contextWindow);
            var tsMatch = TimestampPattern.Match(contextWindow);

            if (urlMatch.Success && keyMatch.Success && tsMatch.Success)
            {
                if (long.TryParse(tsMatch.Groups["ts"].Value, out long logTimestamp))
                {
                    if (logTimestamp > 9999999999) logTimestamp /= 1000;
                    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    if (Math.Abs(nowUnix - logTimestamp) <= 60)
                    {
                        var rawUrl = urlMatch.Groups["url"].Value;
                        var rawKey = keyMatch.Groups["key"].Value;

                        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
                        {
                            var path = uri.AbsolutePath;
                            var markerIndex = path.IndexOf("/stream-", StringComparison.OrdinalIgnoreCase);
                            if (markerIndex >= 0)
                            {
                                var embeddedKey = path[(markerIndex + 1)..];
                                if (!string.IsNullOrEmpty(uri.Query)) embeddedKey += uri.Query;
                                
                                if (!embeddedKey.Contains(".m3u8") && !embeddedKey.Contains(".flv"))
                                {
                                    key = embeddedKey;
                                    var serverPath = markerIndex > 0 ? path[..markerIndex] : string.Empty;
                                    server = $"{uri.Scheme}://{uri.Authority}{serverPath}";
                                    return true;
                                }
                            }
                        }

                        server = rawUrl;
                        key = rawKey;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
