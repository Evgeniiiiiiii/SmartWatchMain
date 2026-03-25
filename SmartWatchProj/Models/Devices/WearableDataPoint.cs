using System.Text.Json.Serialization;

namespace SmartWatchProj.Models.Devices
{
    public sealed class WearableDataPoint
    {
        [JsonPropertyName("HR")]
        public int Hr { get; set; }

        [JsonPropertyName("SpO2")]
        public int SpO2 { get; set; }

        [JsonPropertyName("Activity")]
        public int Activity { get; set; }
    }
}
