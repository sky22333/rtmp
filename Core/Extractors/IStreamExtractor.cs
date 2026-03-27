using System.Threading;
using System.Threading.Tasks;
using StreamCapturePro.Core.Models;

namespace StreamCapturePro.Core.Extractors
{
    public interface IStreamExtractor
    {
        string ExtractorName { get; }
        
        /// <summary>
        /// 异步提取推流信息。如果提取失败或被取消，应抛出异常或返回 null。
        /// </summary>
        Task<StreamInfo?> ExtractAsync(CancellationToken cancellationToken);
    }
}
