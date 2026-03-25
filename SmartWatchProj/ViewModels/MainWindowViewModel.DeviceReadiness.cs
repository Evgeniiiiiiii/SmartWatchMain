using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartWatchProj.Models;
using SmartWatchProj.Models.Devices;
using SmartWatchProj.Services.Devices;
using SmartWatchProj.Services.Logging;
using Brushes = Avalonia.Media.Brushes;

namespace SmartWatchProj.ViewModels
{
    public partial class MainWindowViewModel
    {
        private IDeviceManager _deviceManager = null!;
        private IAppLogger _appLogger = null!;

        [ObservableProperty] private ObservableCollection<DeviceModuleState> deviceModules = new();
        [ObservableProperty] private string equipmentCompactSummary = "Оборудование не проверено";
        [ObservableProperty] private string equipmentSummary = "Оборудование не проверено.";
        [ObservableProperty] private IBrush equipmentSummaryColor = Brushes.Gray;
        [ObservableProperty] private string deviceConfigurationPath = string.Empty;
        [ObservableProperty] private string deviceLogPath = string.Empty;
        [ObservableProperty] private string manualEmployeeId = string.Empty;
        [ObservableProperty] private bool isEquipmentPanelExpanded;
        [ObservableProperty] private Employee? selectedEmployee;
        [ObservableProperty] private string confirmedEmployeeDisplay = "Сотрудник не подтвержден";
        [ObservableProperty] private bool isCheckingEquipment;
        [ObservableProperty] private bool canStartMeasurement;
        [ObservableProperty] private bool canStartTestRun;
        [ObservableProperty] private string lastWorkflowSummary = string.Empty;

        private void InitializeOperationalServices()
        {
            _appLogger = new FileAppLogger();
            _deviceManager = new DeviceManager(new DeviceConfigurationStore(), _appLogger);
            DeviceConfigurationPath = _deviceManager.ConfigurationPath;
            DeviceLogPath = _appLogger.LogFilePath;

            _ = InitializeDeviceStateAsync();
        }

        private async Task InitializeDeviceStateAsync()
        {
            try
            {
                await _deviceManager.LoadAsync();
                DeviceModules = _deviceManager.Devices;
                AttachDeviceSubscriptions();
                RefreshEquipmentSummary();
                await CheckEquipment();
            }
            catch (Exception ex)
            {
                _appLogger.Error("Не удалось инициализировать менеджер устройств.", ex);
                EquipmentSummary = $"Ошибка инициализации устройств: {ex.Message}";
                EquipmentCompactSummary = "Оборудование не инициализировано";
                EquipmentSummaryColor = Brushes.Red;
                ShowTopInfo("Не удалось загрузить настройки устройств.", Brushes.Red);
            }
        }

        partial void OnDeviceModulesChanged(ObservableCollection<DeviceModuleState> value)
        {
            AttachDeviceSubscriptions();
            RefreshEquipmentSummary();
        }

        partial void OnIsEmployeeFoundChanged(bool value)
        {
            RefreshEquipmentSummary();
        }

        private void AttachDeviceSubscriptions()
        {
            foreach (var device in DeviceModules)
            {
                device.PropertyChanged -= DeviceModuleOnPropertyChanged;
                device.PropertyChanged += DeviceModuleOnPropertyChanged;
            }
        }

        private void DeviceModuleOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            RefreshEquipmentSummary();
        }

        private void RefreshEquipmentSummary()
        {
            var readyCount = DeviceModules.Count(device => device.IsEnabled && device.Status == DeviceStatus.Ready);
            var detectingCount = DeviceModules.Count(device => device.IsEnabled && device.Status == DeviceStatus.Detecting);
            var errorCount = DeviceModules.Count(device => device.IsEnabled && device.Status == DeviceStatus.Error);
            var missingCount = DeviceModules.Count(device => device.IsEnabled && device.Status == DeviceStatus.NotFound);
            var disabledCount = DeviceModules.Count(device => !device.IsEnabled);

            EquipmentSummary = $"Готово: {readyCount} | Поиск: {detectingCount} | Не найдено: {missingCount} | Ошибки: {errorCount} | Отключено: {disabledCount}";
            EquipmentCompactSummary = $"Готово: {readyCount} | Не найдено: {missingCount} | Ошибки: {errorCount}";
            EquipmentSummaryColor = errorCount > 0
                ? Brushes.Red
                : missingCount > 0
                    ? Brushes.Orange
                    : readyCount > 0
                        ? Brushes.Green
                        : Brushes.Gray;

            CanStartMeasurement = IsEmployeeFound && readyCount > 0;
            CanStartTestRun = IsEmployeeFound && DeviceModules.Any(device => device.IsEnabled);
        }

