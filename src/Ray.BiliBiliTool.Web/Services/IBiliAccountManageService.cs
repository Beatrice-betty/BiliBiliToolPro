namespace Ray.BiliBiliTool.Web.Services;

/// <summary>
/// B站账号管理服务
/// </summary>
public interface IBiliAccountManageService
{
    /// <summary>
    /// 获取当前已保存的全部B站账号（安全字段，不含 Cookie 原文）
    /// </summary>
    /// <param name="forceRefresh">true 时强制调用B站接口重新检测账号状态</param>
    Task<List<BiliAccountDto>> GetAccountListAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 检测单个账号的 Cookie 是否有效并刷新昵称等信息
    /// </summary>
    Task<BiliAccountDto> CheckAccountAsync(
        long userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 生成扫码登录二维码
    /// </summary>
    Task<BiliQrCodeDto> CreateLoginQrCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 轮询扫码登录结果，登录成功后会复用现有逻辑自动保存账号
    /// </summary>
    Task<BiliQrLoginCheckDto> CheckQrLoginAsync(
        string qrcodeKey,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 删除指定 UID 的账号
    /// </summary>
    Task DeleteAccountAsync(long userId, CancellationToken cancellationToken = default);
}
