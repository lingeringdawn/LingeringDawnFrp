<?php
/**
 * 桌面客户端取回访问密钥的授权回调页
 *
 * 用途：LingeringDawn Frp 客户端在浏览器里登录面板后，自动把该用户的 frp 访问密钥取回本地，
 *       免去“去用户中心复制、再回客户端粘贴”。
 *
 * 流程：
 *   1. 客户端在本机起一个 127.0.0.1 的回环监听，并用系统默认浏览器打开本页：
 *        ?page=app_auth&redirect=http://127.0.0.1:<端口>/&state=<随机串>
 *   2. 本页校验「已登录会话」与 redirect（只允许回环地址），取出该用户的 token
 *   3. 302 回跳给客户端：<redirect>?token=...&state=...&username=...
 *
 * 安全考虑：
 *   - 必须已有登录会话，未登录不会返回任何密钥
 *   - redirect 只接受 http://127.0.0.1:<端口> / http://localhost:<端口>，防止密钥被引到外部地址
 *   - state 原样带回，由客户端校验，防止别的本地进程伪造回调
 *   - 密钥出现在回环地址的 URL 里（会留在本机浏览器历史中），但不经过面板的访问日志
 */

if (!defined("ROOT")) {
    Header("HTTP/1.1 403 Forbidden");
    exit("Forbidden");
}

if (!class_exists("SakuraPanel\\UserManager")) {
    include_once(ROOT . "/core/UserManager.php");
}

$redirect = isset($_GET['redirect']) ? trim((string) $_GET['redirect']) : '';
$state    = isset($_GET['state']) ? (string) $_GET['state'] : '';
$user     = isset($_SESSION['user']) ? (string) $_SESSION['user'] : '';

/** 只允许本机回环地址，且必须是 http（https 本地监听没有证书） */
function app_auth_redirect_allowed($url)
{
    return (bool) preg_match('#^http://(?:127\.0\.0\.1|localhost|\[::1\]):(\d{2,5})/?(\?[^\s]*)?$#i', $url);
}

function app_auth_page($title, $body)
{
    Header("Content-Type: text/html; charset=utf-8");
    echo '<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">'
        . '<meta name="viewport" content="width=device-width,initial-scale=1">'
        . '<title>' . htmlspecialchars($title) . '</title><style>'
        . 'body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;'
        . 'background:#f5f3fb;font-family:"Microsoft YaHei UI",system-ui,sans-serif;color:#1e1a2b}'
        . '.card{background:#fff;border:1px solid #e7e1f3;border-radius:16px;padding:32px 36px;max-width:460px;'
        . 'box-shadow:0 18px 40px rgba(59,42,99,.08)}'
        . 'h1{margin:0 0 12px;font-size:19px}'
        . 'p{margin:0 0 10px;line-height:1.7;color:#6e6785;font-size:14px}'
        . 'a.btn{display:inline-block;margin-top:10px;padding:10px 20px;border-radius:9px;background:#8b3dff;'
        . 'color:#fff;text-decoration:none;font-weight:600;font-size:14px}'
        . '</style></head><body><div class="card"><h1>' . htmlspecialchars($title) . '</h1>' . $body . '</div></body></html>';
}

if ($user === '') {
    app_auth_page('需要先登录', '<p>客户端需要读取你的访问密钥，请先在浏览器里登录面板。</p>'
        . '<p>登录完成后，请回到客户端再次点击「浏览器获取密钥」。</p>'
        . '<a class="btn" href="?page=sso">前往登录</a>');
    exit;
}

if (!app_auth_redirect_allowed($redirect)) {
    app_auth_page('回调地址无效', '<p>出于安全考虑，只允许回环地址（127.0.0.1 / localhost）。</p>'
        . '<p>请从客户端内点击「浏览器获取密钥」重新发起。</p>');
    exit;
}

$um    = new SakuraPanel\UserManager();
$token = $um->getUserToken($user);

if (!$token) {
    app_auth_page('未找到访问密钥', '<p>当前账号（' . htmlspecialchars($user) . '）还没有分配 frp 访问密钥。</p>'
        . '<p>请在面板的用户中心确认账号状态。</p>');
    exit;
}

$separator = (strpos($redirect, '?') === false) ? '?' : '&';
$target    = $redirect . $separator
    . 'token=' . rawurlencode($token)
    . '&username=' . rawurlencode($user)
    . '&state=' . rawurlencode($state);

Header("Location: " . $target);
exit;
