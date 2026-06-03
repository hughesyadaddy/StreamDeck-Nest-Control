using Aeroverra.StreamDeck.Client;
using Aeroverra.StreamDeck.Client.Events;
using Aeroverra.StreamDeck.NestControl.Models;
using Aeroverra.StreamDeck.NestControl.Services.Nest;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Aeroverra.StreamDeck.NestControl.Services
{
    internal sealed class CoreWorker : BackgroundService, IAsyncDisposable
    {
        private const string OAuthRedirectUri = "http://localhost:20777/";
        private const string OAuthFailureResponse = "Authentication failed. Verify Nest Control settings in Stream Deck, then run Setup again.";
        private const string OAuthSuccessResponse = "Success! You can now close this window.";

        private readonly ILogger<CoreWorker> _logger;
        private readonly EventManager _eventsManager;
        private readonly IElgatoDispatcher _dispatcher;
        private readonly GlobalSettings _globalSettings;
        private readonly NestService _nestService;
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();

        private string _lastContext = string.Empty;
        private string _lastUuid = string.Empty;
        private Task? _listenerTask;
        private bool _isRunning;
        private bool _disposed;

        public CoreWorker(
            ILogger<CoreWorker> logger,
            EventManager eventsManager,
            IElgatoDispatcher dispatcher,
            GlobalSettings globalSettings,
            NestService nestService)
        {
            _logger = logger;
            _eventsManager = eventsManager;
            _dispatcher = dispatcher;
            _globalSettings = globalSettings;
            _nestService = nestService;
            _eventsManager.OnSendToPlugin += OnSendToPlugin;
            _eventsManager.OnDidReceiveGlobalSettings += OnDidReceiveGlobalSettings;
            _nestService.OnConnected += OnNestConnected;
        }

        private void OnNestConnected(object? sender, EventArgs e)
        {
            _isRunning = true;
            PublishDeviceListToPropertyInspector();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }

            await _lifetime.CancelAsync();
            await _nestService.StopAsync();
        }

        private void OnSendToPlugin(object? sender, SendToPluginEvent e)
        {
            _lastContext = e.Context;
            _lastUuid = e.Action;

            if (e.payload["Reset"]?.ToObject<bool>() == true)
            {
                Reset();
            }

            if (e.payload["Setup"]?.ToObject<bool>() == true)
            {
                Setup();
            }

            if (string.Equals(e.payload["property_inspector"]?.ToString(), "propertyInspectorConnected", StringComparison.Ordinal))
            {
                _ = RefreshDevicesForPropertyInspectorAsync();
            }
        }

        private async Task RefreshDevicesForPropertyInspectorAsync()
        {
            if (_disposed)
            {
                return;
            }

            if (_nestService.Devices.Count == 0 && _globalSettings.Setup == true)
            {
                try
                {
                    await ConnectFromStoredCredentialsAsync(_lifetime.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not reconnect to Nest while property inspector was opened.");
                }
            }

            PublishDeviceListToPropertyInspector();
        }

        private void PublishDeviceListToPropertyInspector()
        {
            var piDevices = PIDevice.GetList(_nestService.Devices);
            _globalSettings.PiDevices = JsonConvert.SerializeObject(piDevices);
            _globalSettings.SubscriptionId = _nestService.SubscriptionId;

            _logger.LogInformation(
                "Publishing {DeviceCount} thermostat(s) to property inspector: {DeviceNames}",
                piDevices.Count,
                piDevices.Count == 0
                    ? "(none)"
                    : string.Join(", ", piDevices.Select(d => d.DisplayName)));

            _ = _dispatcher.SetGlobalSettingsAsync(_globalSettings);

            if (!string.IsNullOrEmpty(_lastContext))
            {
                _dispatcher.SendToPropertyInspector(_lastContext, _lastUuid, new { Update = true });
            }
        }

        private async void OnDidReceiveGlobalSettings(object? sender, DidReceiveGlobalSettingsEvent e)
        {
            if (_disposed)
            {
                return;
            }

            var entered = false;
            try
            {
                await _connectLock.WaitAsync(_lifetime.Token);
                entered = true;
                await Task.Delay(1000, _lifetime.Token);

                if (!_isRunning && _globalSettings.Setup == true)
                {
                    _logger.LogInformation("Auto-connecting to Nest using stored credentials.");
                    await ConnectFromStoredCredentialsAsync(_lifetime.Token);
                }
                else
                {
                    _logger.LogInformation(
                        "Skipping Nest auto-connect. IsRunning={IsRunning}, Setup={Setup}",
                        _isRunning,
                        _globalSettings.Setup);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect Nest service from stored credentials.");
            }
            finally
            {
                if (entered)
                {
                    _connectLock.Release();
                }
            }
        }

        private async Task ConnectFromStoredCredentialsAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_globalSettings.ProjectId)
                || string.IsNullOrWhiteSpace(_globalSettings.CloudProjectId)
                || string.IsNullOrWhiteSpace(_globalSettings.ClientId)
                || string.IsNullOrWhiteSpace(_globalSettings.ClientSecret)
                || string.IsNullOrWhiteSpace(_globalSettings.RefreshToken)
                || string.IsNullOrWhiteSpace(_globalSettings.SubscriptionId))
            {
                _logger.LogWarning("Stored Nest credentials are incomplete; skipping auto-connect.");
                return;
            }

            await _nestService.ConnectWithRefreshToken(
                _globalSettings.ProjectId,
                _globalSettings.CloudProjectId,
                _globalSettings.ClientId,
                _globalSettings.ClientSecret,
                _globalSettings.RefreshToken,
                _globalSettings.SubscriptionId,
                cancellationToken);
        }

        private async Task ListenForCallbackAsync()
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(OAuthRedirectUri);
            listener.Start();

            while (!_lifetime.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(_lifetime.Token);
                _ = ProcessRequestAsync(context, _lifetime.Token);
            }
        }

        private void Reset()
        {
            _globalSettings.Setup = false;
            _globalSettings.PiDevices = null;
            _dispatcher.SetGlobalSettings(_globalSettings);
            _dispatcher.SendToPropertyInspector(_lastContext, _lastUuid, new { Update = true });
        }

        private void Setup()
        {
            _dispatcher.GetGlobalSettings();
            if (string.IsNullOrWhiteSpace(_globalSettings.ProjectId) || string.IsNullOrWhiteSpace(_globalSettings.ClientId))
            {
                _logger.LogWarning("ProjectId or ClientId is not set in global settings. Cannot start setup.");
                return;
            }

            _listenerTask ??= ListenForCallbackAsync();

            var url = NestService.GetAccountLinkUrl(_globalSettings.ProjectId, _globalSettings.ClientId, OAuthRedirectUri.TrimEnd('/'));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                url = url.Replace("&", "^&");
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext httpContext, CancellationToken cancellationToken)
        {
            try
            {
                var code = httpContext.Request.QueryString["code"];
                var scope = httpContext.Request.QueryString["scope"];
                var responseString = OAuthFailureResponse;

                if (!string.IsNullOrWhiteSpace(code))
                {
                    try
                    {
                        await _nestService.ConnectWithCode(
                            _globalSettings.ProjectId!,
                            _globalSettings.CloudProjectId!,
                            _globalSettings.ClientId!,
                            _globalSettings.ClientSecret!,
                            OAuthRedirectUri.TrimEnd('/'),
                            code,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "OAuth code exchange failed.");
                        await Communication.LogAsync(LogLevel.Critical, ex.ToString());
                    }
                }

                if (_nestService.RefreshToken is not null)
                {
                    _globalSettings.Code = code;
                    _globalSettings.Scope = scope;
                    _globalSettings.Setup = true;
                    _globalSettings.RefreshToken = _nestService.RefreshToken;
                    _globalSettings.SubscriptionId = _nestService.SubscriptionId;
                    _dispatcher.SetGlobalSettings(_globalSettings);
                    await Communication.MetricsAsync();
                    responseString = OAuthSuccessResponse;
                }
                else if (string.IsNullOrWhiteSpace(code))
                {
                    await Communication.LogAsync(LogLevel.Critical, "OAuth callback did not include an authorization code.");
                }

                await WriteResponseAsync(httpContext, responseString);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OAuth callback processing failed.");
                await WriteResponseAsync(httpContext, OAuthFailureResponse);
            }
        }

        private static async Task WriteResponseAsync(HttpListenerContext httpContext, string responseString)
        {
            if (!httpContext.Response.OutputStream.CanWrite)
            {
                return;
            }

            var response = httpContext.Response;
            response.StatusCode = 200;
            var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.OutputStream.Close();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _eventsManager.OnSendToPlugin -= OnSendToPlugin;
            _eventsManager.OnDidReceiveGlobalSettings -= OnDidReceiveGlobalSettings;
            _nestService.OnConnected -= OnNestConnected;
            await _lifetime.CancelAsync();
            _lifetime.Dispose();
            _connectLock.Dispose();
        }
    }
}
