using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using MudBlazor;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Components.Pages.Today;

[Authorize]
public partial class Today : ComponentBase
{
    [Inject]
    private ITodayTaskService TodayTaskService { get; set; } = null!;

    [Inject]
    private IOptionsMonitor<AutoRecoverOptions> AutoRecoverOptions { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    private List<AccountTodayTasksDto> _accounts = [];
    private bool _loading;
    private bool _busy;
    private DateTimeOffset? _lastRefresh;

    private bool _autoEnable;
    private int _intervalHours = 2;
    private int _retentionDays = 3;

    protected override async Task OnInitializedAsync()
    {
        LoadSettings();
        await RefreshAsync();
    }

    private void LoadSettings()
    {
        var config = AutoRecoverOptions.CurrentValue;
        _autoEnable = config.IsEnable;
        _intervalHours = Math.Clamp(config.IntervalHours, 1, 24);
        _retentionDays = Math.Clamp(config.RecordRetentionDays, 1, 90);
    }

    private async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            _accounts = await TodayTaskService.GetTodayStatusAsync(false);
            _lastRefresh = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"加载今日任务失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RedoItemAsync(
        AccountTodayTasksDto account,
        string taskKey,
        TodayTaskItemDto item
    )
    {
        _busy = true;
        try
        {
            var result = await TodayTaskService.RedoAsync(account.UserId, taskKey, item.ItemKey);
            Snackbar.Add(result.Message, result.Success ? Severity.Success : Severity.Warning);
            await RefreshAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RedoAccountAsync(AccountTodayTasksDto account)
    {
        _busy = true;
        try
        {
            var count = await TodayTaskService.RedoAllForAccountAsync(account.UserId);
            Snackbar.Add($"已补做 {count} 项", Severity.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"补做失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RedoAllAsync()
    {
        _busy = true;
        try
        {
            var count = await TodayTaskService.RedoAllMissingAsync();
            Snackbar.Add($"已补做 {count} 项", Severity.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"补做失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DisableShareAsync()
    {
        _busy = true;
        try
        {
            await TodayTaskService.DisableShareAsync();
            Snackbar.Add("已关闭分享任务，之后不再尝试分享", Severity.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"关闭失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        _busy = true;
        try
        {
            await TodayTaskService.SaveAutoRecoverSettingsAsync(
                _autoEnable,
                _intervalHours,
                _retentionDays
            );
            Snackbar.Add("设置已保存", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"保存失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private static string StateIcon(TodayTaskItemState state) =>
        state switch
        {
            TodayTaskItemState.Completed => "✅",
            TodayTaskItemState.NotDone => "❌",
            TodayTaskItemState.Failed => "⚠️",
            TodayTaskItemState.RetryExhausted => "⚠️",
            TodayTaskItemState.Waiting => "⏳",
            TodayTaskItemState.NotToday => "➖",
            TodayTaskItemState.Disabled => "⛔",
            TodayTaskItemState.Unknown => "❓",
            _ => "•",
        };

    private static string StateClass(TodayTaskItemState state) =>
        state switch
        {
            TodayTaskItemState.Completed => "state-ok",
            TodayTaskItemState.NotDone => "state-bad",
            TodayTaskItemState.Failed or TodayTaskItemState.RetryExhausted => "state-warn",
            TodayTaskItemState.Unknown => "state-bad",
            _ => "state-muted",
        };
}
