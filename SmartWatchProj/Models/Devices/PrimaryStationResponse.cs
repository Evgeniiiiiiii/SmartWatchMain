using System.Text.Json.Serialization;

namespace SmartWatchProj.Models.Devices
{
    public sealed class PrimaryStationResponse
    {
        [JsonPropertyName("Temp")]
        public double Temp { get; set; }

        [JsonPropertyName("Alco")]
        public double Alco { get; set; }

        [JsonPropertyName("SYS")]
        public int Sys { get; set; }

        [JsonPropertyName("DAD")]
        public int Dad { get; set; }
    }
}