        [RelayCommand]
        private async Task CheckEquipment()
        {
            if (IsCheckingEquipment)
            {
                return;
            }

            try
            {
                IsCheckingEquipment = true;
                ResultMessage = "Проверка оборудования...";
                ResultColor = Brushes.Black;
                _appLogger.Info("Запущена повторная проверка оборудования.");

                await _deviceManager.CheckEquipmentAsync();

                RefreshEquipmentSummary();
                ResultMessage = EquipmentSummary;
                ResultColor = EquipmentSummaryColor;
            }
            catch (Exception ex)
            {
                _appLogger.Error("Ошибка при проверке оборудования.", ex);
                ResultMessage = $"Ошибка проверки оборудования: {ex.Message}";
                ResultColor = Brushes.Red;
                ShowTopInfo("Проверка оборудования завершилась ошибкой.", Brushes.Red);
            }
            finally
            {
                IsCheckingEquipment = false;
            }
        }

        [RelayCommand]
        private async Task SaveDeviceSettings()
        {
            try
            {
                await _deviceManager.SaveAsync();
                _appLogger.Info("Настройки устройств сохранены.");
                ShowTopInfo("Настройки устройств сохранены.", Brushes.Green);
                await CheckEquipment();
            }
            catch (Exception ex)
            {
                _appLogger.Error("Ошибка сохранения настроек устройств.", ex);
                ShowTopInfo($"Не удалось сохранить настройки: {ex.Message}", Brushes.Red);
            }
        }

        [RelayCommand]
        private async Task ConfirmEmployee()
        {
            await Task.Yield();

            var employee = ResolveEmployeeFromInputs();
            if (employee == null)
            {
                ClearConfirmedEmployee();
                ShowTopInfo("Сотрудник не найден. Укажите CardId, EmployeeId или выберите из списка.", Brushes.Red);
                return;
            }

            ApplyConfirmedEmployee(employee);
            ShowTopInfo($"Сотрудник подтвержден: {employee.Name} (ID {employee.Id})", Brushes.Green);
        }

        [RelayCommand]
        private async Task StartTestRun()
        {
            try
            {
                ClearTopInfo();

                if (!EnsureEmployeeConfirmed())
                {
                    return;
                }

                if (!DeviceModules.Any(device => device.IsEnabled))
                {
                    ShowTopInfo("Для тестового прогона включите хотя бы одно устройство.", Brushes.Red);
                    return;
                }

                IsCollectingData = true;
                IsOverlayVisible = true;
                IsCaptchaVisible = false;
                CurrentInstruction = "Подготовка тестового прогона";
                ProgressValue = 0;

                var workflowResult = await StartMeasurementProcessAsync(isTestRun: true);
                ApplyWorkflowResult(workflowResult, persistMeasurement: false, isTestRun: true);
            }
            catch (Exception ex)
            {
                _appLogger.Error("Тестовый прогон завершился ошибкой.", ex);
                ResultMessage = $"Ошибка тестового прогона: {ex.Message}";
                ResultColor = Brushes.Red;
            }
            finally
            {
                IsCollectingData = false;
                IsOverlayVisible = false;
                ProgressValue = 0;
                CurrentInstruction = string.Empty;
            }
        }

        private bool EnsureEmployeeConfirmed()
        {
            var employee = ResolveEmployeeFromInputs();
            if (employee == null)
            {
                ClearConfirmedEmployee();
                ShowTopInfo("Подтвердите сотрудника перед запуском измерений.", Brushes.Red);
                return false;
            }

            ApplyConfirmedEmployee(employee);
            return true;
        }

        private Employee? ResolveEmployeeFromInputs()
        {
            if (!string.IsNullOrWhiteSpace(CardId))
            {
                var byCard = Employees.FirstOrDefault(employee =>
                    string.Equals(employee.CardId, CardId.Trim(), StringComparison.OrdinalIgnoreCase));

                if (byCard != null)
                {
                    return byCard;
                }
            }

            if (!string.IsNullOrWhiteSpace(ManualEmployeeId) &&
                int.TryParse(ManualEmployeeId.Trim(), out var employeeId))
            {
                var byId = Employees.FirstOrDefault(employee => employee.Id == employeeId);
                if (byId != null)
                {
                    return byId;
                }
            }

            return SelectedEmployee;
        }

