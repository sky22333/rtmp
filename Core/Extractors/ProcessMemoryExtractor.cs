using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StreamCapturePro.Core.Models;
using StreamCapturePro.Core.Utils;

namespace StreamCapturePro.Core.Extractors
{
    public class ProcessMemoryExtractor : IStreamExtractor
    {
        private const int PollingDelayMs = 800;
        private const int MaxProcesses = 8;
        private const int ReadChunkSize = 1048576; // 增加到 1MB 以减少 API 调用并降低被截断概率
        private const int MaxRegionBytes = 16 * 1024 * 1024; // 最大扫描区域
        private static readonly Regex UrlPattern = new(@"rtmp://[^\s""'\x00\\]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        private static readonly byte[] RtmpUtf8 = Encoding.UTF8.GetBytes("rtmp://");

        private readonly ProcessScanOptionsService _optionsService;

        public string ExtractorName => "进程内存";

        public ProcessMemoryExtractor(ProcessScanOptionsService optionsService)
        {
            _optionsService = optionsService;
        }

        public async Task<StreamInfo?> ExtractAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var keywords = _optionsService.GetKeywords();
                if (keywords.Length == 0)
                {
                    await Task.Delay(PollingDelayMs, cancellationToken);
                    continue;
                }

                foreach (var process in GetCandidateProcesses(keywords))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var result = ScanProcess(process, cancellationToken);
                        if (result is { IsValid: true })
                        {
                            return result;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                await Task.Delay(PollingDelayMs, cancellationToken);
            }

            return null;
        }

        private static IReadOnlyList<Process> GetCandidateProcesses(string[] keywords)
        {
            var list = new List<(Process Process, DateTime StartTime)>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (!keywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    {
                        process.Dispose();
                        continue;
                    }

                    var startTime = process.StartTime;
                    list.Add((process, startTime));
                }
                catch
                {
                    process.Dispose();
                }
            }

            return list
                .OrderByDescending(item => item.StartTime)
                .Take(MaxProcesses)
                .Select(item => item.Process)
                .ToList();
        }

        private StreamInfo? ScanProcess(Process process, CancellationToken cancellationToken)
        {
            var handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessAccessFlags.QueryInformation | NativeMethods.ProcessAccessFlags.VmRead,
                false,
                process.Id);

            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                foreach (var region in EnumerateReadableRegions(handle, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = ScanRegion(handle, region, cancellationToken);
                    if (result is { IsValid: true })
                    {
                        result.Source = $"{ExtractorName} ({process.ProcessName})";
                        return result;
                    }
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }

            return null;
        }

        private static IEnumerable<(nuint BaseAddress, nuint RegionSize)> EnumerateReadableRegions(IntPtr processHandle, CancellationToken cancellationToken)
        {
            nuint address = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = NativeMethods.VirtualQueryEx(
                    processHandle,
                    (IntPtr)address,
                    out var info,
                    (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());

                if (result == 0)
                {
                    yield break;
                }

                var readable =
                    info.State == NativeMethods.MemoryState.Commit &&
                    (info.Protect & NativeMethods.MemoryProtection.Guard) == 0 &&
                    (info.Protect & NativeMethods.MemoryProtection.NoAccess) == 0 &&
                    (
                        (info.Protect & NativeMethods.MemoryProtection.ReadOnly) != 0 ||
                        (info.Protect & NativeMethods.MemoryProtection.ReadWrite) != 0 ||
                        (info.Protect & NativeMethods.MemoryProtection.WriteCopy) != 0 ||
                        (info.Protect & NativeMethods.MemoryProtection.ExecuteRead) != 0 ||
                        (info.Protect & NativeMethods.MemoryProtection.ExecuteReadWrite) != 0 ||
                        (info.Protect & NativeMethods.MemoryProtection.ExecuteWriteCopy) != 0
                    );

                if (readable && info.RegionSize > 0)
                {
                    yield return ((nuint)info.BaseAddress, info.RegionSize);
                }

                var next = (nuint)info.BaseAddress + info.RegionSize;
                if (next <= address)
                {
                    yield break;
                }

                address = next;
            }
        }

