using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aeroverra.StreamDeck.NestControl.Services.Nest.Models
{
    public static class TraitExtensions
    {
        public static bool TryGetTrait<T>(this IDictionary<string, object> traits, string traitName, out T trait)
            where T : class
        {
            trait = null!;
            if (!traits.TryGetValue(traitName, out var value))
            {
                return false;
            }

            if (value is JsonElement element)
            {
                var deserialized = System.Text.Json.JsonSerializer.Deserialize<T>(element);
                if (deserialized is null)
                {
                    return false;
                }

                trait = deserialized;
                return true;
            }

            if (value is JObject jObject)
            {
                var deserialized = jObject.ToObject<T>();
                if (deserialized is null)
                {
                    return false;
                }

                trait = deserialized;
                return true;
            }

            if (value is T typedValue)
            {
                trait = typedValue;
                return true;
            }

            return false;
        }

        public static T GetTrait<T>(this IDictionary<string, object> traits, string traitName)
            where T : class
        {
            if (!traits.TryGetTrait(traitName, out T trait))
            {
                throw new KeyNotFoundException($"Device trait '{traitName}' was not found.");
            }

            return trait;
        }
    }

    public class DeviceInfoTrait
    {
        [JsonPropertyName("customName")]
        [JsonProperty("customName")]
        public string CustomName { get; set; } = string.Empty;
    }

    public class ConnectivityTrait
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
    }

    public class ThermostatModeTrait
    {
        [JsonPropertyName("mode")]
        public ThermostatMode Mode { get; set; }

        [JsonPropertyName("availableModes")]
        public string[] AvailableModes { get; set; } = Array.Empty<string>();
    }

    public enum ThermostatMode
    {
        OFF, COOL, HEAT, HEATCOOL
    }

    public class ThermostatSetpointTrait
    {
        [JsonPropertyName("heatCelsius")]
        public decimal HeatCelsius { get; set; }

        [JsonPropertyName("coolCelsius")]
        public decimal CoolCelsius { get; set; }
    }

    public class TemperatureTrait
    {
        [JsonPropertyName("ambientTemperatureCelsius")]
        public decimal AmbientTemperatureCelsius { get; set; }
    }

    public class SettingsTrait
    {
        [JsonPropertyName("temperatureScale")]
        [JsonProperty("temperatureScale")]
        public string TemperatureScale { get; set; } = "FAHRENHEIT";
    }

    public class CameraMotionTrait
    {
        [JsonPropertyName("eventSessionId")]
        public string EventSessionId { get; set; } = string.Empty;
    }

    public class CameraPersonTrait
    {
        [JsonPropertyName("eventSessionId")]
        public string EventSessionId { get; set; } = string.Empty;
    }
}
