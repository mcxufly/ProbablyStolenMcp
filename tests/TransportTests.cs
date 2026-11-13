using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyStolenMcp;

static class Tests
{
    const string Token = "test-token-" + "00000000000000000000000000000000";
    static int assertions;
    static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
    static string Request(string id, string operation = "observe", string token = Token) => new JObject
    {
        ["token"] = token, ["request_id"] = id, ["operation"] = operation, ["arguments"] = new JObject()
    }.ToString(Formatting.None);
    static string Code(JObject result) => (string)result["error"]!["code"]!;
    static JObject LargeResponse(int payloadBytes)
    {
        int overhead = Encoding.UTF8.GetByteCount("{\"ok\":true,\"result\":{\"padding\":\"\"}}");
        var p = new BridgeProtocol(Token, (op, input) => new JObject { ["padding"] = new string('x', payloadBytes - overhead) });
        using var s = new LoopbackServer(0, p);
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, s.Port);
        client.GetStream().Write(Encoding.UTF8.GetBytes(Request("size") + "\n"));
        using var received = new MemoryStream();
        var buffer = new byte[65536];
        for (int step = 0; step < 256; step++)
        {
            s.Tick();
            if (!client.Client.Poll(1000, SelectMode.SelectRead) || client.Available == 0) continue;
            int count = client.GetStream().Read(buffer, 0, Math.Min(buffer.Length, client.Available));
            received.Write(buffer, 0, count);
            if (buffer[count - 1] == '\n') return JObject.Parse(Encoding.UTF8.GetString(received.ToArray()));
        }
        throw new Exception("Large response did not complete");
    }
    static void CheckRestartAfterSuccess()
    {
        var p = new BridgeProtocol(Token, (op, input) => new JObject());
        using var s = new LoopbackServer(0, p);
        int port = s.Port;
        using (var client = new TcpClient())
        {
            client.ReceiveTimeout = 1000;
            client.Connect(IPAddress.Loopback, port);
            client.GetStream().Write(Encoding.UTF8.GetBytes(Request("restart") + "\n"));
            s.Tick(); s.Tick();
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, leaveOpen: true);
            Check((bool)JObject.Parse(reader.ReadLine())["ok"]!, "restart fixture received response");
        }
        s.Tick(); // Observe peer FIN before closing our listener.
        s.Dispose();
        using var replacement = new LoopbackServer(port, p);
        Check(replacement.Port == port, "exclusive listener rebinds after normal successful calls");
    }
    static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--fixture")
        {
            var fixtureProtocol = new BridgeProtocol(Token, (op, input) => new JObject
            {
                ["operation"] = op, ["echo"] = input, ["fixture"] = true
            });
            using var fixture = new LoopbackServer(0, fixtureProtocol);
            Console.WriteLine(fixture.Port);
            Console.Out.Flush();
            while (true) { fixture.Tick(); System.Threading.Thread.Sleep(1); }
        }
        int calls = 0;
        var protocol = new BridgeProtocol(Token, (op, args) => { calls++; return new JObject { ["operation"] = op }; });
        Check(!BridgeProtocol.ValidToken(""), "empty token denied");
        Check(!BridgeProtocol.ValidToken(new string('a', 31)), "short token denied");
        Check(!BridgeProtocol.ValidToken(new string('a', 129)), "long token denied");
        Check(!BridgeProtocol.ValidToken(new string('中', 32)), "nonASCII token denied");
        Check(BridgeProtocol.ValidToken(Token), "valid token");
        Check(Code(protocol.Handle("[]")) == "invalid_request", "array denied");
        Check(Code(protocol.Handle("{")) == "invalid_request", "bad json denied");
        Check(Code(protocol.Handle(Request("a", token: "wrong"))) == "unauthorized", "auth denied");
        Check(calls == 0, "unauthorized never executes");
        Check(Code(protocol.Handle(Request("a", "eval"))) == "unknown_operation", "no arbitrary commands");
        Check(Code(protocol.Handle(Request("a").Replace("\"arguments\":{}", "\"arguments\":{},\"arguments\":{}"))) == "invalid_request", "duplicate fields denied");
        Check(Code(protocol.Handle(Request("a") + " {}")) == "invalid_request", "trailing data denied");
        Check((bool)protocol.Handle(Request("a"))["ok"]!, "valid executes");
        Check(Code(protocol.Handle(Request("a"))) == "duplicate_request", "duplicates denied");
        Check(calls == 1, "duplicate no mutation");
        var invalid = new BridgeProtocol(Token, (op, args) => throw new ArgumentException("bad " + Token));
        JObject invalidResult = invalid.Handle(Request("b"));
        Check(Code(invalidResult) == "invalid_arguments", "argument classification");
        Check(!invalidResult.ToString().Contains(Token), "error secrets redacted");
        var failed = new BridgeProtocol(Token, (op, args) => throw new Exception("private internal data"));
        Check(!failed.Handle(Request("c")).ToString().Contains("private internal data"), "stack/details hidden");
        using var server = new LoopbackServer(0, protocol);
        Check(server.Port > 0, "listener allocated");
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, server.Port);
            client.ReceiveTimeout = 2000;
            byte[] wire = Encoding.UTF8.GetBytes(Request("socket") + "\n");
            client.GetStream().Write(wire, 0, wire.Length - 1);
            server.Tick();
            Check(calls == 1, "partial line not executed");
            client.GetStream().WriteByte((byte)'\n');
            server.Tick();
            Check(calls == 1, "complete line waits until next tick before dispatch");
            server.Tick();
            var buffer = new byte[4096];
            int count = client.GetStream().Read(buffer);
            Check((bool)JObject.Parse(Encoding.UTF8.GetString(buffer, 0, count))["ok"]!, "loopback roundtrip");
            Check(calls == 2, "one complete request executed");
        }
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, server.Port);
            byte[] wire = Encoding.UTF8.GetBytes(Request("pipeline") + "\n" + Request("pipeline2") + "\n");
            client.GetStream().Write(wire);
            server.Tick();
            Check(calls == 2, "pipelined payload not executed");
        }
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, server.Port);
            byte[] wire = Encoding.UTF8.GetBytes(new string('x', BridgeProtocol.MaxRequestBytes + 1));
            client.GetStream().Write(wire);
            server.Tick();
            Check(calls == 2, "oversized payload not executed");
        }
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, server.Port);
            client.GetStream().Write(Encoding.UTF8.GetBytes(Request("closed-before-dispatch") + "\n"));
            client.Client.Shutdown(SocketShutdown.Both);
        }
        server.Tick();
        server.Tick();
        Check(calls == 2, "closed queued request never dispatches");
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, server.Port);
            client.GetStream().Write(Encoding.UTF8.GetBytes(Request("closed-after-parse") + "\n"));
            server.Tick();
            client.Client.Shutdown(SocketShutdown.Both);
        }
        server.Tick();
        Check(calls == 2, "client closed after parse never dispatches");
        server.Dispose();
        server.Tick(); // idempotent stop and no post-stop work
        Check(calls == 2, "disposed server does not execute");
        Check((bool)LargeResponse(BridgeProtocol.MaxResponseBytes)["ok"]!, "exact 1MiB response payload allowed excluding LF");
        Check(Code(LargeResponse(BridgeProtocol.MaxResponseBytes + 1)) == "response_too_large", "oversized response replaced by bounded error");
        foreach (string operation in new[] { "dialogue_history", "dialogue_status", "dialogue_action" })
        {
            var allowed = new BridgeProtocol(Token, (op, input) => new JObject { ["operation"] = op });
            Check((bool)allowed.Handle(Request(operation, operation))["ok"]!, "native dialogue operation explicitly allowed: " + operation);
        }
        CheckRestartAfterSuccess();
        Console.WriteLine($"PASS: {assertions} transport/protocol assertions");
    }
}
