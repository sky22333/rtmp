using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamCapturePro.Core.Models;

namespace StreamCapturePro.Core.Utils
{
    public class ObsSyncService
    {
        /// <summary>
        /// 将抓取到的推流信息同步到 OBS 的 service.json 中
        /// </summary>
        public async Task<bool> SyncToObsAsync(StreamInfo info)
        {
            try
            {
                if (info == null || !info.IsValid) return false;

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string obsConfigPath = Path.Combine(appData, "obs-studio", "basic", "profiles");

                if (!Directory.Exists(obsConfigPath))
                {
                    // OBS 尚未安装或未初始化过 Profile
                    return false;
                }

                // 遍历所有的 Profile 文件夹，更新里面的 service.json
                var profileDirs = Directory.GetDirectories(obsConfigPath);
                bool updatedAny = false;

                foreach (var dir in profileDirs)
                {
                    string serviceJsonPath = Path.Combine(dir, "service.json");
                    if (File.Exists(serviceJsonPath))
                    {
                        await UpdateServiceJsonAsync(serviceJsonPath, info);
                        updatedAny = true;
                    }
                    else
                    {
                        // 如果没有 service.json，主动创建一个基础的自定义推流配置
                        await CreateBasicServiceJsonAsync(serviceJsonPath, info);
                        updatedAny = true;
                    }
                }

                return updatedAny;
            }
            catch
            {
                return false;
            }
        }

        private async Task UpdateServiceJsonAsync(string path, StreamInfo info)
        {
            string json = await File.ReadAllTextAsync(path);
            JObject root;
            
            try
            {
                root = JObject.Parse(json);
            }
            catch
            {
                root = new JObject();
            }

            // 强制将推流类型改为自定义 (Custom)
            root["type"] = "rtmp_custom";

            if (root["settings"] == null)
            {
                root["settings"] = new JObject();
            }

            var settings = (JObject)root["settings"]!;
            settings["server"] = info.Server;
            settings["key"] = info.Key;
            settings["use_auth"] = false;

            await File.WriteAllTextAsync(path, root.ToString(Formatting.Indented));
        }

        private async Task CreateBasicServiceJsonAsync(string path, StreamInfo info)
        {
            var root = new JObject
            {
                ["type"] = "rtmp_custom",
                ["settings"] = new JObject
                {
                    ["server"] = info.Server,
                    ["key"] = info.Key,
                    ["use_auth"] = false
                }
            };

            await File.WriteAllTextAsync(path, root.ToString(Formatting.Indented));
        }
    }
}