        private void ApplyConfirmedEmployee(Employee employee)
        {
            SelectedEmployee = employee;
            currentEmployeeId = employee.Id;
            IsEmployeeFound = true;

            if (!string.IsNullOrWhiteSpace(employee.CardId))
            {
                CardId = employee.CardId;
            }

            ManualEmployeeId = employee.Id.ToString();
            ConfirmedEmployeeDisplay = $"{employee.Name} | ID {employee.Id}" +
                                       (string.IsNullOrWhiteSpace(employee.CardId) ? string.Empty : $" | Card {employee.CardId}");

            ResultMessage = $"Подтвержден сотрудник: {employee.Name} (ID {employee.Id})";
            ResultColor = Brushes.Green;
        }

        private void ClearConfirmedEmployee()
        {
            IsEmployeeFound = false;
            currentEmployeeId = 0;
            ConfirmedEmployeeDisplay = "Сотрудник не подтвержден";
            ResultColor = Brushes.Red;
        }

        private async Task<MeasurementWorkflowResult> StartMeasurementProcessAsync(bool isTestRun)
        {
            var workflowResult = new MeasurementWorkflowResult
            {
                Measurement = new VitalMeasurement
                {
                    EmployeeId = currentEmployeeId,
                    Timestamp = DateTime.Now
                }
            };

            var enabledDevices = DeviceModules
                .Where(device => device.IsEnabled)
                .OrderBy(device => device.SortOrder)
                .ToList();

            if (enabledDevices.Count == 0)
            {
                workflowResult.Issues.Add("Нет включенных устройств для запуска.");
                _measurementCompletion?.TrySetResult(workflowResult);
                return workflowResult;
            }

            for (var index = 0; index < enabledDevices.Count; index++)
            {
                var device = enabledDevices[index];
                CurrentInstruction = $"{(isTestRun ? "Тест" : "Измерение")}: {device.DisplayName}";
                ProgressValue = (index / (double)enabledDevices.Count) * 100;

                _appLogger.Info($"Старт {(isTestRun ? "тестового шага" : "измерения")} для устройства {device.DisplayName}.");

                var operationResult = await _deviceManager.ReadDeviceAsync(device, currentEmployeeId);
                workflowResult.DeviceResults.Add(operationResult);

                if (operationResult.Success)
                {
                    ApplyDeviceResultToMeasurement(workflowResult, operationResult);
                }
                else
                {
                    workflowResult.Issues.Add($"{device.DisplayName}: {operationResult.Message}");
                }
            }

            ProgressValue = 100;
            CurrentInstruction = isTestRun ? "Тестовый прогон завершен" : "Измерения завершены";
            _appLogger.Info(isTestRun ? "Тестовый прогон завершен." : "Измерения завершены.");

            _measurementCompletion?.TrySetResult(workflowResult);
            return workflowResult;
        }

        private void ApplyDeviceResultToMeasurement(MeasurementWorkflowResult workflowResult, DeviceOperationResult operationResult)
        {
            var measurement = workflowResult.Measurement;

            switch (operationResult.ModuleType)
            {
                case DeviceModuleType.Thermometer:
                    if (operationResult.PrimaryStationResponse != null)
                    {
                        measurement.Temperature = operationResult.PrimaryStationResponse.Temp;
                    }
                    break;

                case DeviceModuleType.AlcoholTester:
                    if (operationResult.PrimaryStationResponse != null)
                    {
                        measurement.AlcoholLevel = operationResult.PrimaryStationResponse.Alco;
                        workflowResult.Issues.Add("Алкоголь получен как RAW ADC. Для интерпретации потребуется калибровка.");
                    }
                    break;

                case DeviceModuleType.BloodPressureMonitor:
                    if (operationResult.PrimaryStationResponse != null)
                    {
                        measurement.BloodPressureSystolic = operationResult.PrimaryStationResponse.Sys;
                        measurement.BloodPressureDiastolic = operationResult.PrimaryStationResponse.Dad;
                    }
                    break;

                case DeviceModuleType.WearableMonitor:
                    var lastPoint = operationResult.WearableDeviceResponse?.Data?.LastOrDefault();
                    if (lastPoint != null)
                    {
                        measurement.HeartRate = lastPoint.Hr;
                        measurement.Saturation = lastPoint.SpO2;
                        measurement.ActivityLevel = lastPoint.Activity;
                    }
                    else
                    {
                        workflowResult.Issues.Add("Носимое устройство вернуло пустой набор данных.");
                    }
                    break;
            }
        }

