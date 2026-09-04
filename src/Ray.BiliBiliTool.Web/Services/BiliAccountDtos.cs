namespace Ray.BiliBiliTool.Web.Services;

/// <summary>
/// B站账号信息（仅返回给前端展示的安全字段，绝不包含 Cookie 原文等敏感信息）
/// </summary>
public class BiliAccountDto
{
    /// <summary>
    /// 账号序号（对应 cookies.json 中 BiliBiliCookies 数组的下标）
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// B站用户 UID（即 Cookie 中的 DedeUserID）
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// B站昵称
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// 等级
    /// </summary>
    public int? Level { get; set; }

    /// <summary>
    /// Cookie 是否有效：true=有效，false=无效，null=未知/未检测
    /// </summary>
    public bool? IsValid { get; set; }

    /// <summary>
    /// 状态说明（供界面展示）
    /// </summary>
    public string? StatusText { get; set; }

    /// <summary>
    /// 最近一次检测时间
    /// </summary>
    public DateTime? LastCheckedAt { get; set; }
}

/// <summary>
/// 扫码登录二维码信息
/// </summary>
public class BiliQrCodeDto
{
    /// <summary>
    /// 二维码 Key（用于轮询登录结果）
    /// </summary>
    public string QrcodeKey { get; set; } = "";

    /// <summary>
    /// 二维码图片（data:image/png;base64, 前缀）
    /// </summary>
    public string QrImageDataUrl { get; set; } = "";
}

/// <summary>
/// 扫码登录轮询结果
/// </summary>
public class BiliQrLoginCheckDto
{
    /// <summary>
    /// 是否登录成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 状态：pending=等待扫码，scanned=已扫码待确认，expired=已失效，failed=失败，success=成功
    /// </summary>
    public string State { get; set; } = "pending";

    /// <summary>
    /// 给用户看的提示信息
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// 登录成功后的账号 UID
    /// </summary>
    public long? UserId { get; set; }

    /// <summary>
    /// 登录成功后的账号昵称
    /// </summary>
    public string? UserName { get; set; }
}
