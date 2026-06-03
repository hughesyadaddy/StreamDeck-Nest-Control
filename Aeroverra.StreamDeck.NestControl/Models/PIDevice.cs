using Aeroverra.StreamDeck.NestControl.Services.Nest;
using Aeroverra.StreamDeck.NestControl.Services.Nest.Extensions;
using Aeroverra.StreamDeck.NestControl.Services.Nest.Models;
using Google.Apis.SmartDeviceManagement.v1.Data;
using Newtonsoft.Json;

namespace Aeroverra.StreamDeck.NestControl.Models
{
    internal class PIDevice
    {
        private const int DeviceIdSuffixLength = 6;

        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "temperatureScale")]
        public string TemperatureScale { get; set; } = "FAHRENHEIT";

        public static List<PIDevice> GetList(IEnumerable<GoogleHomeEnterpriseSdmV1Device> devices)
        {
            var thermostats = devices
                .Where(d => d.Type == NestConstants.DEVICE_TYPE_THERMOSTAT)
                .ToList();

            var piDevices = new List<PIDevice>(thermostats.Count);
            for (var index = 0; index < thermostats.Count; index++)
            {
                var device = thermostats[index];
                piDevices.Add(new PIDevice
                {
                    Name = device.Name ?? string.Empty,
                    DisplayName = ResolveDisplayName(device, index, thermostats.Count),
                    TemperatureScale = device.GetTemperatureScale().ToString()
                });
            }

            return piDevices;
        }

        internal static string ResolveDisplayName(GoogleHomeEnterpriseSdmV1Device device, int index, int total)
        {
            if (device.Traits.TryGetTrait<DeviceInfoTrait>(NestConstants.TRAIT_DEVICE_INFO, out var info)
                && !string.IsNullOrWhiteSpace(info.CustomName))
            {
                return info.CustomName.Trim();
            }

            if (total > 1)
            {
                var suffix = GetDeviceIdSuffix(device.Name);
                return string.IsNullOrEmpty(suffix)
                    ? $"Thermostat {index + 1}"
                    : $"Thermostat {index + 1} · …{suffix}";
            }

            var singleSuffix = GetDeviceIdSuffix(device.Name);
            return string.IsNullOrEmpty(singleSuffix)
                ? "Thermostat"
                : $"Thermostat · …{singleSuffix}";
        }

        private static string GetDeviceIdSuffix(string? resourceName)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                return string.Empty;
            }

            var segments = resourceName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var deviceId = segments.Length > 0 ? segments[^1] : resourceName.Trim();
            if (deviceId.Length <= DeviceIdSuffixLength)
            {
                return deviceId;
            }

            return deviceId[^DeviceIdSuffixLength..];
        }
    }
}
