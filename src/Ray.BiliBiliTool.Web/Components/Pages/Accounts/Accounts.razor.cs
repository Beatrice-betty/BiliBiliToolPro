using Microsoft.AspNetCore.Components;
using MudBlazor;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Components.Pages.Accounts;

public partial class Accounts : ComponentBase
{
    [Inject]
    private IBiliAccountManageService AccountService { get; set; } = null!;

    [Inject]
    private IDialogService DialogService { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    private List<BiliAccountDto> _accounts = [];
    private bool _loading;
    private bool _busy;

    protected override async Task OnInitializedAsync()
    {
        await LoadAccountsAsync(false);
    }

    private async Task LoadAccountsAsync(bool force)
    {
        _loading = true;
        try
        {
            _accounts = await AccountService.GetAccountListAsync(force);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"加载账号失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ShowAddAccountDialogAsync()
    {
        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Small,
            FullWidth = true,
        };

        var dialog = await DialogService.ShowAsync<AddAccountDialog>("添加 B 站账号", options);
        var result = await dialog.Result;

        if (result is not null && !result.Canceled && (result.Data is bool ok) && ok)
        {
            Snackbar.Add("账号添加成功，将自动对所有账号执行任务。", Severity.Success);
            await LoadAccountsAsync(true);
        }
    }

    private async Task CheckSingleAccountAsync(BiliAccountDto account)
    {
        if (account.UserId <= 0)
        {
            Snackbar.Add("该账号无法识别 UID，无法检测。", Severity.Warning);
            return;
        }

        _busy = true;
        try
        {
            var refreshed = await AccountService.CheckAccountAsync(account.UserId);
            var old = _accounts.FindIndex(x => x.UserId == account.UserId);
            if (old >= 0)
            {
                _accounts[old] = refreshed;
            }
            Snackbar.Add(
                refreshed.IsValid == true
                    ? $"账号（UID：{account.UserId}）登录状态有效。"
                    : $"账号（UID：{account.UserId}）{refreshed.StatusText}",
                refreshed.IsValid == true ? Severity.Success : Severity.Warning
            );
        }
        catch (Exception ex)
        {
            Snackbar.Add($"检测失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteAccountAsync(BiliAccountDto account)
    {
        if (account.UserId <= 0)
        {
            Snackbar.Add(
                "该账号无法识别 UID，请在服务器上直接编辑 config/cookies.json 删除。",
                Severity.Warning
            );
            return;
        }

        var accountName = string.IsNullOrWhiteSpace(account.UserName)
            ? $"UID {account.UserId}"
            : $"“{account.UserName}”（UID：{account.UserId}）";

        var message =
            $"确定要删除账号 {accountName} 吗？{Environment.NewLine}"
            + "删除后该账号保存的登录信息将被移除，相关任务将不再对该账号执行。";

        var confirmed = await DialogService.ShowMessageBox(
            title: "删除账号",
            message: message,
            yesText: "删除",
            cancelText: "取消"
        );

        if (confirmed != true)
        {
            return;
        }

        _busy = true;
        try
        {
            await AccountService.DeleteAccountAsync(account.UserId);
            Snackbar.Add($"账号 {accountName} 已删除。", Severity.Success);
            await LoadAccountsAsync(true);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"删除失败：{ex.Message}", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }
}
