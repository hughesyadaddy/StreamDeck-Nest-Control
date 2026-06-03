using System.Globalization;
using Aeroverra.StreamDeck.NestControl.Services.Nest.Extensions;
using Aeroverra.StreamDeck.NestControl.Services.Nest.Models;
using Google.Apis.SmartDeviceManagement.v1.Data;
using Newtonsoft.Json.Linq;

namespace Aeroverra.StreamDeck.NestControl.Services.Presets
{
    internal static class PresetConfiguration
    {
        internal const int MaxPresetCount = 4;
        internal const double DefaultHoldSeconds = 1.0;

        private const decimal MinHeatCoolSpreadF = 3m;
        private const decimal MinHeatCoolSpreadC = 2m;
        private const decimal MinPresetFahrenheit = 40m;
        private const decimal MaxPresetFahrenheit = 95m;
        private const decimal MinPresetCelsius = 4m;
        private const decimal MaxPresetCelsius = 35m;

        internal static IReadOnlyList<decimal> ParsePresets(JObject settings, TemperatureScale scale, ILogger logger)
        {
            if (!settings.TryGetValue("presets", out var presetsToken))
            {
                return Array.Empty<decimal>();
            }

            var raw = presetsToken?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<decimal>();
            }

            var values = new List<decimal>(MaxPresetCount);
            foreach (var segment in raw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (values.Count >= MaxPresetCount)
                {
                    break;
                }

                var trimmed = segment.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                {
                    logger.LogWarning("Ignoring invalid preset value {PresetValue}.", trimmed);
                    continue;
                }

                values.Add(ClampPreset(value, scale));
            }

            return values;
        }

        internal static int GetPresetIndex(JObject settings, int presetCount)
        {
            if (presetCount <= 0)
            {
                return 0;
            }

            if (settings.TryGetValue("presetIndex", out var indexToken)
                && int.TryParse($"{indexToken}", NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                return ((index % presetCount) + presetCount) % presetCount;
            }

            return 0;
        }

        internal static double GetHoldSeconds(JObject settings)
        {
            if (settings.TryGetValue("holdSeconds", out var holdToken)
                && double.TryParse($"{holdToken}", NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return seconds;
            }

            return DefaultHoldSeconds;
        }

        internal static TemperatureScale ResolveScale(JObject settings, GoogleHomeEnterpriseSdmV1Device? thermostat)
        {
            if (thermostat is not null)
            {
                return thermostat.GetTemperatureScale();
            }

            if (settings.TryGetValue("temperatureScale", out var scaleToken)
                && Enum.TryParse($"{scaleToken}", ignoreCase: true, out TemperatureScale savedScale))
            {
                return savedScale;
            }

            return TemperatureScale.FAHRENHEIT;
        }

        internal static (decimal HeatCelsius, decimal CoolCelsius) ToCelsiusTargets(
            ThermostatMode mode,
            decimal preset,
            TemperatureScale scale)
        {
            var clamped = ClampPreset(preset, scale);
            var spreadHalf = scale == TemperatureScale.CELSIUS
                ? MinHeatCoolSpreadC / 2m
                : MinHeatCoolSpreadF / 2m;

            decimal ToCelsius(decimal value) =>
                scale == TemperatureScale.CELSIUS ? value : value.ToCelsius();

            return mode switch
            {
                ThermostatMode.HEAT => (ToCelsius(clamped), ToCelsius(clamped)),
                ThermostatMode.COOL => (ToCelsius(clamped), ToCelsius(clamped)),
                ThermostatMode.HEATCOOL => (
                    ToCelsius(clamped - spreadHalf),
                    ToCelsius(clamped + spreadHalf)),
                _ => (ToCelsius(clamped), ToCelsius(clamped))
            };
        }

        internal static decimal ClampPreset(decimal preset, TemperatureScale scale)
        {
            var (min, max) = scale == TemperatureScale.CELSIUS
                ? (MinPresetCelsius, MaxPresetCelsius)
                : (MinPresetFahrenheit, MaxPresetFahrenheit);
            return Math.Clamp(preset, min, max);
        }
    }
}
