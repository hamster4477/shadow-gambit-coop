# Security and private data / 安全与隐私

This is an experimental co-op gateway intended for cooperating players. It has not received a hostile-client or internet-service security audit. Lack of a fixed player cap is not a guarantee of resistance to resource exhaustion.

Keep invitations and full room URLs private. Native TCP and UDP do not provide TLS encryption. An HTTPS gateway protects the HTTPS connection when configured and certificate validation succeeds; it does not automatically encrypt a separate UDP tunnel.

Please do not post raw crash dumps, entire game directories, saves, process environment dumps or account profiles in public issues. They may contain private data or copyrighted game material. The log collector performs best-effort redaction, but always inspect its output before publishing.

日志收集器会尽量遮盖房间地址、用户路径及账号标识，但不能保证任意新增日志格式都已识别。公开反馈前请人工检查；不要上传原始崩溃转储、存档、账号目录或完整游戏文件夹。

If GitHub private vulnerability reporting is enabled for the repository, use its Security tab for confidential reports. Otherwise, first open an issue without sensitive data to arrange private disclosure. No private contact address is invented by this source export.
