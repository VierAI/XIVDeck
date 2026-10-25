using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Utility;
using EmbedIO;
using Swan.Logging;
using XIVDeck.FFXIVPlugin.Base;
using XIVDeck.FFXIVPlugin.Exceptions;
using XIVDeck.FFXIVPlugin.Game.Chat;
using XIVDeck.FFXIVPlugin.Resources.Localization;
using XIVDeck.FFXIVPlugin.Server.Helpers;
using XIVDeck.FFXIVPlugin.Server.Messages;
using XIVDeck.FFXIVPlugin.Server.Types;
using XIVDeck.FFXIVPlugin.UI.Windows.Nags;
using XIVDeck.FFXIVPlugin.Utils;

namespace XIVDeck.FFXIVPlugin.Server;

public class XIVDeckWebServer : IXIVDeckServer {
    private readonly PluginLogShim _logShim = new();
    private readonly XIVDeckPlugin _plugin;

    // Core web server components
    private IWebServer? _host;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    // Exposed modules
    private XIVDeckWSServer? _wsServer;

    public XIVDeckWebServer(XIVDeckPlugin plugin) {
        Logger.RegisterLogger(this._logShim);
        this._plugin = plugin;
    }

    public bool IsRunning => this._host != null && this._host.State != WebServerState.Stopped;

    public void StartServer() {
        this._cts = new CancellationTokenSource();

        var listenerMode = GetListenerMode();
        var port = this._plugin.Configuration.WebSocketPort;

        Injections.PluginLog.Information($"Starting EmbedIO server on port {port} with listener mode {listenerMode}");

        this._host = new WebServer(o => o
            .WithUrlPrefixes(GenerateUrlPrefixes(port))
            .WithMode(listenerMode)
        );

        this._wsServer = new XIVDeckWSServer("/ws");

        this._host.WithCors(origins: "file://");
        this._host.WithModule(this._wsServer);
        this._host.WithModule(new AuthModule("/"));

        this._host.StateChanged += (_, e) => {
            Injections.PluginLog.Debug($"EmbedIO server changed state to {e.NewState.ToString()}");
        };

        ConfigureErrorHandlers(this._host);
        ApiControllerWiring.Autowire(this._host);

        this._serverTask = Task.Run(async () => {
            try {
                await this._host.RunAsync(this._cts.Token);
            } catch (HttpListenerException ex) when (ex.ErrorCode is 32 or 183) {
                this.HandlePortConflict(ex);
            } catch (SocketException ex) when (ex.ErrorCode == 10048) {
                this.HandlePortConflict(ex);
            } catch (Exception ex) {
                Injections.PluginLog.Error(ex, "Error during webserver run!");
                ErrorNotifier.ShowError(UIStrings.XIVDeckWebServer_RunException, false, true, true);
            }
        }, this._cts.Token);
    }

    public void StopServer() {
        this._cts?.Cancel();
        this._serverTask?.Wait();
        this._host?.Dispose();

        Injections.PluginLog.Debug("Web server has been stopped.");
    }

    public void Dispose() {
        this.StopServer();

        Logger.UnregisterLogger(this._logShim);
        this._logShim.Dispose();

        GC.SuppressFinalize(this);
    }

    public void BroadcastMessage(BaseOutboundMessage message) {
        this._wsServer?.BroadcastMessage(message);
    }

    public void NoteMode(string contextId, bool wantsPush) {
        this._wsServer?.NoteMode(contextId, wantsPush);
    }

    public void RememberActions(string contextId, IReadOnlyList<(string Type, int Id)> actions) {
        this._wsServer?.RememberActions(contextId, actions);
    }

    public void BroadcastHotbar(IReadOnlyList<MicroHotbarSlot> slots) {
        this._wsServer?.BroadcastHotbar(slots);
    }

    public void SendHotbar(string contextId, IReadOnlyList<MicroHotbarSlot> slots) {
        this._wsServer?.SendHotbar(contextId, slots);
    }

    public void SendActionIcons(string contextId, IReadOnlyList<ActionIconDto> actions) {
        this._wsServer?.SendActionIcons(contextId, actions);
    }

