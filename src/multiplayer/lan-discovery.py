"""Short, host-opened pairing window for peers on the same private IPv4 subnet."""
import ipaddress
import re
import socket
import sys
import time
from pathlib import Path


def settings(room_directory, interface):
    host = ipaddress.IPv4Interface(interface)
    if not any(host.network.subnet_of(ipaddress.ip_network(block)) for block in
               ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')):
        raise ValueError('private LAN required')
    room = (room_directory / f'{host.ip}.room').read_text(encoding='ascii').strip()
    if not re.fullmatch(r'[A-Za-z0-9_-]{24}', room):
        raise ValueError('invalid local room')
    return host, room


def serve(room_directory):
    window = room_directory / 'pairing.txt'
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        try:
            sock.bind(('', 38478))
        except OSError as error:
            if error.errno in (98, 10048):  # a previous pairing window was extended
                return
            raise
        sock.settimeout(0.5)
        while 0 <= time.time() - window.stat().st_mtime < 120:
            if not window.exists():
                return
            try:
                host, room = settings(room_directory, window.read_text(encoding='ascii').strip())
            except (OSError, ValueError):
                time.sleep(0.2)
                continue
            try:
                packet, peer = sock.recvfrom(128)
            except socket.timeout:
                continue
            if ipaddress.ip_address(peer[0]) not in host.network or not re.fullmatch(rb'SGC_FIND [0-9a-f]{32}', packet):
                continue
            sock.sendto(b'SGC_ROOM ' + packet[9:] + b' ' + room.encode('ascii'), peer)


if __name__ == '__main__':
    serve(Path(sys.argv[1]))
