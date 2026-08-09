using System;
using System.Linq;
using System.Net;

namespace StreamCapturePro.Core.Utils
{
    /// <summary>
    /// 直播伴侣内部接口返回的推流节点可能是内网专用域名（公网 DNS 无记录），
    /// 这里将其替换为公网可解析的同族节点。
    /// </summary>
    public static class PublicPushHostResolver
    {
        private const string InternalHost = "kspc.push.yximgs.com";

        private static readonly string[] PublicPrefixes = ["ali", "tx", "hw"];

        private static readonly Lazy<string> ResolvedHost = new(() =>
            PublicPrefixes
                .Select(prefix => $"{prefix}.push.yximgs.com")
                .FirstOrDefault(IsResolvable)
            ?? $"{PublicPrefixes[0]}.push.yximgs.com");

        public static string Resolve(string serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl)
                || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
                || !uri.Host.Equals(InternalHost, StringComparison.OrdinalIgnoreCase))
            {
                return serverUrl;
            }

            return new UriBuilder(uri) { Host = ResolvedHost.Value }.Uri.ToString();
        }

        private static bool IsResolvable(string host)
        {
            try
            {
                return Dns.GetHostAddresses(host).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
