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
        // How long the orange "hold to set" border stays visible after we settle on a preset
        // (no more taps coming).
        private static readonly TimeSpan HoldHintDuration = TimeSpan.FromMilliseconds(3000);
        private static readonly TimeSpan AppliedFlashDuration = TimeSpan.FromMilliseconds(900);
        // Debounce: time of "no taps" after the last tap before we decide the user has settled
        // on a preset and we should reveal the orange hint.
        private static readonly TimeSpan SettleDebounce = TimeSpan.FromMilliseconds(350);
        // Anything shorter than this is treated as a tap (advance). Longer presses that release
        // before the auto-commit timer are treated as a cancelled hold (no advance, no change).
        private static readonly TimeSpan TapMaxDuration = TimeSpan.FromMilliseconds(250);

        private readonly NestService _nestService;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly PresetDisplayAnimator _displayAnimator = new();
        private readonly ILogger<TemperaturePreset> _logger;

        private GoogleHomeEnterpriseSdmV1Device? _thermostat;
        private DateTime _keyDownAt = DateTime.MinValue;
        private CancellationTokenSource? _holdCommitCts;
        private CancellationTokenSource? _settleCts;
        private bool _committedDuringHold;
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
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _keyDownAt = DateTime.UtcNow;

            // Cancel any pending "settle → orange hint" timer. We're interacting now; the
            // debounce only re-arms after the next KeyUp.
            CancelSettleTimer();

            // Cancel any orange revert that's currently scheduled. We do NOT change the visual
            // here — whatever is on screen (orange or Normal) stays put during the press so
            // the user sees a stable "I am holding what I see" experience.
            _ = _displayAnimator.CancelActiveFlashAsync();

            // Schedule auto-commit at holdDuration. If they keep holding past that mark, the
            // current preset is applied and the green confirmation flash fires.
            var oldCommit = Interlocked.Exchange(ref _holdCommitCts, null);
            oldCommit?.Cancel();
            oldCommit?.Dispose();
            var commitCts = new CancellationTokenSource();
            _holdCommitCts = commitCts;
            _committedDuringHold = false;
            var commitToken = commitCts.Token;
            var holdDuration = TimeSpan.FromSeconds(PresetConfiguration.GetHoldSeconds(Context.Settings));

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(holdDuration, commitToken);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (commitToken.IsCancellationRequested || _disposed)
                {
                    return;
                }

                await RunExclusiveAsync(async () =>
                {
                    if (commitToken.IsCancellationRequested || _disposed)
                    {
                        return;
                    }

                    var presets = ParsePresets();
                    if (presets.Count == 0)
                    {
                        return;
                    }

                    _committedDuringHold = true;
                    await ApplySelectedPresetAsync(presets);
                });
            }, commitToken);

            return Task.CompletedTask;
        }

        public override async Task KeyUpAsync(int userDesiredState)
        {
            if (_disposed)
            {
                return;
            }

            var heldFor = GetKeyHoldDuration();
            _keyDownAt = DateTime.MinValue;

            var commitCts = Interlocked.Exchange(ref _holdCommitCts, null);
            commitCts?.Cancel();
            commitCts?.Dispose();

            var committed = _committedDuringHold;
            _committedDuringHold = false;

            // Auto-commit fired during the hold. The green "DONE" flash is already on screen
            // and will revert to Normal on its own — don't restart the settle timer (the user
            // has committed; no further hint is needed).
            if (committed)
            {
                return;
            }

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

                if (heldFor < TapMaxDuration)
                {
                    // Quick tap → advance to next preset ascending. Hide any lingering orange
                    // hint immediately so the user sees they're toggling, not in confirm mode.
                    if (presets.Count > 1)
                    {
                        await AdvancePresetIndexAsync(presets.Count);
                    }
                    await _displayAnimator.CancelActiveFlashAsync();
                    await RenderPresetAsync(PresetKeyFooter.Normal, presets);
                }
                // Medium press (between tap threshold and auto-commit): cancelled hold. Do not
                // advance, do not change the visual — let whatever was on screen stay.

                // Always re-arm the settle timer. After SettleDebounce of inactivity, the
                // orange "hold to set" hint appears.
                StartSettleTimer();
            });
        }

        private void StartSettleTimer()
        {
            if (_disposed)
            {
                return;
            }

            var oldCts = Interlocked.Exchange(ref _settleCts, null);
            oldCts?.Cancel();
            oldCts?.Dispose();

            var settleCts = new CancellationTokenSource();
            _settleCts = settleCts;
            var token = settleCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SettleDebounce, token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                await RunExclusiveAsync(async () =>
                {
                    if (token.IsCancellationRequested || _disposed)
                    {
                        return;
                    }

                    var presets = ParsePresets();
                    if (presets.Count == 0)
                    {
                        return;
                    }

                    await FlashHoldHintAsync(presets);
                });
            }, token);
        }

        private void CancelSettleTimer()
        {
            var oldCts = Interlocked.Exchange(ref _settleCts, null);
            oldCts?.Cancel();
            oldCts?.Dispose();
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
            var commitCts = Interlocked.Exchange(ref _holdCommitCts, null);
            commitCts?.Cancel();
            commitCts?.Dispose();
            CancelSettleTimer();
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
                _thermostat.SetLocalThermostatMode(ThermostatMode.HEAT);
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
