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
using System.Threading;
using System.Threading.Tasks;

namespace SmartWatchProj.ViewModels
{
    public partial class MainWindowViewModel
    {
        private RuntimeLogStore runtimeLogStore = null!;
        private DevicePreflightService devicePreflightService = null!;
        private DiagnosticsMeasurementProvider diagnosticsMeasurementProvider = null!;
        private SerialHardwareMeasurementProvider hardwareMeasurementProvider = null!;
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
        [ObservableProperty] private string startBlockingSummary = "Статус запуска ещё не рассчитан.";
        [ObservableProperty] private string employeeStatusText = "Сотрудник не подтвержден.";
        [ObservableProperty] private string runModeText = string.Empty;
        [ObservableProperty] private string testRunSummary = "Тестовый прогон еще не запускался.";
        [ObservableProperty] private ObservableCollection<DeviceStatusSnapshot> deviceStatuses = new();
        [ObservableProperty] private ObservableCollection<AppLogEntry> logEntries = new();

        public string DevicePanelToggleText => IsDevicePanelOpen
            ? "Закрыть подготовку"
            : "Оборудование / Подготовка";

        public bool IsStrictHardwareMode => OperatingSystem.IsLinux();
        public bool IsNoCameraModeEnabled => IsStrictHardwareMode && LoadDeviceRuntimeConfig().LinuxNoCameraMode;
        public bool AreDiagnosticsControlsEnabled => !IsStrictHardwareMode;
        public bool IsSimulationEnabled => !IsStrictHardwareMode;
        public bool CanStartWorkflow => IsEmployeeFound && (!IsStrictHardwareMode || DeviceStatuses.All(snapshot => !snapshot.IsBlocking));
        public string CameraModeStatusText => IsNoCameraModeEnabled
            ? "Камера отключена для текущего Linux test режима."
            : string.Empty;
        public string StartAvailabilityReason
        {
            get
            {
                if (!IsEmployeeFound)
                {
                    return string.IsNullOrWhiteSpace(CardId)
                        ? "Старт недоступен: сотрудник не подтверждён."
                        : $"Старт недоступен: карта {CardId} не подтверждена.";
                }

                if (IsStrictHardwareMode)
                {
                    var blockingDevices = DeviceStatuses
                        .Where(snapshot => snapshot.IsBlocking)
                        .Select(snapshot => snapshot.DisplayName)
                        .ToArray();

                    if (blockingDevices.Length > 0)
                    {
                        return $"Старт недоступен: оборудование не готово ({string.Join(", ", blockingDevices)}).";
                    }
                }

                return "Старт доступен.";
            }
        }
        public bool CanEmergencyStop => IsCollectingData || IsCheckingHardware || IsOverlayVisible;

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
            devicePreflightService = new DevicePreflightService(baseDirectory, runtimeLogStore);
            diagnosticsMeasurementProvider = new DiagnosticsMeasurementProvider(runtimeLogStore);
            hardwareMeasurementProvider = new SerialHardwareMeasurementProvider(baseDirectory, runtimeLogStore);

            if (IsStrictHardwareMode)
            {
                IsDiagnosticsModeEnabled = false;
            }

            UpdateRunModeText();
            UpdateEmployeeStatus();
            RefreshHardwareState();
            if (IsNoCameraModeEnabled)
            {
                LogWarning("Mode", "Linux no-camera mode enabled");
            }
            LogInfo("Startup", $"Diagnostics mode {(IsDiagnosticsModeEnabled ? "enabled" : "disabled")}.");
        }

        partial void OnIsDiagnosticsModeEnabledChanged(bool value)
        {
            if (IsStrictHardwareMode && value)
            {
                IsDiagnosticsModeEnabled = false;
                LogWarning("Mode", "На Linux включён strict production mode: diagnostics fallback отключён.");
                return;
            }

            UpdateRunModeText();
            OnPropertyChanged(nameof(DeviceModeLabel));
            OnPropertyChanged(nameof(DeviceModeBrush));
            OnPropertyChanged(nameof(AreDiagnosticsControlsEnabled));
            OnPropertyChanged(nameof(IsSimulationEnabled));

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
            OnPropertyChanged(nameof(CanEmergencyStop));
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
                OnPropertyChanged(nameof(StartAvailabilityReason));
                return;
            }

