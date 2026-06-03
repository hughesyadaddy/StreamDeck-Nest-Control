using Aeroverra.StreamDeck.NestControl.Services.Nest.Models;
using Google.Apis.SmartDeviceManagement.v1.Data;

namespace Aeroverra.StreamDeck.NestControl.Services.Nest.Extensions
{
    public static class GoogleHomeEnterpriseSdmV1DeviceExtensions
    {
        public static bool IsThermostat(this GoogleHomeEnterpriseSdmV1Device device)
        {
            return device.Type == NestConstants.DEVICE_TYPE_THERMOSTAT;
        }

        public static DeviceInfoTrait GetDeviceInfo(this GoogleHomeEnterpriseSdmV1Device device)
        {
            return device.Traits.GetTrait<DeviceInfoTrait>(NestConstants.TRAIT_DEVICE_INFO);
        }

        public static ThermostatModeTrait GetThermostatMode(this GoogleHomeEnterpriseSdmV1Device device)
        {
            return device.Traits.GetTrait<ThermostatModeTrait>(NestConstants.TRAIT_THERMOSTAT_MODE);
        }

        public static bool TryGetThermostatMode(this GoogleHomeEnterpriseSdmV1Device device, out ThermostatMode mode)
        {
            if (device.Traits.TryGetTrait<ThermostatModeTrait>(NestConstants.TRAIT_THERMOSTAT_MODE, out var trait))
            {
                mode = trait.Mode;
                return true;
            }

            mode = ThermostatMode.OFF;
            return false;
        }

        public static ThermostatSetpointTrait GetThermostatSetPoint(this GoogleHomeEnterpriseSdmV1Device device)
        {
            return device.Traits.GetTrait<ThermostatSetpointTrait>(NestConstants.TRAIT_THERMOSTAT_SETPOINT);
        }

        public static TemperatureTrait GetThermostatTemperature(this GoogleHomeEnterpriseSdmV1Device device)
        {
            return device.Traits.GetTrait<TemperatureTrait>(NestConstants.TRAIT_THERMOSTAT_Temperature);
        }

        public static TemperatureScale GetTemperatureScale(this GoogleHomeEnterpriseSdmV1Device device)
        {
            if (device.Traits.TryGetTrait<SettingsTrait>(NestConstants.TRAIT_SETTINGS, out var settings)
                && Enum.TryParse(settings.TemperatureScale, ignoreCase: true, out TemperatureScale scale))
            {
                return scale;
            }

            return TemperatureScale.FAHRENHEIT;
        }

        public static string GetThermostatRenderedSetPoint(this GoogleHomeEnterpriseSdmV1Device device, TemperatureScale scale)
        {
            var mode = device.GetThermostatMode().Mode;
            var setPoint = device.GetThermostatSetPoint();
            if (scale == TemperatureScale.FAHRENHEIT)
            {
                if (mode == ThermostatMode.COOL)
                {
                    return setPoint.CoolCelsius.ToFahrenheit().ToString("F0");
                }
                else if (mode == ThermostatMode.HEAT)
                {
                    return setPoint.HeatCelsius.ToFahrenheit().ToString("F0");
                }
                else if (mode == ThermostatMode.HEATCOOL)
                {
                    return $"{setPoint.HeatCelsius.ToFahrenheit().ToString("F0")}-{setPoint.CoolCelsius.ToFahrenheit().ToString("F0")}";
                }
            }
            else
            {
                if (mode == ThermostatMode.COOL)
                {
                    return setPoint.CoolCelsius.ToString("F0");
                }
                else if (mode == ThermostatMode.HEAT)
                {
                    return setPoint.HeatCelsius.ToString("F0");
                }
                else if (mode == ThermostatMode.HEATCOOL)
                {
                    return $"{setPoint.HeatCelsius.ToString("F0")}-{setPoint.CoolCelsius.ToString("F0")}";
                }
            }
            return "Err";
        }

        public static string GetThermostatRenderedTemperature(this GoogleHomeEnterpriseSdmV1Device device, TemperatureScale scale)
        {
            var temp = device.GetThermostatTemperature();
            if (scale == TemperatureScale.FAHRENHEIT)
            {
                return $"{temp.AmbientTemperatureCelsius.ToFahrenheit().ToString("F0")}";
            }
            return $"{temp.AmbientTemperatureCelsius.ToString("F0")}";
        }
    }
}
