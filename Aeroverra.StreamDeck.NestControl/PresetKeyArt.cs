using System.Net;
using System.Text;
using Aeroverra.StreamDeck.NestControl.Services.Nest.Models;

namespace Aeroverra.StreamDeck.NestControl
{
    internal enum PresetKeyFooter
    {
        Normal,
        HoldHint,
        Applied,
        Message
    }

    internal static class PresetKeyArt
    {
        private const int KeySize = 72;
        private const int ActionBandTop = 50;
        private const int MaxMessageLength = 24;
        private const string FontStack = "-apple-system, BlinkMacSystemFont, 'SF Pro Display', 'Helvetica Neue', Arial, sans-serif";
        private const string ArmedAccent = "#FF9F0A";
        private const string SuccessAccent = "#30D158";

        internal static string ToDataUri(
            decimal temperature,
            int selectedIndex,
            int presetCount,
            ThermostatMode mode,
            PresetKeyFooter footer,
            string? message = null)
        {
            var normalizedIndex = NormalizeSelectedIndex(selectedIndex, presetCount);
            var svg = BuildSvg(temperature, normalizedIndex, presetCount, mode, footer, message);
            return $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
        }

        private static int NormalizeSelectedIndex(int selectedIndex, int presetCount)
        {
            if (presetCount <= 0)
            {
                return 0;
            }

            return ((selectedIndex % presetCount) + presetCount) % presetCount;
        }

        private static string BuildSvg(
            decimal temperature,
            int selectedIndex,
            int presetCount,
            ThermostatMode mode,
            PresetKeyFooter footer,
            string? message)
        {
            return footer switch
            {
                PresetKeyFooter.Message => BuildMessageSvg(message),
                PresetKeyFooter.HoldHint => BuildActionStateSvg(
                    mode,
                    temperature,
                    ArmedAccent,
                    "HOLD",
                    BuildHintOutline(ArmedAccent)),
                PresetKeyFooter.Applied => BuildActionStateSvg(
                    mode,
                    temperature,
                    SuccessAccent,
                    "SET",
                    string.Empty),
                _ => BuildNormalSvg(temperature, selectedIndex, presetCount, mode)
            };
        }

        private static string BuildMessageSvg(string? message)
        {
            var label = TruncateMessage(message);
            return $"""
                <svg width="{KeySize}" height="{KeySize}" viewBox="0 0 {KeySize} {KeySize}" xmlns="http://www.w3.org/2000/svg">
                  <rect width="{KeySize}" height="{KeySize}" fill="#1C1C1E"/>
                  <text x="36" y="40" text-anchor="middle" font-family="{FontStack}" font-size="12" font-weight="500" fill="#AEAEB2">{Escape(label)}</text>
                </svg>
                """;
        }

        private static string BuildNormalSvg(decimal temperature, int selectedIndex, int presetCount, ThermostatMode mode)
        {
            var (top, bottom) = ModeGradient(mode);
            var tempLabel = FormatTemperature(temperature);

            return $"""
                <svg width="{KeySize}" height="{KeySize}" viewBox="0 0 {KeySize} {KeySize}" xmlns="http://www.w3.org/2000/svg">
                  <defs>
                    <linearGradient id="bg" x1="36" y1="0" x2="36" y2="{KeySize}" gradientUnits="userSpaceOnUse">
                      <stop offset="0%" stop-color="{top}"/>
                      <stop offset="100%" stop-color="{bottom}"/>
                    </linearGradient>
                  </defs>
                  <rect width="{KeySize}" height="{KeySize}" fill="url(#bg)"/>
                  <text x="36" y="42" text-anchor="middle" font-family="{FontStack}" font-size="32" font-weight="700" fill="#FFFFFF">{Escape(tempLabel)}</text>
                  {BuildPositionIndicator(selectedIndex, presetCount)}
                </svg>
                """;
        }

        private static string BuildActionStateSvg(
            ThermostatMode mode,
            decimal temperature,
            string bandColor,
            string actionLabel,
            string outline)
        {
            var (top, bottom) = ModeGradient(mode);
            var tempLabel = FormatTemperature(temperature);
            var bandHeight = KeySize - ActionBandTop;

            return $"""
                <svg width="{KeySize}" height="{KeySize}" viewBox="0 0 {KeySize} {KeySize}" xmlns="http://www.w3.org/2000/svg">
                  <defs>
                    <linearGradient id="bg" x1="36" y1="0" x2="36" y2="{ActionBandTop}" gradientUnits="userSpaceOnUse">
                      <stop offset="0%" stop-color="{top}"/>
                      <stop offset="100%" stop-color="{bottom}"/>
                    </linearGradient>
                  </defs>
                  <rect x="0" y="0" width="{KeySize}" height="{ActionBandTop}" fill="url(#bg)"/>
                  <rect x="0" y="{ActionBandTop}" width="{KeySize}" height="{bandHeight}" fill="{bandColor}"/>
                  <text x="36" y="32" text-anchor="middle" font-family="{FontStack}" font-size="28" font-weight="700" fill="#FFFFFF">{Escape(tempLabel)}</text>
                  <text x="36" y="64" text-anchor="middle" font-family="{FontStack}" font-size="16" font-weight="800" fill="#FFFFFF">{Escape(actionLabel)}</text>
                  {outline}
                </svg>
                """;
        }

        private static string BuildHintOutline(string color) =>
            $"""<rect x="2" y="2" width="68" height="68" rx="10" fill="none" stroke="{color}" stroke-width="3"/>""";

        private static string BuildPositionIndicator(int selectedIndex, int presetCount)
        {
            if (presetCount <= 1)
            {
                return string.Empty;
            }

            if (presetCount > 6)
            {
                return $"""<text x="36" y="60" text-anchor="middle" font-family="{FontStack}" font-size="11" font-weight="600" fill="#FFFFFF" fill-opacity="0.7">{selectedIndex + 1}/{presetCount}</text>""";
            }

            const double spacing = 7;
            var width = (presetCount - 1) * spacing;
            var startX = 36 - (width / 2);
            const int y = 58;

            var dots = new StringBuilder();
            for (var i = 0; i < presetCount; i++)
            {
                var x = startX + (i * spacing);
                var opacity = i == selectedIndex ? 1.0 : 0.35;
                var radius = i == selectedIndex ? 2.8 : 2;
                dots.Append(FormattableString.Invariant(
                    $"""<circle cx="{x:F1}" cy="{y}" r="{radius}" fill="#FFFFFF" fill-opacity="{opacity:F2}"/>"""));
            }

            return dots.ToString();
        }

        private static string FormatTemperature(decimal temperature) => $"{temperature:F0}°";

        private static string TruncateMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            var trimmed = message.Trim();
            return trimmed.Length <= MaxMessageLength
                ? trimmed
                : trimmed[..MaxMessageLength];
        }

        private static (string Top, string Bottom) ModeGradient(ThermostatMode mode) => mode switch
        {
            ThermostatMode.HEAT => ("#FF9F0A", "#D9480F"),
            ThermostatMode.COOL => ("#5EB0E5", "#2563A8"),
            ThermostatMode.HEATCOOL => ("#BF5AF2", "#5E5CE6"),
            _ => ("#3A3A3C", "#1C1C1E")
        };

        private static string Escape(string value) => WebUtility.HtmlEncode(value);
    }
}
