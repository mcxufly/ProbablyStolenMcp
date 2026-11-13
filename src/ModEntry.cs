using System;
using UnityEngine;

namespace ProbablyStolenMcp
{
    public sealed class ModEntry : IMod
    {
        public const string Id = "xufly_probably_stolen_mcp";
        ModLog log;
        ConfigEntry<bool> enabled;
        ConfigEntry<int> port;
        ConfigEntry<string> token;
        GameObject host;

        public void Init(ModManifest manifest)
        {
            log = new ModLog(manifest);
            ModConfig config = ModHelper.GetConfig(Id);
            enabled = config.Define("enabled", false, "MCP", () => Text("enabled"), tooltip: () => Text("enabled_help"));
            port = config.Define("port", 17653, "MCP", () => Text("port"), min: 1024, max: 65535);
            token = config.Define("token", "", "MCP", () => Text("token"), tooltip: () => Text("token_help"));
        }

        static string Text(string key) => ModHelper.GetLocalized(Id, key);

        public void OnEnable()
        {
            ModHook.OnModConfigApplied += ConfigApplied;
            Restart();
        }

        void ConfigApplied(string modId) { if (modId == Id) Restart(); }

        void Restart()
        {
            Stop();
            if (!enabled.Value) { log.Log("MCP is disabled. Enable explicitly in mod settings after configuring a private token."); return; }
            if (!BridgeProtocol.ValidToken(token.Value)) { log.Warning("MCP not started: configure a private 32-128 character token (ASCII letters/digits/-/_)."); return; }
            try
            {
                host = new GameObject("ProbablyStolenMcp.LocalBridge");
                UnityEngine.Object.DontDestroyOnLoad(host);
                var component = host.AddComponent<BridgeBehaviour>();
                component.StartServer(port.Value, token.Value, log);
                log.Log("MCP local bridge listening on 127.0.0.1:" + port.Value + ". Only an authenticated local agent can operate the game.");
            }
            catch (Exception)
            {
                Stop();
                log.Error("MCP local bridge failed to start. Check whether the configured port is already in use.");
            }
        }

        void Stop()
        {
            if (host == null) return;
            var component = host.GetComponent<BridgeBehaviour>();
            if (component != null) component.StopServer();
            UnityEngine.Object.Destroy(host);
            host = null;
        }

        public void OnDisable()
        {
            ModHook.OnModConfigApplied -= ConfigApplied;
            Stop();
        }
    }

    public sealed class BridgeBehaviour : MonoBehaviour
    {
        LoopbackServer server;
        GameAdapter adapter;
        ModLog log;

        public void StartServer(int port, string token, ModLog log)
        {
            this.log = log;
            adapter = new GameAdapter();
            server = new LoopbackServer(port, new BridgeProtocol(token, adapter.Execute));
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += SceneChanged;
        }

        void SceneChanged(UnityEngine.SceneManagement.Scene before, UnityEngine.SceneManagement.Scene after) => adapter?.Reset();

        void Update()
        {
            try { server?.Tick(); }
            catch (Exception)
            {
                // Fail closed; do not leave repeated frame exceptions or pending commands behind.
                StopServer();
                log?.Error("MCP bridge stopped after a transport error. Apply mod settings to restart.");
            }
        }

        public void StopServer()
        {
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= SceneChanged;
            server?.Dispose();
            server = null;
            adapter?.Reset();
            adapter = null;
        }
        void OnDisable() => StopServer();
        void OnDestroy() => StopServer();
        void OnApplicationQuit() => StopServer();
    }
}
