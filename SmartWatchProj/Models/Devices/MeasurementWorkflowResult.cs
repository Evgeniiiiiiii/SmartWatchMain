using System.Collections.Generic;
using System.Linq;
using SmartWatchProj.Models;

namespace SmartWatchProj.Models.Devices
{
    public sealed class MeasurementWorkflowResult
    {
        public VitalMeasurement Measurement { get; set; } = new();
        public List<DeviceOperationResult> DeviceResults { get; } = new();
        public List<string> Issues { get; } = new();

        public bool HasAnyRealData => DeviceResults.Any(result => result.Success);
    }
}
