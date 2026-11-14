"""Send one move request to the local test plugin. Requires Python 3."""
import socket
import time


def exchange(sock, message):
    try:
        sock.send(message.encode("ascii"))
        reply = sock.recv(32769)
        return reply.decode("ascii") if len(reply) <= 32768 else ""
    except (ConnectionRefusedError, ConnectionResetError, TimeoutError, UnicodeDecodeError):
        return ""


def main():
    print("请切回游戏，F7 绑定 B，再选 A 按 F8。发送器最多等待两分钟。", flush=True)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.connect(("127.0.0.1", 38476))
        sock.settimeout(1)
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            reply = exchange(sock, "TARGET")
            if reply.startswith("TARGET "):
                ticket = reply.split()[1]
                break
            time.sleep(0.3)
        else:
            raise SystemExit("没有找到已标记的目标。请确认游戏已加载 0.2.0 插件，并按 F8。")

        print("已取得目标。请让 A 走开，6 秒后向 B 发令。", flush=True)
        time.sleep(6)
        for _ in range(3):
            reply = exchange(sock, "MOVE " + ticket)
            if reply == "ACCEPTED":
                print("游戏已接受指令。请观察 B 是否到达、A 是否仍能控制。")
                return
            if reply in ("NOT_READY", "REJECTED"):
                raise SystemExit("目标已改变或角色状态不允许发令。请重新按 F8 后重试。")
        raise SystemExit("没有收到游戏确认。请检查插件日志。")


if __name__ == "__main__":
    main()
