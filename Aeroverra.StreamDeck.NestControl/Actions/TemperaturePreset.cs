using System.Globalization;
using Aeroverra.StreamDeck.Client.Actions;
using Aeroverra.StreamDeck.NestControl.Services.Nest;
using Aeroverra.StreamDeck.NestControl.Services.Nest.Extensions;
using Aeroverra.StreamDeck.NestControl.Services.Nest.Models;
using Aeroverra.StreamDeck.NestControl.Services.Presets;
using Google.Apis.SmartDeviceManagement.v1.Data;
using Newtonsoft.Json.Linq;

namespace Aeroverra.StreamDeck.NestControl.Actions
{
    [PluginActionAttribute("aeroverra.streamdeck.nestcontrol.temperaturepreset")]
    public sealed class TemperaturePreset : ActionBase, IAsyncDisposable
    {
        private static readonly TimeSpan HoldHintDuration = TimeSpan.FromMilliseconds(850);
        private static readonly TimeSpan AppliedFlashDuration = TimeSpan.FromMilliseconds(900);

        private readonly NestService _nestService;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly PresetDisplayAnimator _displayAnimator = new();
        private readonly ILogger<TemperaturePreset> _logger;

        private GoogleHomeEnterpriseSdmV1Device? _thermostat;
        private DateTime _keyDownAt = DateTime.MinValue;
        private bool _disposed;

        public TemperaturePreset(ILogger<TemperaturePreset> logger, NestService nestService)
        {
            _logger = logger;
            _nestService = nestService;
            _nestService.OnConnected += OnNestConnected;
            _nestService.OnDeviceUpdated += OnNestDeviceUpdated;
        }

        private string DeviceName =>
            Context.Settings.TryGetValue("device", out var deviceToken)
                ? deviceToken?.ToString() ?? string.Empty
                : string.Empty;

        public override async Task WillAppearAsync()
        {
            await RunExclusiveAsync(() => RefreshAndRenderNormalAsync());
        }

        public override async Task DidReceiveSettingsAsync(JObject settings)
        {
            await RunExclusiveAsync(async () =>
            {
                RefreshThermostat();
                await NormalizePresetIndexAsync();
                await RenderPresetAsync(PresetKeyFooter.Normal);
            });
        }