        private StreamInfo? ScanRegion(IntPtr processHandle, (nuint BaseAddress, nuint RegionSize) region, CancellationToken cancellationToken)
        {
            var total = (int)Math.Min((long)region.RegionSize, MaxRegionBytes);
            var offset = 0;
            const int OverlapSize = 1024;
            var buffer = ArrayPool<byte>.Shared.Rent(ReadChunkSize + OverlapSize);
            var currentOverlap = 0;

            try
            {
                while (offset < total)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var toRead = Math.Min(ReadChunkSize, total - offset);
                    var ok = NativeMethods.ReadProcessMemory(
                        processHandle,
                        (IntPtr)(region.BaseAddress + (nuint)offset),
                        ref buffer[currentOverlap],
                        (nuint)toRead,
                        out var bytesRead);

                    offset += toRead;
                    if (!ok || bytesRead == 0)
                    {
                        currentOverlap = 0;
                        continue;
                    }

                    var size = (int)bytesRead + currentOverlap;
                    if (size < 16)
                    {
                        currentOverlap = size;
                        continue;
                    }

                    if (TryExtractFromBuffer(buffer, size, out var server, out var key))
                    {
                        return new StreamInfo
                        {
                            Server = server,
                            Key = key
                        };
                    }

                    if (size > OverlapSize)
                    {
                        Array.Copy(buffer, size - OverlapSize, buffer, 0, OverlapSize);
                        currentOverlap = OverlapSize;
                    }
                    else
                    {
                        currentOverlap = size;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return null;
        }

        private static bool TryExtractFromBuffer(byte[] buffer, int bytesRead, out string server, out string key)
        {
            server = string.Empty;
            key = string.Empty;

            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, bytesRead);

            // 分配一个可复用的字符缓冲区，避免循环内频繁 stackalloc 或 Rent
            char[]? reusableCharBuffer = ArrayPool<char>.Shared.Rent(4096);
            Span<char> charSpan = reusableCharBuffer.AsSpan();

            try
            {
                // Search UTF8
                int offset = 0;
                while (offset < bytesRead)
                {
                    int index = span.Slice(offset).IndexOf(RtmpUtf8);
                    if (index < 0) break;

                    int absoluteIndex = offset + index;
                    int startIndex = Math.Max(0, absoluteIndex - 100);
                    int extractLength = Math.Min(1024, bytesRead - startIndex);
                    
                    var slice = span.Slice(startIndex, extractLength);
                    
                    int charCount = Encoding.UTF8.GetChars(slice, charSpan);
                    var textSpan = charSpan.Slice(0, charCount);

                    if (TryExtractFromTextSpan(textSpan, out server, out key))
                    {
                        return true;
                    }

                    offset = absoluteIndex + 1;
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(reusableCharBuffer);
            }

            return false;
        }

        private static bool TryExtractFromTextSpan(ReadOnlySpan<char> textSpan, out string server, out string key)
        {
            server = string.Empty;
            key = string.Empty;

            foreach (var match in UrlPattern.EnumerateMatches(textSpan))
            {
                var rawUrl = DecodeEscapedUrl(textSpan.Slice(match.Index, match.Length).ToString());
                if (TryBuildResultFromRtmpUrl(rawUrl, out server, out key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractFromText(string text, out string server, out string key)
        {
            server = string.Empty;
            key = string.Empty;

            var urlMatches = UrlPattern.Matches(text);
            for (var i = urlMatches.Count - 1; i >= 0; i--)
            {
                var rawUrl = DecodeEscapedUrl(urlMatches[i].Value);
                if (TryBuildResultFromRtmpUrl(rawUrl, out server, out key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryBuildResultFromRtmpUrl(string rawUrl, out string server, out string key)
        {
            server = string.Empty;
            key = string.Empty;

            if (string.IsNullOrWhiteSpace(rawUrl)) return false;

            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var path = uri.AbsolutePath;
            var markerIndex = path.IndexOf("/stream-", StringComparison.OrdinalIgnoreCase);
            
            // 处理形如 rtmp://host/stream-xxx 的情况，或者 rtmp://host/app/stream-xxx
            if (markerIndex < 0 && path.StartsWith("stream-", StringComparison.OrdinalIgnoreCase))
            {
                markerIndex = 0; // 极少见，但做个兜底
            }

            if (markerIndex >= 0)
            {
                // Key = 路径最后一部分 (包含 stream-) + 查询参数
                var embeddedKey = path.Substring(markerIndex + 1); // 加1是为了跳过前面的 '/'
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    embeddedKey += uri.Query;
                }

                if (IsValidPushKey(embeddedKey))
                {
                    key = embeddedKey;
                    // Server = Scheme://Host:Port/App
                    var serverPath = markerIndex > 0 ? path.Substring(0, markerIndex) : string.Empty;
                    server = $"{uri.Scheme}://{uri.Authority}{serverPath}";
                    return true;
                }
            }
            
            // 如果没有明显的 stream-，回退到通用解析 (使用 Uri 的段)
            return TryBuildGenericPushResult(uri, out server, out key);
        }

        private static bool TryBuildGenericPushResult(Uri uri, out string server, out string key)
        {
            server = string.Empty;
            key = string.Empty;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            var tail = segments[^1]; // 取最后一段
            var candidateKey = tail + uri.Query;
            
            if (!IsValidPushKey(candidateKey))
            {
                return false;
            }

            key = candidateKey;
            
            // Server = Scheme://Host:Port/App (去掉最后一段)
            var serverPath = "/" + string.Join('/', segments.Take(segments.Length - 1));
            // 确保 Server 以 / 结尾，这符合大多数推流软件（如 OBS）的习惯
            if (!serverPath.EndsWith("/"))
            {
                serverPath += "/";
            }
            server = $"{uri.Scheme}://{uri.Authority}{serverPath}";
            return true;
        }

        private static string DecodeEscapedUrl(string value)
        {
            return value.Replace("\\/", "/", StringComparison.Ordinal);
        }

        private static bool IsValidPushKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (key.Contains("/index.m3u8", StringComparison.OrdinalIgnoreCase)
                || key.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                || key.Contains(".flv", StringComparison.OrdinalIgnoreCase)
                || key.Contains("playlist", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static class NativeMethods
        {
            [Flags]
            public enum ProcessAccessFlags : uint
            {
                VmRead = 0x0010,
                QueryInformation = 0x0400
            }

            [Flags]
            public enum MemoryState : uint
            {
                Commit = 0x1000
            }

            [Flags]
            public enum MemoryProtection : uint
            {
                NoAccess = 0x01,
                ReadOnly = 0x02,
                ReadWrite = 0x04,
                WriteCopy = 0x08,
                ExecuteRead = 0x20,
                ExecuteReadWrite = 0x40,
                ExecuteWriteCopy = 0x80,
                Guard = 0x100
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MemoryBasicInformation
            {
                public IntPtr BaseAddress;
                public IntPtr AllocationBase;
                public MemoryProtection AllocationProtect;
                public nuint RegionSize;
                public MemoryState State;
                public MemoryProtection Protect;
                public uint Type;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(ProcessAccessFlags dwDesiredAccess, bool bInheritHandle, int dwProcessId);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool ReadProcessMemory(
                IntPtr hProcess,
                IntPtr lpBaseAddress,
                ref byte lpBuffer,
                nuint nSize,
                out nuint lpNumberOfBytesRead);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern nuint VirtualQueryEx(
                IntPtr hProcess,
                IntPtr lpAddress,
                out MemoryBasicInformation lpBuffer,
                nuint dwLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);
        }
    }
}
