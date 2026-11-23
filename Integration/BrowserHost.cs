using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Raskladka.Integration
{
    internal sealed class BrowserMessage
    {
        public string op { get; set; }
        public string id { get; set; }
        public string state { get; set; }
        public string value { get; set; }
        public bool enabled { get; set; }
    }
    internal static class BrowserProtocol
    {
        internal static readonly string PipeName = "Raskladka.Browser." + WindowsIdentity.GetCurrent().User.Value + "." + Process.GetCurrentProcess().SessionId;
        private const int Limit = 1024 * 1024;
        internal static byte[] Read(Stream stream)
        {
            byte[] header = new byte[4];
            ReadBytes(stream, header);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > Limit) throw new InvalidDataException();
            byte[] bytes = new byte[length];
            ReadBytes(stream, bytes);
            return bytes;
        }
        private static void ReadBytes(Stream stream, byte[] buffer)
        {
            for (int offset = 0; offset < buffer.Length;) {
                int count = stream.Read(buffer, offset, buffer.Length - offset);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
        }
        internal static void Write(Stream stream, byte[] bytes)
        {
            if (bytes.Length > Limit) throw new InvalidDataException();
            lock (stream) {
                byte[] header = BitConverter.GetBytes(bytes.Length);
                stream.Write(header, 0, 4); stream.Write(bytes, 0, bytes.Length); stream.Flush();
            }
        }
        internal static byte[] Encode(BrowserMessage message)
        { return Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(message)); }
        internal static BrowserMessage Decode(byte[] data)
        { return new JavaScriptSerializer().Deserialize<BrowserMessage>(Encoding.UTF8.GetString(data)); }
    }
    public static class BrowserHost
    {
        private const string Origin = "chrome-extension://mcpenfmmkfcimaeobcgebnhkbeemhdaf/";

        public static bool TryRun(string[] args)
        {
            if (args == null || args.Length == 0 || args[0] != Origin) return false;
            using (var input = Console.OpenStandardInput())
            using (var output = Console.OpenStandardOutput())
            using (var ended = new ManualResetEvent(false)) {
                NamedPipeClientStream active = null;
                object state = new object();
                var reader = new Thread(delegate() {
                    try {
                        for (;;) {
                            byte[] bytes = BrowserProtocol.Read(input);
                            lock (state) if (active != null) BrowserProtocol.Write(active, bytes);
                        }
                    } catch (Exception) {
                        lock (state) if (active != null) active.Dispose();
                        ended.Set();
                    }
                });
                reader.IsBackground = true; reader.Start();
                while (!ended.WaitOne(0)) {
                    using (var pipe = new NamedPipeClientStream(".", BrowserProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                        bool wasConnected = false;
                        try {
                            pipe.Connect(1500);
                            wasConnected = true;
                            lock (state) { if (ended.WaitOne(0)) break; active = pipe; }
                            while (!ended.WaitOne(0)) BrowserProtocol.Write(output, BrowserProtocol.Read(pipe));
                        } catch (Exception) {
                            if (wasConnected && !ended.WaitOne(0)) {
                                try { BrowserProtocol.Write(output, BrowserProtocol.Encode(new BrowserMessage { op = "config", enabled = false })); }
                                catch (IOException) { break; }
                            }
                        } finally { lock (state) active = null; }
                    }
                    if (ended.WaitOne(750)) break;
                }
            }
            return true;
        }
    }
}
