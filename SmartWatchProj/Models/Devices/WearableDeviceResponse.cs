using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SmartWatchProj.Models.Devices
{
    public sealed class WearableDeviceResponse
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("data")]
        public List<WearableDataPoint> Data { get; set; } = new();
    }
}
