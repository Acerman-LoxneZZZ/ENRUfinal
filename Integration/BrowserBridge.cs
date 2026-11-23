using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Raskladka.Integration
{
    internal sealed class BrowserBridge : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        private readonly object gate = new object();
        private readonly List<BrowserPeer> peers = new List<BrowserPeer>();
        private NamedPipeServerStream listening;
        private bool disposed;
        internal BrowserBridge()
        {
            var listener = new Thread(Listen) { IsBackground = true, Name = "Раскладка · браузер" };
            listener.Start();
        }

        private void Listen()
        {
            try {
                var rights = new PipeSecurity();
                rights.SetAccessRuleProtection(true, false);
                rights.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                for (;;) {
                    NamedPipeServerStream server;
                    lock (gate) {
                        if (disposed) return;
                        listening = server = new NamedPipeServerStream(BrowserProtocol.PipeName, PipeDirection.InOut, 16,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, rights);
                    }
                    try {
                        server.WaitForConnection();
                        uint pid;
                        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out pid)) continue;
                        using (var client = Process.GetProcessById((int)pid))
                        using (var self = Process.GetCurrentProcess())
                            if (!String.Equals(client.MainModule.FileName, self.MainModule.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                        lock (gate) {
                            if (disposed) return;
                            var peer = new BrowserPeer(server, Remove);
                            peers.Add(peer);
                            // An already loaded 1.0 extension treats this as enabled=false
                            // and can still correct explicit selections until reloaded.
                            peer.Send(new BrowserMessage { op = "config" });
                            peer.Start(); listening = null; server = null;
                        }
                    } catch (Exception) { lock (gate) if (disposed) return; }
                    finally { if (server != null) server.Dispose(); }
                }
            } catch (Exception) { /* An unavailable optional bridge must leave native editors usable. */ }
        }
        private void Remove(BrowserPeer peer) { lock (gate) peers.Remove(peer); }
        internal EditorResult Correct(Func<bool> fieldStillFocused, Func<string, EditConversion> convert)
        {
            BrowserPeer[] current;
            lock (gate) current = peers.ToArray();
            if (current.Length == 0) return EditorResult.Refuse("Подключите расширение «Раскладка» для браузера");
            string id = Guid.NewGuid().ToString("N");
            foreach (var peer in current) {
                if (!fieldStillFocused()) return EditorResult.Refuse(EditorService.ContextChanged);
                var ready = peer.Request(new BrowserMessage { op = "prepare", id = id });
                if (ready == null || ready.state == "inactive") continue;
                if (ready.state != "ready") return EditorResult.Refuse(ready.state == "empty" ? EditorService.SelectHint : EditorService.Unavailable);
                try {
                    EditConversion change = convert(ready.value);
                    if (change == null || change.Replacement == null || change.Replacement == ready.value)
                        return EditorResult.Refuse(change == null ? EditorService.Unavailable : change.Message);
                    if (!fieldStillFocused()) return EditorResult.Refuse(EditorService.ContextChanged);
                    var result = peer.Request(new BrowserMessage { op = "commit", id = id, value = change.Replacement });
                    return result != null && result.state == "done" ? EditorResult.Done(change) : EditorResult.Refuse(EditorService.ContextChanged);
                } finally {
                    try { peer.Send(new BrowserMessage { op = "release", id = id }); } catch (IOException) { }
                }
            }
            return EditorResult.Refuse(EditorService.Unavailable);
        }
        public void Dispose()
        {
            BrowserPeer[] current;
            lock (gate) {
                disposed = true; if (listening != null) listening.Dispose();
                current = peers.ToArray();
            }
            foreach (var peer in current) peer.Dispose();
        }
    }
    internal sealed class BrowserPeer : IDisposable
    {
        private readonly NamedPipeServerStream stream;
        private readonly Action<BrowserPeer> remove;
        private readonly object gate = new object();
        private TaskCompletionSource<BrowserMessage> waiting;
        private string expected;
        internal BrowserPeer(NamedPipeServerStream stream, Action<BrowserPeer> remove) { this.stream = stream; this.remove = remove; }
        internal void Start()
        {
            new Thread(delegate() {
                try {
                    for (;;) {
                        var message = BrowserProtocol.Decode(BrowserProtocol.Read(stream));
                        lock (gate) if (message.id == expected && waiting != null) waiting.TrySetResult(message);
                    }
                } catch (Exception) { }
                finally { Dispose(); }
            }) { IsBackground = true }.Start();
        }
        internal void Send(BrowserMessage message) { BrowserProtocol.Write(stream, BrowserProtocol.Encode(message)); }
        internal BrowserMessage Request(BrowserMessage message)
        {
            var pending = new TaskCompletionSource<BrowserMessage>();
            try {
                lock (gate) { waiting = pending; expected = message.id; }
                Send(message);
                return pending.Task.Wait(1600) ? pending.Task.Result : null;
            } catch (Exception) { return null; }
            finally { lock (gate) waiting = null; }
        }
        public void Dispose()
        {
            lock (gate) if (waiting != null) waiting.TrySetResult(null);
            stream.Dispose(); remove(this);
        }
    }
}
