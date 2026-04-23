using System;
using System.Text.Json.Serialization;

namespace SmartWatchProj.Models
{
    public class VitalMeasurement
    {
        public int Id { get; set; }
        public int EmployeeId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public double HeartRate { get; set; }
        public double Saturation { get; set; }
        public string? EcgData { get; set; }
        public double ActivityLevel { get; set; }

        public double BloodPressureSystolic { get; set; }
        public double BloodPressureDiastolic { get; set; }
        public double Temperature { get; set; }
        public double Glucose { get; set; }
        public double Cholesterol { get; set; }
        public double AlcoholLevel { get; set; }

        [JsonIgnore]
        public bool HasAlcoholValue { get; set; }

        [JsonIgnore]
        public string AlcoholAssessmentSource { get; set; } = "missing";

        public string? Diagnosis { get; set; }
        public string? Recommendation { get; set; } // "Зелёный", "Жёлтый", "Красный"
    }
}
