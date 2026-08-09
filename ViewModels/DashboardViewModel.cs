using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamCapturePro.Core.Extractors;
using StreamCapturePro.Core.Models;
using StreamCapturePro.Core.Utils;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace StreamCapturePro.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(90);
        private readonly ISnackbarService _snackbarService;
        private readonly IReadOnlyList<IStreamExtractor> _extractors;
        private readonly ObsSyncService _obsSyncService;
        private readonly ProcessScanOptionsService _processScanOptionsService;
        private CancellationTokenSource? _captureCts;

        [ObservableProperty]
        private string _serverAddress = string.Empty;

        [ObservableProperty]
        private string _streamKey = string.Empty;

        [ObservableProperty]
        private bool _isCapturing = false;

        [ObservableProperty]
        private string _statusText = "准备就绪，在推流软件中点击开播后即可开始获取";

        [ObservableProperty]
        private string _processKeywordsText = string.Empty;

        public DashboardViewModel(
            ISnackbarService snackbarService,
            IEnumerable<IStreamExtractor> extractors,
            ObsSyncService obsSyncService,
            ProcessScanOptionsService processScanOptionsService)
        {
            _snackbarService = snackbarService;
            _extractors = extractors.ToList();
            _obsSyncService = obsSyncService;
            _processScanOptionsService = processScanOptionsService;
            ProcessKeywordsText = _processScanOptionsService.KeywordsText;
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task StartCapture()
        {
            if (IsCapturing)
            {
                StopCapture();
                return;
            }

            IsCapturing = true;
            StatusText = "正在监听推流信息，请在直播软件中点击【开始直播】...";
            ServerAddress = string.Empty;
            StreamKey = string.Empty;

            var activeExtractors = _extractors;
            if (activeExtractors.Count == 0)
            {
                IsCapturing = false;
                StatusText = "未找到可用提取引擎。";
                return;
            }

            if (_processScanOptionsService.GetKeywords().Length == 0)
            {
                IsCapturing = false;
                StatusText = "请先填写进程关键字再开始捕获。";
                ShowInfo("请先配置进程关键字，例如：直播伴侣。");
                return;
            }

            using var cts = new CancellationTokenSource();
            _captureCts = cts;
            using var timeoutCts = new CancellationTokenSource(CaptureTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
            var errors = new List<string>();

            try
            {
                var taskMap = activeExtractors.ToDictionary(
                    extractor => extractor,
                    extractor => Task.Run(() => extractor.ExtractAsync(linkedCts.Token), linkedCts.Token));

                while (taskMap.Count > 0)
                {
                    var completedTask = await Task.WhenAny(taskMap.Values);
                    var completedPair = taskMap.First(item => item.Value == completedTask);
                    taskMap.Remove(completedPair.Key);

                    try
                    {
                        var result = await completedTask;
                        if (result is { IsValid: true })
                        {
                            ServerAddress = PublicPushHostResolver.Resolve(result.Server);
                            StreamKey = result.Key;
                            StatusText = $"获取成功！来源：{result.Source.Replace("(", "[").Replace(")", "]")}";
                            ShowSuccess("推流地址和密钥已获取完毕。");
                            cts.Cancel();
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{completedPair.Key.ExtractorName}: {ex.Message}");
                    }
                }

                if (errors.Count > 0)
                {
                    StatusText = "提取失败，请检查日志或抓包环境。";
                    ShowError(errors.First());
                }
                else
                {
                    StatusText = "未能获取到有效的推流信息。";
                }
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.IsCancellationRequested && !cts.IsCancellationRequested)
                {
                    StatusText = "等待命中超时，请确认已点击开播并重试。";
                    ShowInfo("90 秒内未捕获到有效推流信息，请确认直播伴侣已开播。");
                }
                else if (string.IsNullOrEmpty(ServerAddress))
                {
                    StatusText = "获取已停止。";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"发生错误: {ex.Message}";
                ShowError(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_captureCts, cts))
                {
                    _captureCts = null;
                    IsCapturing = false;
                }
            }
        }

        private void StopCapture()
        {
            var cts = _captureCts;
            if (cts != null && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
            if (IsCapturing && string.IsNullOrEmpty(ServerAddress))
            {
                StatusText = "已停止获取";
            }
        }

        private void ShowInfo(string message)
            => _snackbarService.Show("提示", message, ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(5));

        private void ShowSuccess(string message)
            => _snackbarService.Show("成功", message, ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(3));

        private void ShowError(string message)
            => _snackbarService.Show("失败", message, ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), TimeSpan.FromSeconds(5));

        partial void OnProcessKeywordsTextChanged(string value)
        {
            _processScanOptionsService.KeywordsText = value;
        }

        [RelayCommand]
        private void CopyServer()
        {
            if (!string.IsNullOrEmpty(ServerAddress))
            {
                System.Windows.Clipboard.SetText(ServerAddress);
                ShowSuccess("推流服务器地址已复制到剪贴板。");
            }
        }

        [RelayCommand]
        private void CopyKey()
        {
            if (!string.IsNullOrEmpty(StreamKey))
            {
                System.Windows.Clipboard.SetText(StreamKey);
                ShowSuccess("推流密钥已复制到剪贴板。");
            }
        }

        [RelayCommand]
        private async Task SyncToObs()
        {
            if (string.IsNullOrEmpty(ServerAddress) || string.IsNullOrEmpty(StreamKey))
            {
                ShowError("没有可用的推流信息，请先进行获取。");
                return;
            }

            var info = new StreamInfo { Server = ServerAddress, Key = StreamKey };
            bool success = await _obsSyncService.SyncToObsAsync(info);

            if (success)
            {
                ShowSuccess("推流配置已成功写入 OBS Studio，重启 OBS 即可生效。");
            }
            else
            {
                ShowError("未找到 OBS 配置文件，请确认是否安装并运行过 OBS Studio。");
            }
        }
    }
}
