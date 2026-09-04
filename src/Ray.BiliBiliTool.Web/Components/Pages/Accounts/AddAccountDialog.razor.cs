using Microsoft.AspNetCore.Components;
using MudBlazor;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Components.Pages.Accounts;

public partial class AddAccountDialog : ComponentBase, IDisposable
{
    [CascadingParameter]
    private IMudDialogInstance MudDialog { get; set; } = null!;

    [Inject]
    private IBiliAccountManageService AccountService { get; set; } = null!;

    private string? _qrKey;
    private string? _qrImageDataUrl;
    private string _statusMessage = "";
    private bool _creating;
    private bool _checking;
    private bool _finished;
    private bool _loginSuccess;

    private Timer? _pollTimer;
    private Timer? _closeTimer;
    private CancellationTokenSource? _cts;
    private bool _busy;

    protected override async Task OnInitializedAsync()
    {
        await GenerateQrCodeAsync();
        await base.OnInitializedAsync();
    }

    private async Task GenerateQrCodeAsync()
    {
        StopPolling();

        _creating = true;
        _checking = false;
        _finished = false;
        _loginSuccess = false;
        _statusMessage = "正在生成二维码…";
        _qrImageDataUrl = null;
        _qrKey = null;

        try
        {
            var dto = await AccountService.CreateLoginQrCodeAsync();
            _qrKey = dto.QrcodeKey;
            _qrImageDataUrl = dto.QrImageDataUrl;
            _statusMessage = "等待扫码：请使用哔哩哔哩 App 扫描二维码";
            StartPolling();
        }
        catch (Exception ex)
        {
            _statusMessage = $"生成二维码失败：{ex.Message}";
        }
        finally
        {
            _creating = false;
        }
    }

    private void StartPolling()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pollTimer = new Timer(
            async _ => await PollAsync(token),
            null,
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(1.5)
        );
    }

    private async Task PollAsync(CancellationToken token)
    {
        if (_finished || _busy || string.IsNullOrEmpty(_qrKey))
        {
            return;
        }

        _busy = true;
        _checking = true;
        try
        {
            var result = await AccountService.CheckQrLoginAsync(_qrKey!, token);
            await InvokeAsync(() =>
            {
                _statusMessage = result.Message ?? "";
                switch (result.State)
                {
                    case "success":
                        _finished = true;
                        _loginSuccess = true;
                        _checking = false;
                        StopPolling();
                        ScheduleAutoClose();
                        break;
                    case "expired":
                    case "failed":
                        _finished = true;
                        _loginSuccess = false;
                        _checking = false;
                        StopPolling();
                        break;
                    default:
                        // pending：等待扫码 / scanned：等待确认，继续轮询
                        _checking = false;
                        break;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 对话框被关闭，忽略
        }
        catch (Exception ex)
        {
            await InvokeAsync(() => _statusMessage = $"登录检测异常：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void ScheduleAutoClose()
    {
        _closeTimer?.Dispose();
        _closeTimer = new Timer(
            _ => InvokeAsync(Close),
            null,
            TimeSpan.FromSeconds(1.2),
            Timeout.InfiniteTimeSpan
        );
    }

    private void StopPolling()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void Close()
    {
        StopPolling();
        _closeTimer?.Dispose();
        _closeTimer = null;
        MudDialog.Close(DialogResult.Ok(_loginSuccess));
    }

    public void Dispose()
    {
        StopPolling();
        _closeTimer?.Dispose();
    }
}
