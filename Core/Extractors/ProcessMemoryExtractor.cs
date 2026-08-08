using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        private const int MaxCandidates = 32;
        private static readonly Regex UrlPattern = new(@"rtmp://[^\s""'\x00\\]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AuthParamPattern = new(
            @"[?&](sign|sig|token|auth|auth_key|timestamp|expire|expires|secret|nonce|t|k)=",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly byte[] RtmpUtf8 = Encoding.UTF8.GetBytes("rtmp://");

        private readonly ProcessScanOptionsService _optionsService;

        public string ExtractorName => "进程内存";

        public ProcessMemoryExtractor(ProcessScanOptionsService optionsService)
        {
            _optionsService = optionsService;
        }

        public async Task<StreamInfo?> ExtractAsync(CancellationToken cancellationToken)
        {
            var preferAuthCandidates = IsKwaiLiveTargeted();

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
                        var result = ScanProcess(process, preferAuthCandidates, cancellationToken);
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

        private StreamInfo? ScanProcess(Process process, bool preferAuthCandidates, CancellationToken cancellationToken)
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
                var candidates = new List<StreamInfo>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var region in EnumerateWritableRegions(handle, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CollectRegion(handle, region, seen, candidates, !preferAuthCandidates, cancellationToken);

                    if (!preferAuthCandidates && candidates.Count > 0)
                    {
                        break;
                    }
                    if (candidates.Count >= MaxCandidates)
                    {
                        break;
                    }
                }

                var best = preferAuthCandidates
                    ? candidates.FirstOrDefault(candidate => HasAuthParams(candidate.Key)) ?? candidates.FirstOrDefault()
                    : candidates.FirstOrDefault();

                if (best is not null)
                {
                    best.Source = $"{ExtractorName} ({process.ProcessName})";
                }
                return best;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        private static IEnumerable<(nuint BaseAddress, nuint RegionSize)> EnumerateWritableRegions(IntPtr processHandle, CancellationToken cancellationToken)
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

                var writable =
                    info.State == NativeMethods.MemoryState.Commit &&
                    (info.Protect & NativeMethods.MemoryProtection.Guard) == 0 &&
                    (info.Protect & (NativeMethods.MemoryProtection.ReadWrite | NativeMethods.MemoryProtection.WriteCopy | NativeMethods.MemoryProtection.ExecuteReadWrite | NativeMethods.MemoryProtection.ExecuteWriteCopy)) != 0;

                if (writable && info.RegionSize > 0)
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

        private void CollectRegion(IntPtr processHandle, (nuint BaseAddress, nuint RegionSize) region, HashSet<string> seen, List<StreamInfo> candidates, bool stopAfterFirst, CancellationToken cancellationToken)
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

                    CollectFromBuffer(buffer, size, seen, candidates, stopAfterFirst);
                    if (stopAfterFirst && candidates.Count > 0)
                    {
                        break;
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
        }

        private void CollectFromBuffer(byte[] buffer, int bytesRead, HashSet<string> seen, List<StreamInfo> candidates, bool stopAfterFirst)
        {
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

                    CollectFromTextSpan(textSpan, seen, candidates, stopAfterFirst);
                    if (stopAfterFirst && candidates.Count > 0)
                    {
                        break;
                    }

                    offset = absoluteIndex + 1;
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(reusableCharBuffer);
            }
        }

        private void CollectFromTextSpan(ReadOnlySpan<char> textSpan, HashSet<string> seen, List<StreamInfo> candidates, bool stopAfterFirst)
        {
            foreach (var match in UrlPattern.EnumerateMatches(textSpan))
            {
                var rawUrl = DecodeEscapedUrl(textSpan.Slice(match.Index, match.Length).ToString());
                if (TryBuildResultFromRtmpUrl(rawUrl, out var server, out var key))
                {
                    if (seen.Add($"{server}|{key}"))
                    {
                        candidates.Add(new StreamInfo { Server = server, Key = key });
                        if (stopAfterFirst)
                        {
                            return;
                        }
                    }
                }
            }
        }

        private bool IsKwaiLiveTargeted()
        {
            var keywords = _optionsService.GetKeywords();
            return keywords.Any(keyword =>
                keyword.Contains("kwailive", StringComparison.OrdinalIgnoreCase) ||
                keyword.Contains("kuaishou", StringComparison.OrdinalIgnoreCase) ||
                keyword.Contains("快手", StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasAuthParams(string key)
            => AuthParamPattern.IsMatch(key);

        private static bool TryBuildResultFromRtmpUrl(string rawUrl, out string server, out string key)
        {
            server = string.Empty;
            key = string.Empty;

            if (string.IsNullOrWhiteSpace(rawUrl)) return false;

            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!IsPublicHost(uri.Host))
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
            if (segments.Length < 2)
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

        private static bool IsPublicHost(string host)
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                return !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
            }
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            return !IPAddress.IsLoopback(ip) && !IsPrivateOrReservedIp(ip);
        }

        private static bool IsPrivateOrReservedIp(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 0
                    || b[0] == 10
                    || b[0] >= 224
                    || (b[0] == 172 && b[1] is >= 16 and <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] is >= 64 and <= 127);
            }

            var v6 = ip.GetAddressBytes();
            return v6[0] == 0xfc || v6[0] == 0xfd || (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80);
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
