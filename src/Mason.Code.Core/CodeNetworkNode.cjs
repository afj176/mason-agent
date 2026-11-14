// Compatibility transport only. The host authenticates the job/token and enforces destinations.
'use strict';
const net = require('node:net');
const tls = require('node:tls');
const { Duplex } = require('node:stream');
const connectPipe = net.createConnection;
const tlsConnect = tls.connect;
const pipePath = process.env.MASON_NETWORK_PIPE;
if (pipePath) {
  class RelaySocket extends Duplex {
    constructor(host, port) {
      super(); this.connecting = true; this.pending = null;
      this.remoteAddress = host; this.remotePort = Number(port); this.remoteFamily = 'relay';
      let header = Buffer.alloc(0), ready = false;
      this.transport = connectPipe({ path: pipePath });
      this.transport.on('connect', () => this.transport.write(JSON.stringify({host, port: Number(port)}) + '\n'));
      this.transport.on('data', data => {
        if (!ready) {
          header = Buffer.concat([header, data]); const end = header.indexOf(10);
          if (end < 0) { if (header.length > 2048) this.destroy(new Error('Network broker response exceeded bound')); return; }
          let response; try { response = JSON.parse(header.subarray(0, end)); } catch { this.destroy(new Error('Invalid network broker response')); return; }
          if (response.ok !== true) { this.destroy(new Error(response.error || 'Network denied')); return; }
          ready = true; this.connecting = false;
          this.emit('connect'); this.emit('ready');
          if (this.pending) { const [chunk, encoding, callback] = this.pending; this.pending = null; this.transport.write(chunk, encoding, callback); }
          data = header.subarray(end + 1); header = null;
        }
        if (data.length && !this.push(data)) this.transport.pause();
      });
      this.transport.on('end', () => this.push(null));
      this.transport.on('error', error => this.destroy(error));
      this.transport.on('close', () => { if (!this.destroyed) this.destroy(); });
      this.transport.on('timeout', () => this.emit('timeout'));
    }
    _read() { this.transport?.resume(); }
    _write(chunk, encoding, callback) { if (this.connecting) this.pending = [chunk, encoding, callback]; else this.transport.write(chunk, encoding, callback); }
    _final(callback) { this.transport.end(callback); }
    _destroy(error, callback) { if(this.pending) { const p=this.pending;this.pending=null;p[2](error||new Error('Network connection closed')); } this.transport?.destroy(); callback(error); }
    setTimeout(ms, callback) { this.transport.setTimeout(ms); if(callback)this.once('timeout',callback); return this; }
    setNoDelay() { return this; }
    setKeepAlive() { return this; }
    ref() { this.transport.ref(); return this; }
    unref() { this.transport.unref(); return this; }
    address() { return {address:'broker',family:'relay',port:0}; }
  }
  function options(args) {
    if(typeof args[0] === 'object') return {...args[0]};
    if(typeof args[0] === 'string' && !/^\d+$/.test(args[0])) return {path:args[0]};
    return {port:Number(args[0]),host:typeof args[1] === 'string'?args[1]:'localhost'};
  }
  function relay(...args) {
    const opts = options(args);
    if(opts.path) return connectPipe(...args);
    const socket = new RelaySocket(opts.host || opts.hostname || 'localhost',opts.port);
    const callback = args[args.length-1];if(typeof callback === 'function')socket.once('connect',callback);
    return socket;
  }
  net.connect = net.createConnection = relay;
  tls.connect = function(...args) {
    const opts = options(args);
    if(opts.socket || opts.path) return tlsConnect(...args);
    const host = opts.host || opts.hostname || opts.servername || 'localhost';
    opts.socket = new RelaySocket(host, opts.port || 443);
    opts.servername ??= host;
    const callback = args[args.length-1];
    return typeof callback === 'function' ? tlsConnect(opts,callback) : tlsConnect(opts);
  };
}