        public override Task KeyDownAsync(int userDesiredState)
        {
            if (!_disposed)
            {
                _keyDownAt = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }

        public override async Task KeyUpAsync(int userDesiredState)
        {
            await RunExclusiveAsync(async () =>
            {
                if (!TryGetConfiguredDevice(out var deviceMissingMessage))
                {
                    await RenderMessageAsync(deviceMissingMessage);
                    return;
                }

                var presets = ParsePresets();
                if (presets.Count == 0)
                {
                    await RenderMessageAsync("Add presets");
                    return;
                }

                var holdDuration = TimeSpan.FromSeconds(PresetConfiguration.GetHoldSeconds(Context.Settings));
                var heldFor = GetKeyHoldDuration();
                _keyDownAt = DateTime.MinValue;

                if (heldFor >= holdDuration)
                {
                    await ApplySelectedPresetAsync(presets);
                    return;
                }

                if (presets.Count > 1)
                {
                    await AdvancePresetIndexAsync(presets.Count);
                }

                await FlashHoldHintAsync(presets);
            });
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _nestService.OnConnected -= OnNestConnected;
            _nestService.OnDeviceUpdated -= OnNestDeviceUpdated;
            await _displayAnimator.DisposeAsync();
            _gate.Dispose();
        }

        private void OnNestConnected(object? sender, EventArgs e) =>
            ScheduleExclusiveWork(RefreshAndRenderNormalAsync);

        private void OnNestDeviceUpdated(object? sender, GoogleHomeEnterpriseSdmV1Device device)
        {
            if (_disposed || !string.Equals(device.Name, DeviceName, StringComparison.Ordinal))
            {
                return;
            }

            ScheduleExclusiveWork(async () =>
            {
                _thermostat = device;
                await RenderPresetAsync(PresetKeyFooter.Normal);
            });
        }

        private void ScheduleExclusiveWork(Func<Task> operation)
        {
            if (_disposed)
            {
                return;
            }

            _ = RunExclusiveAsync(operation);
        }

        private async Task RunExclusiveAsync(Func<Task> operation)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _gate.WaitAsync(_displayAnimator.ShutdownToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (!_disposed)
                {
                    await operation();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Temperature preset action failed for device {DeviceName}.", DeviceName);
                await Dispatcher.ShowAlertAsync();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task RefreshAndRenderNormalAsync()
        {
            RefreshThermostat();
            await RenderPresetAsync(PresetKeyFooter.Normal);
        }

        private bool TryGetConfiguredDevice(out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(DeviceName))
            {
                errorMessage = "Select device";
                return false;
            }

            if (_thermostat is not null)
            {
                errorMessage = string.Empty;
                return true;
            }

            RefreshThermostat();
            if (_thermostat is not null)
            {
                errorMessage = string.Empty;
                return true;
            }

            errorMessage = "Not found";
            return false;
        }

        private TimeSpan GetKeyHoldDuration() =>
            _keyDownAt == DateTime.MinValue ? TimeSpan.Zero : DateTime.UtcNow - _keyDownAt;

        private async Task AdvancePresetIndexAsync(int presetCount)
        {
            var nextIndex = (PresetConfiguration.GetPresetIndex(Context.Settings, presetCount) + 1) % presetCount;
            await PersistPresetIndexAsync(nextIndex);
        }

        private async Task ApplySelectedPresetAsync(IReadOnlyList<decimal> presets)
        {
            if (_thermostat is null)
            {
                await RenderMessageAsync("Not found");
                return;
            }

            if (!_thermostat.TryGetThermostatMode(out var mode))
            {
                await RenderMessageAsync("Unavailable");
                return;
            }

            var preset = presets[PresetConfiguration.GetPresetIndex(Context.Settings, presets.Count)];
            var scale = PresetConfiguration.ResolveScale(Context.Settings, _thermostat);

            if (mode == ThermostatMode.OFF)
            {
                var enabled = await _nestService.SetMode(_thermostat, ThermostatMode.HEAT);
                if (!enabled)
                {
                    await Dispatcher.ShowAlertAsync();
                    return;
                }

                mode = ThermostatMode.HEAT;
            }

            var (heatCelsius, coolCelsius) = PresetConfiguration.ToCelsiusTargets(mode, preset, scale);
            var applied = await _nestService.SetTemp(_thermostat, heatCelsius, coolCelsius);
            if (!applied)
            {
                await Dispatcher.ShowAlertAsync();
                return;
            }

            await Dispatcher.ShowOkAsync();
            await FlashAppliedAsync(presets);
        }

        private async Task FlashHoldHintAsync(IReadOnlyList<decimal> presets)
        {
            await _displayAnimator.RunFlashAsync(
                HoldHintDuration,
                ct => RenderPresetAsync(PresetKeyFooter.HoldHint, presets, ct),
                ct => RenderPresetAsync(PresetKeyFooter.Normal, presets, ct));
        }

        private async Task FlashAppliedAsync(IReadOnlyList<decimal> presets)
        {
            await _displayAnimator.RunFlashAsync(
                AppliedFlashDuration,
                ct => RenderPresetAsync(PresetKeyFooter.Applied, presets, ct),
                ct => RenderPresetAsync(PresetKeyFooter.Normal, presets, ct));
        }

        private async Task RenderPresetAsync(
            PresetKeyFooter footer,
            IReadOnlyList<decimal>? presets = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            presets ??= ParsePresets();
            if (presets.Count == 0)
            {
                await RenderMessageAsync("Add presets", cancellationToken);
                return;
            }

            var index = PresetConfiguration.GetPresetIndex(Context.Settings, presets.Count);
            var preset = presets[index];
            var mode = ResolveRenderMode(_thermostat);

            var image = PresetKeyArt.ToDataUri(preset, index, presets.Count, mode, footer);
            await Dispatcher.SetStateAsync(0);
            await Dispatcher.SetImageAsync(image);
            await Dispatcher.SetTitleAsync(string.Empty);
        }

        private async Task RenderMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var image = PresetKeyArt.ToDataUri(0, 0, 1, ThermostatMode.OFF, PresetKeyFooter.Message, message);
            await Dispatcher.SetStateAsync(0);
            await Dispatcher.SetImageAsync(image);
            await Dispatcher.SetTitleAsync(string.Empty);
        }

        private IReadOnlyList<decimal> ParsePresets() =>
            PresetConfiguration.ParsePresets(
                Context.Settings,
                PresetConfiguration.ResolveScale(Context.Settings, _thermostat),
                _logger);

        private async Task NormalizePresetIndexAsync()
        {
            var presets = ParsePresets();
            if (presets.Count == 0)
            {
                return;
            }

            var normalized = PresetConfiguration.GetPresetIndex(Context.Settings, presets.Count);
            if (!Context.Settings.TryGetValue("presetIndex", out var currentToken)
                || !int.TryParse($"{currentToken}", NumberStyles.Integer, CultureInfo.InvariantCulture, out var current)
                || current != normalized)
            {
                await PersistPresetIndexAsync(normalized);
            }
        }

        private async Task PersistPresetIndexAsync(int index)
        {
            var settings = Context.Settings.DeepClone() as JObject ?? new JObject();
            settings["presetIndex"] = index;
            Context.Settings["presetIndex"] = index;
            await Dispatcher.SetSettingsAsync(settings);
        }

        private void RefreshThermostat()
        {
            if (string.IsNullOrWhiteSpace(DeviceName))
            {
                _thermostat = null;
                return;
            }

            _thermostat = _nestService.Devices.FirstOrDefault(x => x.Name == DeviceName);
        }

        private static ThermostatMode ResolveRenderMode(GoogleHomeEnterpriseSdmV1Device? thermostat) =>
            thermostat is not null && thermostat.TryGetThermostatMode(out var mode)
                ? mode
                : ThermostatMode.OFF;
    }
}
