# 面板侧配套文件

客户端的「浏览器获取密钥」依赖面板上的一个授权页。部署方式：

    把 app_auth.php 放到面板的 pages/ 目录
    /var/www/html/SakuraPanel/pages/app_auth.php

无需改配置、无需加路由（面板按 `?page=xxx` 映射到 `pages/xxx.php`）。
该页要求登录会话，未登录时面板会先给出登录页。

## 工作方式

1. 客户端在 `127.0.0.1` 起临时监听（系统分配空闲端口），生成随机 `state`
2. 用系统默认浏览器打开：
   `https://<面板>/?page=app_auth&redirect=http://127.0.0.1:<端口>/&state=<state>`
3. 授权页校验浏览器里的登录会话与 `redirect`（只允许回环地址），取出该用户的 frp 密钥
4. 302 回跳：`<redirect>?token=...&username=...&state=...`
5. 客户端校验 `state` 后填入并保存密钥

## 安全边界

- 未登录不返回任何密钥
- `redirect` 只接受 `http://127.0.0.1|localhost:<端口>`
- `state` 原样带回、由客户端校验，防本机其它进程伪造回调
- 密钥只出现在本机回环 URL（会留在本机浏览器历史），不经过面板访问日志