    public void RefreshSubscribedActions(string? actionType) {
        this._wsServer?.RefreshSubscribedActions(actionType);
    }

    public void SendIcons(string contextId, IReadOnlyList<int> iconIds) {
        this._wsServer?.SendIcons(contextId, iconIds);
    }

    public void ForgetSentIcons() {
        this._wsServer?.ForgetSentIcons();
    }

    public void ReplaceIcons(IReadOnlyList<int> iconIds) {
        this._wsServer?.ReplaceIcons(iconIds);
    }

    public bool PushEditableChanges() {
        return this._wsServer?.PushEditableChanges() ?? false;
    }

    public void MarkIconsKnown(string contextId, IReadOnlyList<int> iconIds) {
        this._wsServer?.MarkIconsKnown(contextId, iconIds);
    }

    public void Prefetch(string? contextId, IReadOnlyList<(string Type, int Id)> actions) {
        this._wsServer?.Prefetch(contextId, actions);
    }

    private static HttpListenerMode GetListenerMode() {
        if (XIVDeckPlugin.Instance.Configuration.HttpListenerMode != null) {
            var mode = XIVDeckPlugin.Instance.Configuration.HttpListenerMode.Value;
            Injections.PluginLog.Debug($"Config explicitly set HttpListenerMode to {mode}.");
            return mode;
        }

        // If you're wondering why this is here despite the below line doing the same thing, it's legacy just in case
        // I want to swap the default listener for a specific operating system class.
        if (Util.IsWine()) {
            Injections.PluginLog.Information("Linux environment detected; using EmbedIO listener.");
            return HttpListenerMode.EmbedIO;
        }

        Injections.PluginLog.Debug("HttpListenerMode not set; using EmbedIO listener.");
        return HttpListenerMode.EmbedIO;
    }

    private static string[] GenerateUrlPrefixes(int port) {
        var prefixes = new List<string> {$"http://localhost:{port}"};

        // Add in explicit URL prefixes for v4 (and v6) because HttpListener just doesn't let us grab all traffic on an interface.
        if (NetworkUtil.HostSupportsLocalIPv4()) prefixes.Add($"http://127.0.0.1:{port}");
        if (NetworkUtil.HostSupportsLocalIPv6()) prefixes.Add($"http://[::1]:{port}");

        return prefixes.ToArray();
    }

    private static void ConfigureErrorHandlers(IWebServer server) {
        server.OnUnhandledException = (ctx, ex) => {
            // Handle known exception types first, as these can be thrown by various subsystems
            switch (ex) {
                case ActionLockedException:
                    return server.OnHttpException(ctx, HttpException.Forbidden(ex.Message, ex));

                case PlayerNotLoggedInException:
                case IllegalGameStateException:
                    return server.OnHttpException(ctx, HttpException.BadRequest(ex.Message, ex));

                case ActionNotFoundException:
                    return server.OnHttpException(ctx, HttpException.NotFound(ex.Message, ex));
            }

            // And then fallback to unknown exceptions
            Injections.PluginLog.Error(ex, "Unhandled exception while processing request: " +
                                $"{ctx.Request.HttpMethod} {ctx.Request.Url.PathAndQuery}");
            ErrorNotifier.ShowError(ex.Message, debounce: true);
            return ExceptionHandler.Default(ctx, ex);
        };

        server.OnHttpException = (ctx, ex) => {
            var inner = ex.DataObject as Exception ?? (HttpException) ex;

            Injections.PluginLog.Warning(inner, $"Got HTTP {ex.StatusCode} while processing request: " +
                                     $"{ctx.Request.HttpMethod} {ctx.Request.Url.PathAndQuery}");

            // Only show messages to users if it's a POST request (button action)
            if (ctx.Request.HttpVerb == HttpVerbs.Post) {
                ErrorNotifier.ShowError(ex.Message ?? inner.Message, true);
            }

            return HttpExceptionHandler.Default(ctx, ex);
        };
    }

    private void HandlePortConflict(Exception ex) {
        Injections.PluginLog.Warning(ex, "Port was already in use!");

        if (!UiUtil.IsMultiboxing()) {
            PortInUseNag.Show();
            return;
        }

        MultiboxNag.Show();
    }
}
