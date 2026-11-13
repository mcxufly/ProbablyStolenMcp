using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace ProbablyStolenMcp
{
    // Poll from our own MonoBehaviour.Update: no unmanaged threads, tasks or Unity access off-thread.
    public sealed class LoopbackServer : IDisposable
    {
        sealed class Connection : IDisposable
        {
            public Socket Socket;
            public readonly byte[] Input = new byte[BridgeProtocol.MaxRequestBytes + 1];
            public int Used;
            public string Request;
            public byte[] Output;
            public int Sent;
            public long Started = Stopwatch.GetTimestamp();
            public void Dispose() { Socket.Dispose(); }
        }
        readonly TcpListener listener;
        readonly BridgeProtocol protocol;
        readonly List<Connection> connections = new List<Connection>();
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        bool disposed;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public LoopbackServer(int port, BridgeProtocol protocol)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(4);
        }

        public void Tick()
        {
            if (disposed) return;
            // Bound socket and command work per frame even if another local process floods us.
            for (int i = 0; i < 4 && listener.Pending(); i++)
            {
                Socket socket = listener.AcceptSocket();
                if (connections.Count >= 4) { socket.Dispose(); continue; }
                socket.Blocking = false;
                socket.NoDelay = true;
                connections.Add(new Connection { Socket = socket });
            }
            bool executed = false;
            for (int i = connections.Count - 1; i >= 0; i--)
            {
                Connection connection = connections[i];
                bool keep;
                try
                {
                    keep = (Stopwatch.GetTimestamp() - connection.Started) / (double)Stopwatch.Frequency < 5 &&
                        Pump(connection, ref executed);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { keep = true; }
                catch (SocketException) { keep = false; }
                catch (ObjectDisposedException) { keep = false; }
                if (!keep) { connection.Dispose(); connections.RemoveAt(i); }
            }
        }

        bool Pump(Connection c, ref bool executed)
        {
            if (c.Output == null)
            {
                if (executed) return true;
                if (c.Request == null)
                {
                    // Poll never blocks the Unity frame. Parse now, dispatch no earlier than the next Tick.
                    if (!c.Socket.Poll(0, SelectMode.SelectRead)) return true;
                    int received = c.Socket.Receive(c.Input, c.Used, c.Input.Length - c.Used, SocketFlags.None);
                    if (received == 0) return false;
                    c.Used += received;
                    int newline = Array.IndexOf(c.Input, (byte)'\n', 0, c.Used);
                    if (newline < 0) return c.Used < c.Input.Length;
                    // One connection carries one request, never pipelined/multiline data.
                    if (newline > BridgeProtocol.MaxRequestBytes || newline != c.Used - 1) return false;
                    try { c.Request = Utf8.GetString(c.Input, 0, newline); }
                    catch (DecoderFallbackException) { return false; }
                    return true;
                }
                // After a complete line, readable means FIN or forbidden extra data.
                // Drop clients that closed while the game was stalled. A disconnect racing an
                // already executing action is inherently uncertain and cannot roll it back.
                if (c.Socket.Poll(0, SelectMode.SelectRead)) return false;
                executed = true;
                var result = protocol.Handle(c.Request);
                c.Output = Utf8.GetBytes(result.ToString(Formatting.None) + "\n");
                if (c.Output.Length - 1 > BridgeProtocol.MaxResponseBytes)
                    c.Output = Utf8.GetBytes(BridgeProtocol.Error("response_too_large", "Use a smaller observation page.").ToString(Formatting.None) + "\n");
            }
            // The client reads one line then closes. Let it close first, avoiding server-side
            // TIME_WAIT on successful calls so applying settings can rebind the exclusive port.
            if (c.Sent == c.Output.Length) return !c.Socket.Poll(0, SelectMode.SelectRead);
            if (!c.Socket.Poll(0, SelectMode.SelectWrite)) return true;
            int remaining = Math.Min(64 * 1024, c.Output.Length - c.Sent);
            c.Sent += c.Socket.Send(c.Output, c.Sent, remaining, SocketFlags.None);
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            listener.Stop();
            foreach (Connection connection in connections) connection.Dispose();
            connections.Clear();
        }
    }
}