        private void ApplyWorkflowResult(MeasurementWorkflowResult workflowResult, bool persistMeasurement, bool isTestRun)
        {
            var measurement = workflowResult.Measurement;
            var (verdict, verdictColor, healthTiles) = BuildVerdict(workflowResult);

            measurement.Recommendation = verdict;
            measurement.Diagnosis = BuildWorkflowDiagnosis(workflowResult, healthTiles, verdict, isTestRun);

            DetailedHealthTiles = new ObservableCollection<HealthTile>(healthTiles);
            VerdictColor = verdictColor;
            HumanRecommendation = measurement.Diagnosis;
            ShowDetailedResult = healthTiles.Count > 0;

            UpdateTilesWithStatuses(healthTiles, verdict);

            if (persistMeasurement && workflowResult.HasAnyRealData)
            {
                Measurements.Add(measurement);
                sessionMeasurements.Add(measurement);
                SaveMeasurementsToJson();
            }

            var missingDevices = workflowResult.DeviceResults
                .Where(result => !result.Success)
                .Select(result => result.DeviceName)
                .ToList();

            var summaryBuilder = new StringBuilder()
                .Append(isTestRun ? "Тестовый прогон" : "Измерение")
                .Append(": ")
                .Append(verdict);

            if (missingDevices.Count > 0)
            {
                summaryBuilder
                    .Append(". Недоступно: ")
                    .Append(string.Join(", ", missingDevices));
            }

            ResultMessage = summaryBuilder.ToString();
            ResultColor = workflowResult.HasAnyRealData ? verdictColor : Brushes.Red;
            LastWorkflowSummary = ResultMessage;
        }

