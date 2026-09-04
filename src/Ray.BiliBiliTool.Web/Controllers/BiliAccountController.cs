using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Controllers;

/// <summary>
/// B站账号管理接口（受登录保护，不会返回完整 Cookie）
/// </summary>
[ApiController]
[Authorize]
[Route("api/biliaccount")]
public class BiliAccountController(IBiliAccountManageService accountManageService) : ControllerBase
{
    /// <summary>
    /// 获取全部B站账号（不含Cookie原文）
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default
    )
    {
        var list = await accountManageService.GetAccountListAsync(force, cancellationToken);
        return Ok(list);
    }

    /// <summary>
    /// 检测并刷新指定账号的登录状态
    /// </summary>
    [HttpGet("check/{userId:long}")]
    public async Task<IActionResult> Check(
        long userId,
        CancellationToken cancellationToken = default
    )
    {
        var account = await accountManageService.CheckAccountAsync(userId, cancellationToken);
        return Ok(account);
    }

    /// <summary>
    /// 生成扫码登录二维码
    /// </summary>
    [HttpPost("qrcode")]
    public async Task<IActionResult> CreateQrCode(CancellationToken cancellationToken = default)
    {
        var qrCode = await accountManageService.CreateLoginQrCodeAsync(cancellationToken);
        return Ok(qrCode);
    }

    /// <summary>
    /// 轮询扫码登录结果（成功后会保存账号）
    /// </summary>
    [HttpGet("qrcode/status")]
    public async Task<IActionResult> CheckQrCode(
        [FromQuery] string key,
        CancellationToken cancellationToken = default
    )
    {
        var result = await accountManageService.CheckQrLoginAsync(key, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 删除指定 UID 的账号
    /// </summary>
    [HttpDelete("{userId:long}")]
    public async Task<IActionResult> Delete(
        long userId,
        CancellationToken cancellationToken = default
    )
    {
        await accountManageService.DeleteAccountAsync(userId, cancellationToken);
        return Ok();
    }
}
