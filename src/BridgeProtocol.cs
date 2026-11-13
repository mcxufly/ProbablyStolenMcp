using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProbablyStolenMcp
{
    // Pure protocol layer: no Unity access, no deserialization of caller-chosen types.
    public sealed class BridgeProtocol
    {
        public const int MaxRequestBytes = 16 * 1024;
        public const int MaxResponseBytes = 1024 * 1024;
        readonly string token;
        readonly Func<string, JObject, JObject> execute;
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<string> recent = new Queue<string>();
        static readonly HashSet<string> Fields = new HashSet<string> { "token", "operation", "arguments", "request_id" };

        public BridgeProtocol(string token, Func<string, JObject, JObject> execute)
        {
            if (!ValidToken(token)) throw new ArgumentException("Token must contain 32-128 ASCII letters, digits, '-' or '_'.");
            this.token = token;
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public static bool ValidToken(string value) => value != null && value.Length >= 32 && value.Length <= 128 &&
            value.All(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_');

        public JObject Handle(string json)
        {
            JObject request;
            try
            {
                using (var input = new StringReader(json))
                using (var reader = new JsonTextReader(input) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                {
                    request = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) return Error("invalid_request", "Expected exactly one JSON object.");
                }
            }
            catch (JsonException) { return Error("invalid_request", "Invalid JSON object."); }
            if (request["token"]?.Type != JTokenType.String || !TokenEquals((string)request["token"]))
                return Error("unauthorized", "Authentication failed.");
            if (request.Properties().Any(p => !Fields.Contains(p.Name)) || request.Properties().Count() != 4 ||
                request["operation"]?.Type != JTokenType.String || request["arguments"]?.Type != JTokenType.Object ||
                request["request_id"]?.Type != JTokenType.String)
                return Error("invalid_request", "Required fields: token, operation, arguments, request_id.");
            string id = (string)request["request_id"];
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                return Error("invalid_request", "Invalid request_id.");
            string operation = (string)request["operation"];
            if (operation != "observe" && operation != "ui_action" && operation != "ui_input" &&
                operation != "inventory" && operation != "item_action" && operation != "move_item" &&
                operation != "dialogue_history" && operation != "dialogue_status" && operation != "dialogue_action" &&
                operation != "drop_item" && operation != "equipment_action")
                return Error("unknown_operation", "Operation is not exposed.");
            if (!seen.Add(id)) return Error("duplicate_request", "Request already attempted; observe before making a new decision.");
            recent.Enqueue(id);
            if (recent.Count > 128) seen.Remove(recent.Dequeue());
            try
            {
                return new JObject { ["ok"] = true, ["result"] = execute(operation, (JObject)request["arguments"]) };
            }
            catch (ArgumentException ex) { return Error("invalid_arguments", SafeMessage(ex.Message)); }
            catch (InvalidOperationException ex) { return Error("unavailable", SafeMessage(ex.Message)); }
            catch (Exception) { return Error("operation_failed", "Operation failed; state may have changed. Observe before continuing; do not retry blindly."); }
        }

        bool TokenEquals(string value)
        {
            int difference = value.Length ^ token.Length;
            for (int i = 0; i < token.Length; i++) difference |= token[i] ^ (i < value.Length ? value[i] : 0);
            return difference == 0;
        }

        string SafeMessage(string message)
        {
            string safe = (message ?? "Operation unavailable.").Replace(token, "[redacted]");
            return safe.Length <= 512 ? safe : safe.Substring(0, 512);
        }

        public static JObject Error(string code, string message) => new JObject
        {
            ["ok"] = false, ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
    }
}
