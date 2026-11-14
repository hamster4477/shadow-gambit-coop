# Security and private data

**English** | [简体中文](SECURITY.zh-CN.md)

This is an experimental co-op gateway intended for cooperating players. It has not received a hostile-client or internet-service security audit. Lack of a fixed player cap is not a guarantee of resistance to resource exhaustion.

Keep invitations and full room URLs private. Native TCP and UDP do not provide TLS encryption. An HTTPS gateway protects the HTTPS connection when configured and certificate validation succeeds; it does not automatically encrypt a separate UDP tunnel.

The log collector best-effort redacts room addresses, user paths and account identifiers, but it cannot guarantee that every new log format is recognised. Please do not post raw crash dumps, entire game directories, saves, process environment dumps or account profiles in public issues; they may contain private data or copyrighted game material. Always inspect the collector's output before publishing it.

Report vulnerabilities and other sensitive problems privately to **hamster4477@qq.com**. Use the repository's Security tab instead if private vulnerability reporting is enabled. If you prefer a public issue, open one without sensitive data and we can move the details to email.

Never paste room URLs, account identifiers, raw dumps or full log bundles into a public thread, even when you believe they are redacted.
