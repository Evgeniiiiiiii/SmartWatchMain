using System;
using System.Threading;
using System.Threading.Tasks;
using SmartWatchProj.Models;
using SmartWatchProj.Services.Diagnostics;

namespace SmartWatchProj.Services.Devices
{
    public interface IMeasurementProvider
    {
        string ProviderName { get; }
        Task<VitalMeasurement> CaptureAsync(int employeeId, CancellationToken cancellationToken = default);
    }

    public sealed class DiagnosticsMeasurementProvider : IMeasurementProvider
    {
        private readonly RuntimeLogStore logStore;
        private int runCounter;

        public DiagnosticsMeasurementProvider(RuntimeLogStore logStore)
        {
            this.logStore = logStore;
        }

        public string ProviderName => "Diagnostics Stub";

        public Task<VitalMeasurement> CaptureAsync(int employeeId, CancellationToken cancellationToken = default)
        {
            var template = templates[runCounter % templates.Length];
            runCounter++;

            logStore.Info("Measurements", $"Diagnostics profile selected: {template.Name}.");

            return Task.FromResult(new VitalMeasurement
            {
                EmployeeId = employeeId,
                Timestamp = DateTime.Now,
                HeartRate = template.HeartRate,
                Saturation = template.Saturation,
                EcgData = $"diagnostics:{template.Name}",
                ActivityLevel = template.ActivityLevel,
                BloodPressureSystolic = template.BloodPressureSystolic,
                BloodPressureDiastolic = template.BloodPressureDiastolic,
                Temperature = template.Temperature,
                Glucose = template.Glucose,
                Cholesterol = template.Cholesterol,
                AlcoholLevel = template.AlcoholLevel,
                Diagnosis = $"Diagnostics profile: {template.Name}"
            });
        }

        private static readonly MeasurementTemplate[] templates =
        {
            new("Норма", 76, 98, 122, 78, 36.6, 5.1, 4.8, 0.0, 2800),
            new("Внимание", 104, 94, 142, 92, 37.3, 6.4, 5.9, 0.1, 1600),
            new("Риск", 128, 89, 168, 104, 37.9, 7.8, 6.6, 0.5, 900)
        };

        private sealed record MeasurementTemplate(
            string Name,
            double HeartRate,
            double Saturation,
            double BloodPressureSystolic,
            double BloodPressureDiastolic,
            double Temperature,
            double Glucose,
            double Cholesterol,
            double AlcoholLevel,
            double ActivityLevel);
    }

    public sealed class PendingHardwareMeasurementProvider : IMeasurementProvider
    {
        public string ProviderName => "Hardware Bridge Pending";

        public Task<VitalMeasurement> CaptureAsync(int employeeId, CancellationToken cancellationToken = default)
        {
            return Task.FromException<VitalMeasurement>(
                new InvalidOperationException(
                    "Реальный сбор данных еще не подключен. Для сегодняшней проверки используйте diagnostics/test run, а для реального запуска добавьте device providers."));
        }
    }
}
