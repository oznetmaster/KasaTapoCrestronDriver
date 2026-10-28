"""Maintainer-only transport test; requires Paramiko 4.0.0, binds loopback only."""
import errno
import hashlib
import json
import logging
import os
from pathlib import Path
import socket
import subprocess
import sys
import threading
import paramiko

logging.getLogger("paramiko").setLevel(logging.CRITICAL)
root = Path(sys.argv[2]).resolve()
root.mkdir(parents=True, exist_ok=True)
remote_root = root / "remote"
(remote_root / "user/ThirdPartyDrivers/Import").mkdir(parents=True, exist_ok=True)
auth = []

class Server(paramiko.ServerInterface):
    def check_auth_password(self, username, password):
        auth.append(username)
        return paramiko.AUTH_SUCCESSFUL if (username, password) == ("test-uploader", "test-only") else paramiko.AUTH_FAILED
    def get_allowed_auths(self, username):
        return "password"
    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

class Files(paramiko.SFTPServerInterface):
    def local(self, path):
        target = (remote_root / path.lstrip("/")).resolve()
        if remote_root != target and remote_root not in target.parents:
            raise PermissionError("Outside test root")
        return target
    def stat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(self.local(path).stat())
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno or errno.EACCES)
    lstat = stat
    def open(self, path, flags, attrs):
        try:
            descriptor = os.open(self.local(path), flags | os.O_BINARY, 0o600)
            file = os.fdopen(descriptor, "r+b" if flags & (os.O_WRONLY | os.O_RDWR) else "rb", buffering=0)
            handle = paramiko.SFTPHandle(flags)
            handle.readfile = file
            handle.writefile = file
            return handle
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno or errno.EACCES)
    def rename(self, old, new):
        try:
            os.rename(self.local(old), self.local(new))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno or errno.EACCES)
    def remove(self, path):
        try:
            self.local(path).unlink()
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno or errno.EACCES)

key = paramiko.RSAKey.generate(2048)
listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen(5)
listener.settimeout(1)
stop = threading.Event()
sessions = []
def accept():
    while not stop.is_set():
        try:
            connection, _ = listener.accept()
        except socket.timeout:
            continue
        except OSError:
            return
        transport = paramiko.Transport(connection)
        sessions.append(transport)
        transport.add_server_key(key)
        transport.set_subsystem_handler("sftp", paramiko.SFTPServer, Files)
        try:
            transport.start_server(server=Server())
        except (paramiko.SSHException, EOFError, ConnectionError):
            transport.close()
thread = threading.Thread(target=accept, daemon=True)
thread.start()
try:
    script = Path(__file__).with_name("Test-LoopbackSftp.ps1")
    run = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
        "-Executable", str(Path(sys.argv[1]).resolve()), "-Port", str(listener.getsockname()[1]), "-ReportDirectory", str(root)],
        timeout=50, capture_output=True, text=True)
    (root / "loopback.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    if run.returncode:
        raise RuntimeError(run.stdout + run.stderr)
    # Probe and incorrect-key attempts must never reach password authentication.
    assert auth == ["test-uploader"], auth
    packages = list((remote_root / "user/ThirdPartyDrivers/Import").iterdir())
    assert len(packages) == 1 and packages[0].suffix == ".pkg"
    report = json.loads((root / "loopback-result.json").read_text(encoding="utf-8-sig"))
    report["RemoteSha256"] = hashlib.sha256(packages[0].read_bytes()).hexdigest()
    report["PasswordAuthentications"] = len(auth)
    (root / "loopback-result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))
finally:
    stop.set()
    listener.close()
    for transport in sessions:
        transport.close()
    thread.join(timeout=2)
