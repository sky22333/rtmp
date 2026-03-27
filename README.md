# 🚀 StreamCapture

一个基于 .NET 8 + WPF 的推流信息提取工具，轻量级 高性能，支持一键复制与同步 OBS。


## 使用说明

- 直接在[`releases`](https://github.com/sky22333/rtmp/releases)页面下载构建好的软件。

1. 在直播软件中执行开播动作
2. 启动程序
3. 在“目标进程关键字”中确认或填写关键字（可多个，逗号分隔）
4. 点击“开始获取”
5. 获取后可复制 Server/Key 或一键同步 OBS

## 预览
![主界面](/assets/demo1.png)

### 开发命令

```bash
dotnet restore
dotnet build
dotnet run
```

### 生产环境构建

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\publish\win-x64
```

### 注意事项

- 进程关键字必须与真实进程名匹配（可在任务管理器查看）
- 如果抓到的参数不稳定，优先使用“开播后立即获取”的结果
- OBS 同步会写入本机 OBS 配置文件，请先关闭 OBS 再执行同步

### 免责说明

1. 本软件仅用于个人学习和测试使用，无需提供任何代价，并不可用于任何商业用途及目的（包括二次开发）
2. 工具仅获取公开传输的明文数据，没有任何破解加密、伪装身份等技术手段
3. 使用者需遵守平台方规定，若平台方禁止使用第三方工具，请立即停止工具的使用并删除工具
