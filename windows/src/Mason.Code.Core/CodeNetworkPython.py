"""pip HTTPS transport for the command-owned relay. Direct sockets remain OS-denied."""
import io
import json
import os
import socket


class PipeSocket:
    def __init__(self, host, port, timeout):
        path = os.environ['MASON_NETWORK_PIPE']
        if not path.startswith('\\\\.\\pipe\\dbc-network-'):
            raise OSError('Invalid network relay')
        self.stream = open(path, 'r+b', buffering=0)
        self._io_refs = 0
        self._closed = False
        self.timeout = timeout
        try:
            self.sendall((json.dumps({'host': host, 'port': port}) + '\n').encode())
            data = bytearray()
            while len(data) < 4096:
                byte = self.recv(1)
                if not byte:
                    raise OSError('Network relay closed before approval')
                if byte == b'\n':
                    break
                data.extend(byte)
            if len(data) >= 4096:
                raise OSError('Network relay response exceeded bound')
            reply = json.loads(data)
            if reply.get('ok') is not True:
                raise OSError(reply.get('error', 'Network declined'))
        except BaseException:
            self.close()
            raise

    def sendall(self, data, flags=0):
        if flags:
            raise ValueError('Socket flags unsupported')
        view = memoryview(data)
        while view:
            written = self.stream.write(view)
            if not written:
                raise OSError('Network relay closed')
            view = view[written:]

    def recv(self, count, flags=0):
        if flags:
            raise ValueError('Socket flags unsupported')
        return self.stream.read(count)

    def settimeout(self, value):
        self.timeout = value  # Host enforces the command/connection deadline.

    def gettimeout(self):
        return self.timeout

    def fileno(self):
        return self.stream.fileno()

    def close(self):
        self._closed = True
        if self._io_refs <= 0:
            self.stream.close()

    def _decref_socketios(self):
        self._io_refs -= 1
        if self._closed and self._io_refs <= 0:
            self.stream.close()

    def shutdown(self, how):
        self.close()


def install():
    # Python 3.13's Windows mode 0700 replaces inherited ACLs with user/admin
    # entries, dropping the LPAC and write-restricted identities. Scratch-only
    # temporary directories must retain the workspace's inherited ACL instead.
    original_mkdir = os.mkdir
    scratch_temp = os.path.normcase(os.path.dirname(os.path.abspath(__file__))) + os.sep

    def mkdir(path, mode=0o777, *, dir_fd=None):
        candidate = os.path.normcase(os.path.abspath(os.fsdecode(path)))
        if mode == 0o700 and dir_fd is None and candidate.startswith(scratch_temp):
            mode = 0o777
        return original_mkdir(path, mode, dir_fd=dir_fd)

    os.mkdir = mkdir
    if not os.environ.get('MASON_NETWORK_PIPE'):
        return
    try:
        from pip._vendor.urllib3 import connection, connectionpool
    except ImportError:
        return
    original_wrap = connection.ssl_wrap_socket
    original_dropped = connectionpool.is_connection_dropped

    def connect(self):
        if not isinstance(self, connection.HTTPSConnection):
            raise OSError('Sandbox package networking requires HTTPS')
        return PipeSocket(self.host, self.port or 443, self.timeout)

    def wrap(*args, **kwargs):
        kwargs['tls_in_tls'] = True
        return original_wrap(*args, **kwargs)

    def dropped(conn):
        transport = getattr(conn, 'sock', None)
        inner = getattr(transport, 'socket', None)
        if isinstance(inner, PipeSocket):
            return inner.stream.closed
        return original_dropped(conn)

    connection.HTTPConnection._new_conn = connect
    connection.ssl_wrap_socket = wrap
    connectionpool.is_connection_dropped = dropped


install()
