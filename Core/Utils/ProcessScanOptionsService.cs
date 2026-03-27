using System;
using System.Linq;

namespace StreamCapturePro.Core.Utils
{
    public class ProcessScanOptionsService
    {
        private readonly object _syncRoot = new();
        private string _keywordsText = "直播伴侣";

        public string KeywordsText
        {
            get
            {
                lock (_syncRoot)
                {
                    return _keywordsText;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    _keywordsText = value ?? string.Empty;
                }
            }
        }

        public string[] GetKeywords()
        {
            var snapshot = KeywordsText;
            return snapshot
                .Split([',', '，', ';', '；', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(keyword => keyword.Trim())
                .Where(keyword => keyword.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
