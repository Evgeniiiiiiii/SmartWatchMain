using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartWatchProj.Models;
using SmartWatchProj.Models.Diagnostics;
using SmartWatchProj.Models.Devices;
using SmartWatchProj.Services.Devices;
using SmartWatchProj.Services.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SmartWatchProj.ViewModels
{
    public partial class MainWindowViewModel
    {
        private RuntimeLogStore runtimeLogStore = null!;
        private DevicePreflightService devicePreflightService = null!;
        private DiagnosticsMeasurementProvider diagnosticsMeasurementProvider = null!;
        private PendingHardwareMeasurementProvider pendingHardwareMeasurementProvider = null!;
        private bool preflightHasRun;

#if DEBUG
        private const bool DefaultDiagnosticsMode = true;
#else
        private const bool DefaultDiagnosticsMode = false;
#endif

        [ObservableProperty] private bool isDiagnosticsModeEnabled = DefaultDiagnosticsMode;
        [ObservableProperty] private bool preferCameraVerificationInDiagnostics = true;
        [ObservableProperty] private bool isDevicePanelOpen;
        [ObservableProperty] private bool isCheckingHardware;
        [ObservableProperty] private string devicePanelSummary = "Подготовка еще не проверялась.";
        [ObservableProperty] private string preflightSummary = "Проверка оборудования еще не выполнялась.";
        [ObservableProperty] private string employeeStatusText = "Сотрудник не подтвержден.";
        [ObservableProperty] private string runModeText = string.Empty;
        [ObservableProperty] private string testRunSummary = "Тестовый прогон еще не запускался.";
        [ObservableProperty] private ObservableCollection<DeviceStatusSnapshot> deviceStatuses = new();
        [ObservableProperty] private ObservableCollection<AppLogEntry> logEntries = new();

        public string DevicePanelToggleText => IsDevicePanelOpen
            ? "Закрыть подготовку"
            : "Оборудование / Подготовка";

        public string DeviceModeLabel => IsDiagnosticsModeEnabled
            ? "Diagnostics mode"
            : "Основной режим";

        public IBrush DeviceModeBrush => IsDiagnosticsModeEnabled
            ? Brushes.SteelBlue
            : Brushes.DarkOliveGreen;

        private void InitializeReadinessLayer()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            runtimeLogStore = new RuntimeLogStore(baseDirectory);
            devicePreflightService = new DevicePreflightService(baseDirectory);
            diagnosticsMeasurementProvider = new DiagnosticsMeasurementProvider(runtimeLogStore);
            pendingHardwareMeasurementProvider = new PendingHardwareMeasurementProvider();

            UpdateRunModeText();
            UpdateEmployeeStatus();
            RefreshHardwareState();
            LogInfo("Startup", $"Diagnostics mode {(IsDiagnosticsModeEnabled ? "enabled" : "disabled")}.");
        }

        partial void OnIsDiagnosticsModeEnabledChanged(bool value)
        {
            UpdateRunModeText();
            OnPropertyChanged(nameof(DeviceModeLabel));
            OnPropertyChanged(nameof(DeviceModeBrush));

            if (devicePreflightService is not null)
            {
                RefreshHardwareState();
                LogInfo("Mode", value
                    ? "Diagnostics mode enabled. Peripheral devices may fall back to stub providers."
                    : "Diagnostics mode disabled. Real run now requires hardware providers.");
            }
        }

        partial void OnIsDevicePanelOpenChanged(bool value)
        {
            OnPropertyChanged(nameof(DevicePanelToggleText));
        }

        partial void OnIsOverlayVisibleChanged(bool value)
        {
            if (value)
            {
                IsDevicePanelOpen = false;
            }
        }

        partial void OnCardIdChanged(string value)
        {
            IsEmployeeFound = false;
            currentEmployeeId = 0;

            if (string.IsNullOrWhiteSpace(value))
            {
                UpdateEmployeeStatus();
                return;
            }

            EmployeeStatusText = $"Карта {value}: ожидается подтверждение сотрудника.";
        }

        partial void OnIsEmployeeFoundChanged(bool value)
        {
            var employee = value
                ? Employees.FirstOrDefault(item => item.Id == currentEmployeeId)
                : null;

            UpdateEmployeeStatus(employee);
        }

        [RelayCommand]
        private Task CheckHardware()
        {
            IsCheckingHardware = true;

            try
            {
                var snapshots = RefreshHardwareState();
                LogHardwareSnapshots(snapshots);
                LogInfo("Preflight", PreflightSummary);
            }
            catch (Exception ex)
            {
                PreflightSummary = $"Ошибка preflight: {ex.Message}";
                LogError("Preflight", ex.Message);
            }
            finally
            {
                IsCheckingHardware = false;
            }

            return Task.CompletedTask;
        }

        [RelayCommand]
        private void ToggleDevicePanel()
        {
            IsDevicePanelOpen = !IsDevicePanelOpen;
        }

        [RelayCommand]
        private void CloseDevicePanel()
        {
            IsDevicePanelOpen = false;
        }

        [RelayCommand]
        private async Task RunTestRun()
        {
            if (!Employees.Any())
            {
                ShowTopInfo("Нет сотрудников для тестового прогона.", Brushes.Red);
                LogWarning("TestRun", "Diagnostics run aborted: no employees.");
                return;
            }

            IsDiagnosticsModeEnabled = true;
            await EnsurePreflightReadyAsync();

            if (!IsEmployeeFound)
            {
                await SimulateCardRead();
            }

            TestRunSummary = "Запущен тестовый прогон через diagnostics provider.";
            LogInfo("TestRun", $"Diagnostics run requested for card {CardId}.");

            await Start();

            TestRunSummary = $"Последний тестовый прогон: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }

        private async Task EnsurePreflightReadyAsync(bool forceRefresh = false)
        {
            if (forceRefresh || !preflightHasRun || DeviceStatuses.Count == 0)
            {
                await CheckHardware();
            }
        }

        private IReadOnlyList<DeviceStatusSnapshot> RefreshHardwareState()
        {
            if (devicePreflightService is null)
            {
                return Array.Empty<DeviceStatusSnapshot>();
            }

            var snapshots = devicePreflightService.Check(IsDiagnosticsModeEnabled);
            DeviceStatuses = new ObservableCollection<DeviceStatusSnapshot>(snapshots);
            PreflightSummary = BuildPreflightSummary(snapshots);
            UpdateDevicePanelSummary(snapshots);
            preflightHasRun = true;
            PublishRecentLogs();
            return snapshots;
        }

        private string BuildPreflightSummary(IReadOnlyCollection<DeviceStatusSnapshot> snapshots)
        {
            var ready = snapshots.Count(snapshot => snapshot.State == DeviceReadinessState.Ready);
            var fallback = snapshots.Count(snapshot => snapshot.State == DeviceReadinessState.Diagnostics);
            var skipped = snapshots.Count(snapshot => snapshot.State == DeviceReadinessState.Skipped);
            var issues = snapshots.Count(snapshot =>
                snapshot.State is DeviceReadinessState.Warning
                    or DeviceReadinessState.Missing
                    or DeviceReadinessState.Error
                    or DeviceReadinessState.Unavailable
                    or DeviceReadinessState.Disabled);
            var blocking = snapshots.Count(snapshot => snapshot.IsBlocking);

            if (blocking > 0)
            {
                return $"Подтверждено: ready={ready}, fallback={fallback}, issues={issues}, skipped={skipped}. Реальный запуск блокируют {blocking} пункт(ов).";
            }

            if (issues > 0)
            {
                return $"Подтверждено: ready={ready}, fallback={fallback}, issues={issues}, skipped={skipped}.";
            }

            if (fallback > 0)
            {
                return $"Реально готово: {ready}. Только diagnostics fallback: {fallback}.";
            }

            return $"Оборудование подтверждено: ready={ready}, skipped={skipped}.";
        }

        private void UpdateRunModeText()
        {
            RunModeText = IsDiagnosticsModeEnabled
                ? "Diagnostics mode: preflight честно показывает реальные статусы устройств, а неподтвержденные модули остаются только fallback."
                : "Production mode: к запуску допускаются только реально подтвержденные устройства.";
        }

        private void UpdateDevicePanelSummary(IReadOnlyCollection<DeviceStatusSnapshot>? snapshots)
        {
            if (snapshots is null || snapshots.Count == 0)
            {
                DevicePanelSummary = IsDiagnosticsModeEnabled
                    ? "Diagnostics mode • оборудование ещё не проверено."
                    : "Основной режим • подготовка ещё не запускалась.";
                return;
            }

            var ready = snapshots.Count(snapshot => snapshot.State == DeviceReadinessState.Ready);
            var fallback = snapshots.Count(snapshot => snapshot.State == DeviceReadinessState.Diagnostics);
            var skipped = snapshots.Count(snapshot => snapshot.State == DeviceReadinessState.Skipped);
            var issues = snapshots.Count(snapshot =>
                snapshot.State is DeviceReadinessState.Warning
                    or DeviceReadinessState.Missing
                    or DeviceReadinessState.Error
                    or DeviceReadinessState.Unavailable
                    or DeviceReadinessState.Disabled);
            var cameraStatus = $"камера: {DescribeDeviceState("camera")}";

            DevicePanelSummary = $"{DeviceModeLabel} • {cameraStatus} • ready: {ready}, issues: {issues}, fallback: {fallback}, skipped: {skipped}";
        }

        private void UpdateEmployeeStatus(Employee? employee = null)
        {
            if (employee is not null)
            {
                EmployeeStatusText = $"Сотрудник подтвержден: {employee.Name} (ID={employee.Id}, карта={employee.CardId}).";
                return;
            }

            EmployeeStatusText = string.IsNullOrWhiteSpace(CardId)
                ? "Сотрудник не подтвержден."
                : $"Карта {CardId}: сотрудник не подтвержден.";
        }

        private DeviceStatusSnapshot? GetDeviceSnapshot(string deviceId) =>
            DeviceStatuses.FirstOrDefault(device => device.Id == deviceId);

        private string DescribeDeviceState(string deviceId)
        {
            var snapshot = GetDeviceSnapshot(deviceId);
            return snapshot is null ? "не проверено" : snapshot.StateText.ToLowerInvariant();
        }

        private void LogHardwareSnapshots(IReadOnlyCollection<DeviceStatusSnapshot> snapshots)
        {
            foreach (var snapshot in snapshots)
            {
                var message = $"{snapshot.DisplayName}: {snapshot.StateText}. {snapshot.Detail}";
                if (snapshot.State == DeviceReadinessState.Ready)
                {
                    LogInfo("Preflight", message);
                    continue;
                }

                if (snapshot.State == DeviceReadinessState.Error)
                {
                    LogError("Preflight", message);
                    continue;
                }

                LogWarning("Preflight", message);
            }
        }

        private bool IsDeviceReady(string deviceId) =>
            DeviceStatuses.Any(device => device.Id == deviceId && device.State == DeviceReadinessState.Ready);

        private async Task<bool> VerifyEmployeePresenceAsync()
        {
            var shouldUseCamera = !IsDiagnosticsModeEnabled || PreferCameraVerificationInDiagnostics;
            await EnsurePreflightReadyAsync(forceRefresh: shouldUseCamera);

            var canUseCamera = IsDeviceReady("camera") && IsDeviceReady("yolo");
            if (shouldUseCamera && canUseCamera)
            {
                LogInfo("Presence", "Camera verification started.");
                return await VerifyUserWithCamera();
            }

            var reason = shouldUseCamera
                ? $"Проверка камерой недоступна: камера={DescribeDeviceState("camera")}, YOLO={DescribeDeviceState("yolo")}."
                : "Проверка камерой пропущена настройкой diagnostics mode.";

            if (IsDiagnosticsModeEnabled)
            {
                ResultMessage = reason;
                ResultColor = Brushes.DarkOrange;
                CameraMessage = reason;
                CameraMessageColor = Brushes.DarkOrange;
                LogWarning("Presence", reason);
                return true;
            }

            ResultMessage = reason;
            ResultColor = Brushes.Red;
            CameraMessage = reason;
            CameraMessageColor = Brushes.Red;
            LogError("Presence", reason);
            return false;
        }

        private IMeasurementProvider GetMeasurementProvider() =>
            IsDiagnosticsModeEnabled
                ? diagnosticsMeasurementProvider
                : pendingHardwareMeasurementProvider;

        private string[] BuildMeasurementInstructions() =>
            IsDiagnosticsModeEnabled
                ? new[]
                {
                    "Preflight подтвержден",
                    "Diagnostics: часы и COM в fallback",
                    "Diagnostics: температура, давление и алкотестер через stub provider",
                    "Diagnostics: фиксация тестовых измерений"
                }
                : new[]
                {
                    "Подключение часов",
                    "Прислонитесь к измерителю температуры",
                    "Дуньте в алкотестер",
                    "Поместите манжету на руку",
                    "Поместите палец в глюкометр"
                };

        private int GetMeasurementStepDelayMs() => IsDiagnosticsModeEnabled ? 1200 : 5000;

        private async Task<VitalMeasurement> CaptureMeasurementAsync()
        {
            var provider = GetMeasurementProvider();
            LogInfo("Measurements", $"Using provider: {provider.ProviderName}.");
            return await provider.CaptureAsync(currentEmployeeId);
        }

        private void LogInfo(string source, string message)
        {
            runtimeLogStore.Info(source, message);
            PublishRecentLogs();
        }

        private void LogWarning(string source, string message)
        {
            runtimeLogStore.Warning(source, message);
            PublishRecentLogs();
        }

        private void LogError(string source, string message)
        {
            runtimeLogStore.Error(source, message);
            PublishRecentLogs();
        }

        private void PublishRecentLogs()
        {
            if (runtimeLogStore is null)
            {
                return;
            }

            LogEntries = new ObservableCollection<AppLogEntry>(
                runtimeLogStore.GetRecentEntries());
        }
    }
}
