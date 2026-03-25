namespace SmartWatchProj.Services.Devices
{
    public static class Esp32Commands
    {
        public const string ReadTemperature = "x1x1x";
        public const string ReadAlcohol = "x1x2x";
        public const string ReadPressure = "x1x3x";
        public const string ReadWearableData = "x2x1x";

        public static string BindWearableUser(int userId) => $"x2x{userId}x";
    }
}