            EmployeeStatusText = $"Карта {value}: ожидается подтверждение сотрудника.";
            OnPropertyChanged(nameof(CanStartWorkflow));
            OnPropertyChanged(nameof(StartAvailabilityReason));
        }

        partial void OnIsEmployeeFoundChanged(bool value)
        {
            var employee = value
                ? Employees.FirstOrDefault(item => item.Id == currentEmployeeId)
                : null;

            UpdateEmployeeStatus(employee);
            OnPropertyChanged(nameof(CanStartWorkflow));
            OnPropertyChanged(nameof(StartAvailabilityReason));
        }

        [RelayCommand]
        private async Task CheckHardware()
        {
            CancelPreflightOperation("explicit hardware check");
            preflightCts = new CancellationTokenSource();
            IsCheckingHardware = true;
            HardwareSafetyStatus = "Проверка оборудования выполняется.";
            OnPropertyChanged(nameof(CanEmergencyStop));

            try
            {
                var snapshots = RefreshHardwareState(preflightCts.Token);
                LogHardwareSnapshots(snapshots);
                LogInfo("Preflight", PreflightSummary);
                LogInfo("Preflight", "Проверка оборудования завершена. Presence-check и основной сценарий не запускались.");
                HardwareSafetyStatus = "Проверка завершена.";
            }
            catch (OperationCanceledException)
            {
                HardwareSafetyStatus = "Аварийная остановка.";
                LogWarning("Preflight", "Measurement aborted for safety");
            }
            catch (Exception ex)
            {
                PreflightSummary = $"Ошибка preflight: {ex.Message}";
                LogError("Preflight", ex.Message);
                HardwareSafetyStatus = ex is TimeoutException ? "Timeout" : "Проверка завершилась ошибкой.";
            }
            finally
            {
                IsCheckingHardware = false;
                preflightCts?.Dispose();
                preflightCts = null;
                OnPropertyChanged(nameof(CanEmergencyStop));
            }
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
            if (IsStrictHardwareMode)
            {
                ShowTopInfo("Тестовый прогон отключен: Linux работает только в реальном production-режиме.", Brushes.Red);
                LogWarning("TestRun", "Test run blocked in strict production mode.");
                return;
            }

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

        private IReadOnlyList<DeviceStatusSnapshot> RefreshHardwareState(CancellationToken cancellationToken = default)
        {
            if (devicePreflightService is null)
            {
                return Array.Empty<DeviceStatusSnapshot>();
            }

            var snapshots = devicePreflightService.Check(IsDiagnosticsModeEnabled, cancellationToken);
            DeviceStatuses = new ObservableCollection<DeviceStatusSnapshot>(snapshots);
            PreflightSummary = BuildPreflightSummary(snapshots);
            StartBlockingSummary = BuildStartBlockingSummary(snapshots);
            UpdateDevicePanelSummary(snapshots);
            preflightHasRun = true;
            PublishRecentLogs();
            OnPropertyChanged(nameof(CanStartWorkflow));
            OnPropertyChanged(nameof(StartAvailabilityReason));
            OnPropertyChanged(nameof(CameraModeStatusText));
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

            if (IsStrictHardwareMode)
            {
                if (blocking > 0)
                {
                    return $"Подтверждено: ready={ready}, issues={issues}, skipped={skipped}. Реальный запуск блокируют {blocking} пункт(ов).";
                }

                return issues > 0
                    ? $"Подтверждено: ready={ready}, issues={issues}, skipped={skipped}."
                    : $"Оборудование подтверждено: ready={ready}, skipped={skipped}.";
            }

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

        private static string BuildStartBlockingSummary(IReadOnlyCollection<DeviceStatusSnapshot> snapshots)
        {
            var blocking = snapshots.Where(snapshot => snapshot.IsBlocking).ToArray();
            if (blocking.Length == 0)
            {
                return "Старт разрешён: обязательные модули подтверждены.";
            }

            var labels = string.Join(", ", blocking.Select(snapshot => snapshot.DisplayName));
            return $"Старт заблокирован: {blocking.Length} обязательных модулей не готовы ({labels}).";
        }

        private void UpdateRunModeText()
        {
            if (IsStrictHardwareMode)
            {
                RunModeText = "Production mode (Linux): только реальные устройства, без diagnostics fallback и без симуляций.";
                return;
            }

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
            var cameraStatus = IsNoCameraModeEnabled
                ? "камера: отключена для Linux test режима"
                : $"камера: {DescribeDeviceState("camera")}";

            DevicePanelSummary = IsStrictHardwareMode
                ? $"{DeviceModeLabel} • {cameraStatus} • ready: {ready}, issues: {issues}, skipped: {skipped}"
                : $"{DeviceModeLabel} • {cameraStatus} • ready: {ready}, issues: {issues}, fallback: {fallback}, skipped: {skipped}";
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
            if (IsNoCameraModeEnabled)
            {
                const string message = "Camera stage bypassed by config";
                ResultMessage = "Камера отключена для текущего Linux test режима.";
                ResultColor = Brushes.DarkOrange;
                CameraMessage = ResultMessage;
                CameraMessageColor = Brushes.DarkOrange;
                LogWarning("Presence", message);
                return true;
            }

            var shouldUseCamera = !IsDiagnosticsModeEnabled || PreferCameraVerificationInDiagnostics;
            await EnsurePreflightReadyAsync(forceRefresh: shouldUseCamera);

            var canUseCamera = IsDeviceReady("camera") && IsDeviceReady("yolo");
            if (shouldUseCamera && canUseCamera)
            {
                LogInfo("Presence", "Camera verification started.");
                var cameraResult = await VerifyUserWithCamera();
                if (cameraResult.IsVerified)
                {
                    return true;
                }

                if (cameraResult.ShouldContinueWithoutCamera)
                {
                    var fallbackMessage = cameraResult.Message.Contains("camera", StringComparison.OrdinalIgnoreCase)
                        || cameraResult.Message.Contains("кадр", StringComparison.OrdinalIgnoreCase)
                        || cameraResult.Message.Contains("preview", StringComparison.OrdinalIgnoreCase)
                        ? "Камера подключена, превью недоступно. Основной ESP32-сценарий продолжен без камеры."
                        : $"{cameraResult.Message} Основной ESP32-сценарий продолжен без камеры.";
                    ResultMessage = fallbackMessage;
                    ResultColor = Brushes.DarkOrange;
                    CameraMessage = fallbackMessage;
                    CameraMessageColor = Brushes.DarkOrange;
                    LogWarning("Presence", fallbackMessage);
                    return true;
                }

                return false;
            }

            var reason = shouldUseCamera
                ? $"Проверка камерой недоступна: камера={DescribeDeviceState("camera")}, YOLO={DescribeDeviceState("yolo")}."
                : "Проверка камерой пропущена настройкой diagnostics mode.";
            var runtimeMessage = $"{reason} Основной ESP32-сценарий продолжен без камеры.";

            ResultMessage = runtimeMessage;
            ResultColor = Brushes.DarkOrange;
            CameraMessage = runtimeMessage;
            CameraMessageColor = Brushes.DarkOrange;
            LogWarning("Presence", runtimeMessage);
            return true;
        }

        private IMeasurementProvider GetMeasurementProvider() =>
            IsStrictHardwareMode
                ? hardwareMeasurementProvider
                : IsDiagnosticsModeEnabled
                ? diagnosticsMeasurementProvider
                : hardwareMeasurementProvider;

        private string[] BuildMeasurementInstructions() =>
            IsStrictHardwareMode
                ? new[]
                {
                    "Подключение к ESP32-контроллеру",
                    "Команда x1x1x: запуск измерения температуры",
                    "Команда x1x2x: запуск измерения алкоголя",
                    "Команда x1x3x: запуск измерения давления",
                    "Парсинг JSON {Temp,Alco,SYS,DAD} с optional debug-логами IRTemperature / Alco / SAD / DAD"
                }
                : IsDiagnosticsModeEnabled
                ? new[]
                {
                    "Preflight подтвержден",
                    "Diagnostics: проверка подключения контроллера",
                    "Diagnostics: эмуляция этапов ESP32-сценария",
                    "Diagnostics: фиксация тестовых измерений"
                }
                : new[]
                {
                    "Подключение к ESP32-контроллеру",
                    "Команда x1x1x: измерение температуры",
                    "Команда x1x2x: измерение алкоголя",
                    "Команда x1x3x: измерение давления",
                    "Парсинг JSON контроллера с optional debug-логами"
                };

        private int GetMeasurementStepDelayMs() => IsStrictHardwareMode ? 1500 : IsDiagnosticsModeEnabled ? 1200 : 5000;

        private async Task<VitalMeasurement> CaptureMeasurementAsync(
            int employeeId,
            CancellationToken cancellationToken,
            MeasurementWorkflowHooks? hooks = null)
        {
            var provider = GetMeasurementProvider();
            if (provider is null)
            {
                throw new InvalidOperationException("measurement provider is null");
            }

            LogInfo("Measurements", $"Using provider: {provider.ProviderName}.");
            var measurement = provider is SerialHardwareMeasurementProvider serialProvider
                ? await serialProvider.CaptureAsync(employeeId, hooks, cancellationToken)
                : await provider.CaptureAsync(employeeId, cancellationToken);
            if (measurement is null)
            {
                throw new InvalidOperationException("measurement result is null");
            }

            return measurement;
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
