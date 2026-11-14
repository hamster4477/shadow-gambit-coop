# Security and private data

**English** | [简体中文](SECURITY.zh-CN.md)

This is an experimental co-op gateway intended for cooperating players. It has not received a hostile-client or internet-service security audit. Lack of a fixed player cap is not a guarantee of resistance to resource exhaustion.

Keep invitations and full room URLs private. Native TCP and UDP do not provide TLS encryption. An HTTPS gateway protects the HTTPS connection when configured and certificate validation succeeds; it does not automatically encrypt a separate UDP tunnel.

The log collector best-effort redacts room addresses, user paths and account identifiers, but it cannot guarantee that every new log format is recognised. Please do not post raw crash dumps, entire game directories, saves, process environment dumps or account profiles in public issues; they may contain private data or copyrighted game material. Always inspect the collector's output before publishing it.

If private vulnerability reporting is enabled for the repository, use its Security tab for confidential reports. Otherwise, first open an issue without sensitive data to arrange private disclosure. No private contact address is invented by this source export.
