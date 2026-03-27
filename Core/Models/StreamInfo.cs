namespace StreamCapturePro.Core.Models
{
    public class StreamInfo
    {
        public string Server { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Source { get; set; } = "Unknown"; // 标识是从哪里提取的 (Log, Network, etc)
        public bool IsValid => !string.IsNullOrEmpty(Server) && !string.IsNullOrEmpty(Key);
    }
}