        private (string verdict, IBrush color, System.Collections.Generic.List<HealthTile> tiles) BuildVerdict(MeasurementWorkflowResult workflowResult)
        {
            var tiles = new System.Collections.Generic.List<HealthTile>();
            var availableModules = workflowResult.DeviceResults
                .Where(result => result.Success)
                .Select(result => result.ModuleType)
                .ToHashSet();

            var measurement = workflowResult.Measurement;

            if (availableModules.Contains(DeviceModuleType.WearableMonitor))
            {
                if (measurement.HeartRate > 0)
                {
                    if (measurement.HeartRate < 50)
                    {
                        tiles.Add(new HealthTile("ЧСС", $"{measurement.HeartRate:F0}", "Очень низкая", "Требуется повторная проверка пульса.", Brushes.Red));
                    }
                    else if (measurement.HeartRate <= 100)
                    {
                        tiles.Add(new HealthTile("ЧСС", $"{measurement.HeartRate:F0}", "Норма", "Пульс в допустимом диапазоне.", Brushes.Green));
                    }
                    else if (measurement.HeartRate <= 120)
                    {
                        tiles.Add(new HealthTile("ЧСС", $"{measurement.HeartRate:F0}", "Повышенная", "Нужен контроль нагрузки и повторное измерение.", Brushes.Orange));
                    }
                    else
                    {
                        tiles.Add(new HealthTile("ЧСС", $"{measurement.HeartRate:F0}", "Высокая", "Показатель требует внимания.", Brushes.Red));
                    }
                }

                if (measurement.Saturation > 0)
                {
                    if (measurement.Saturation >= 95)
                    {
                        tiles.Add(new HealthTile("SpO₂", $"{measurement.Saturation:F0}%", "Норма", "Сатурация в рабочем диапазоне.", Brushes.Green));
                    }
                    else if (measurement.Saturation >= 92)
                    {
                        tiles.Add(new HealthTile("SpO₂", $"{measurement.Saturation:F0}%", "Снижена", "Нужна повторная проверка сатурации.", Brushes.Orange));
                    }
                    else
                    {
                        tiles.Add(new HealthTile("SpO₂", $"{measurement.Saturation:F0}%", "Критично", "Показатель требует немедленной проверки.", Brushes.Red));
                    }
                }

                if (measurement.ActivityLevel > 0)
                {
                    tiles.Add(new HealthTile("Активность", $"{measurement.ActivityLevel:F0}", "Получена", "Данные активности считаны с носимого устройства.", Brushes.SteelBlue));
                }
            }

            if (availableModules.Contains(DeviceModuleType.BloodPressureMonitor) &&
                measurement.BloodPressureSystolic > 0 &&
                measurement.BloodPressureDiastolic > 0)
            {
                var pressureText = $"{measurement.BloodPressureSystolic:F0}/{measurement.BloodPressureDiastolic:F0}";
                if (measurement.BloodPressureSystolic < 90 || measurement.BloodPressureDiastolic < 60)
                {
                    tiles.Add(new HealthTile("АД", pressureText, "Понижено", "Нужна повторная проверка давления.", Brushes.Orange));
                }
                else if (measurement.BloodPressureSystolic <= 139 && measurement.BloodPressureDiastolic <= 89)
                {
                    tiles.Add(new HealthTile("АД", pressureText, "Норма", "Давление в допустимых границах.", Brushes.Green));
                }
                else if (measurement.BloodPressureSystolic <= 159 || measurement.BloodPressureDiastolic <= 99)
                {
                    tiles.Add(new HealthTile("АД", pressureText, "Повышено", "Рекомендуется повторное измерение давления.", Brushes.Orange));
                }
                else
                {
                    tiles.Add(new HealthTile("АД", pressureText, "Высокое", "Показатель требует внимания.", Brushes.Red));
                }
            }

            if (availableModules.Contains(DeviceModuleType.Thermometer) && measurement.Temperature > 0)
            {
                if (measurement.Temperature < 35.0)
                {
                    tiles.Add(new HealthTile("Темп.", $"{measurement.Temperature:F1}°C", "Низкая", "Нужна повторная проверка температуры.", Brushes.Orange));
                }
                else if (measurement.Temperature <= 37.2)
                {
                    tiles.Add(new HealthTile("Темп.", $"{measurement.Temperature:F1}°C", "Норма", "Температура в допустимом диапазоне.", Brushes.Green));
                }
                else if (measurement.Temperature <= 38.0)
                {
                    tiles.Add(new HealthTile("Темп.", $"{measurement.Temperature:F1}°C", "Повышена", "Потребуется повторное измерение температуры.", Brushes.Orange));
                }
                else
                {
                    tiles.Add(new HealthTile("Темп.", $"{measurement.Temperature:F1}°C", "Высокая", "Температура требует внимания.", Brushes.Red));
                }
            }

            if (availableModules.Contains(DeviceModuleType.AlcoholTester))
            {
                tiles.Add(new HealthTile("Алкоголь", $"{measurement.AlcoholLevel:F0} ADC", "RAW", "Требуется калибровка и интерпретация raw-значения.", Brushes.SteelBlue));
            }

            if (tiles.Count == 0)
            {
                return ("Нет данных", Brushes.Orange, tiles);
            }

            var hasCritical = tiles.Any(tile => tile.Color == Brushes.Red);
            var hasWarning = tiles.Any(tile => tile.Color == Brushes.Orange);

            var verdict = hasCritical
                ? "Риск"
                : hasWarning
                    ? "Внимание"
                    : "Норма";

            var verdictColor = hasCritical
                ? Brushes.Red
                : hasWarning
                    ? Brushes.Orange
                    : Brushes.Green;

            return (verdict, verdictColor, tiles);
        }

        private string BuildWorkflowDiagnosis(
            MeasurementWorkflowResult workflowResult,
            System.Collections.Generic.IEnumerable<HealthTile> healthTiles,
            string verdict,
            bool isTestRun)
        {
            var employee = Employees.FirstOrDefault(item => item.Id == workflowResult.Measurement.EmployeeId);
            var employeeName = employee?.Name ?? $"ID {workflowResult.Measurement.EmployeeId}";

            var builder = new StringBuilder();
            builder.AppendLine($"{employeeName}: {(isTestRun ? "тестовый прогон" : "измерение")} завершен.");

            var tiles = healthTiles.ToList();
            if (tiles.Count > 0)
            {
                builder.AppendLine("Полученные показатели:");
                foreach (var tile in tiles)
                {
                    builder.AppendLine($"- {tile.Title}: {tile.Value} ({tile.Status})");
                }
            }
            else
            {
                builder.AppendLine("Реальные данные от устройств не получены.");
            }

            builder.AppendLine($"Итоговый статус: {verdict}.");

            if (workflowResult.Issues.Count > 0)
            {
                builder.AppendLine("Замечания:");
                foreach (var issue in workflowResult.Issues.Distinct())
                {
                    builder.AppendLine($"- {issue}");
                }
            }

            return builder.ToString().Trim();
        }
    }
}
