using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aeroverra.StreamDeck.NestControl.Services.Nest.Models
{
    internal sealed class CommandBody
    {
        [JsonProperty("command")]
        public string Command { get; set; } = string.Empty;

        [JsonProperty("params")]
        public JObject Params { get; set; } = new JObject();
    }
}
