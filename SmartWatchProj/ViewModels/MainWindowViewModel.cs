using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emgu.CV;
using Emgu.CV.CvEnum;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SkiaSharp;
using SmartWatchProj.Models;
using SmartWatchProj.Services.Devices;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using YoloDotNet;
using YoloDotNet.Core;
using YoloDotNet.Extensions;
using YoloDotNet.Models;
using Brushes = Avalonia.Media.Brushes;
namespace SmartWatchProj.ViewModels
{
    public class HealthTile
    {
        public string Title { get; set; } = "";
        public string Value { get; set; } = "";
        public string Status { get; set; } = "";
        public string Advice { get; set; } = "";  // Новый: совет/предупреждение
        public Avalonia.Media.IBrush Color { get; set; } = Avalonia.Media.Brushes.Gray;

        public HealthTile(string title, string value, string status, string advice, Avalonia.Media.IBrush color)
        {
            Title = title;
            Value = value;
            Status = status;
            Advice = advice;
            Color = color;
        }
    }

    public partial class MainWindowViewModel : ViewModelBase
    {
        // Свойства для UI (убрали HumanRecommendation из отображения, но оставили для PDF)
        [ObservableProperty] private ObservableCollection<VitalMeasurement> measurements = new();
        [ObservableProperty] private string cardId = string.Empty;
        [ObservableProperty] private bool isEmployeeFound;
        [ObservableProperty] private bool isCollectingData;
        [ObservableProperty] private string resultMessage = string.Empty;
        [ObservableProperty] private Avalonia.Media.IBrush resultColor = Avalonia.Media.Brushes.Black;
        [ObservableProperty] private string cameraMessage = string.Empty;
        [ObservableProperty] private Avalonia.Media.IBrush cameraMessageColor = Avalonia.Media.Brushes.Black;
        [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? cameraFrame;
        [ObservableProperty] private string instructionMessage = string.Empty;
        [ObservableProperty] private ObservableCollection<Employee> employees = new();
        [ObservableProperty] private ObservableCollection<HealthTile> detailedHealthTiles = new(); // Для расчёта, но не биндим напрямую
        [ObservableProperty] private string finalVerdict = "";
        [ObservableProperty] private string verdictReason = string.Empty;
        [ObservableProperty] private string verdictRecommendation = string.Empty;
        [ObservableProperty] private Avalonia.Media.IBrush verdictColor = Avalonia.Media.Brushes.Gray;
        [ObservableProperty] private bool isVerdictVisible;
        // [ObservableProperty] private string humanRecommendation = ""; // Убрали из UI, оставили для PDF
        [ObservableProperty] private bool showDetailedResult = false;
        [ObservableProperty] private string newEmployeeName = string.Empty;
        [ObservableProperty] private string newCardId = string.Empty;

        [ObservableProperty] private string lastHeartRate = "Нет данных";
        [ObservableProperty] private string lastSaturation = "Нет данных";
        [ObservableProperty] private string lastBloodPressure = "Нет данных";
        [ObservableProperty] private string lastTemperature = "Нет данных";
        [ObservableProperty] private string lastGlucose = "Нет данных";
        [ObservableProperty] private string lastCholesterol = "Нет данных";
        [ObservableProperty] private string lastAlcoholLevel = "Нет данных";
        [ObservableProperty] private string lastActivityLevel = "Нет данных";
        [ObservableProperty] private string lastRecommendation = "Нет данных"; // Теперь для 9-й плитки

        [ObservableProperty] private string humanRecommendation = "";

        [ObservableProperty] private string serverIp = string.Empty;
        [ObservableProperty] private string appliedServerIp = string.Empty;
        [ObservableProperty] private string syncMessage = ""; // Статус для UI

        [ObservableProperty] private string adminPassword = "";  // Пароль админа
        [ObservableProperty] private bool isAdminAuthenticated = false;  // Флаг аутентификации

        [ObservableProperty] private bool isOverlayVisible = false;  // Видимость оверлея
        [ObservableProperty] private bool isCaptchaVisible = false;  // Видимость CAPTCHA (true сначала)
        [ObservableProperty] private string hardwareSafetyStatus = "Готов к работе.";
        [ObservableProperty] private string captchaPrompt = "";  // Текст "Проверка: Нажмите на все цифры X"
        [ObservableProperty] private string currentInstruction = "";  // Текст текущей инструкции
        [ObservableProperty] private string pressurePrepCountdownText = string.Empty;
        [ObservableProperty] private bool isPressurePrepActive;
        [ObservableProperty] private double progressValue = 0;  // Значение ProgressBar (0-100)
        [ObservableProperty] private ObservableCollection<int> captchaItems = new();  // Коллекция 9 случайных цифр (0-9)
        [ObservableProperty] private string captchaMessage = "";  // Сообщение об ошибке CAPTCHA
        [ObservableProperty] private int selectedCount = 0;  // Количество выбранных элементов

        [ObservableProperty] private ObservableCollection<double> captchaLefts = new();
        [ObservableProperty] private ObservableCollection<double> captchaTops = new();

        [ObservableProperty] private ObservableCollection<bool> isCaptchaSelected = new(Enumerable.Repeat(false, 9));  // Инициализация false для 9 кнопок
        [ObservableProperty] private ObservableCollection<IBrush> captchaBackgrounds = new();

        //[ObservableProperty] private string topInfoMessage = "";
        //[ObservableProperty] private IBrush topInfoColor = Brushes.Red;
        //[ObservableProperty] private bool isTopInfoVisible;
        private List<int> selectedCaptchaIndices = new List<int>();  // Выбранные индексы
        private List<int> correctCaptchaIndices = new List<int>();  // Индексы с targetDigit
        private TaskCompletionSource<VitalMeasurement>? _measurementCompletion;  // Для ожидания результата измерений
        private TaskCompletionSource<bool>? pressurePrepCompletion;
        private CancellationTokenSource? measurementRunCts;
        private CancellationTokenSource? preflightCts;

        private const string EmployeesJsonPath = "employees.json";
        private const string MeasurementsJsonPath = "measurements.json";
        private const string DeviceConfigFileName = "device-port-map.json";
        private int currentEmployeeId;
        private VideoCapture? capture;
        private Yolo? yoloEngine;
        private PdfDocument sessionReport = new PdfDocument();
        private List<VitalMeasurement> sessionMeasurements = new List<VitalMeasurement>();
        private bool linuxCameraAvailable;
        private EmployeeOverallStatus employeeOverallStatus = EmployeeOverallStatus.Unknown;


        public MainWindowViewModel()
        {
            EnsureCriticalUiStateInitialized();

            try
            {
                LoadEmployeesFromJson();
                LoadMeasurementsFromJson();
                UpdateLastData();
                InitializeReadinessLayer();
                Console.WriteLine($"Инициализация успешна. Время: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при инициализации: {ex.Message}");
            }
        }

        private string _topInfoMessage = string.Empty;
        public string TopInfoMessage
        {
            get => _topInfoMessage;
            set
            {
                if (_topInfoMessage == value) return;
                _topInfoMessage = value;
                OnPropertyChanged(nameof(TopInfoMessage));
                IsTopInfoVisible = !string.IsNullOrWhiteSpace(_topInfoMessage);
            }
        }

        private IBrush _topInfoColor = Brushes.Red;
        public IBrush TopInfoColor
        {
            get => _topInfoColor;
            set
            {
                if (_topInfoColor == value) return;
                _topInfoColor = value;
                OnPropertyChanged(nameof(TopInfoColor));
            }
        }

        private bool _isTopInfoVisible;
        public bool IsTopInfoVisible
        {
            get => _isTopInfoVisible;
            private set
            {
                if (_isTopInfoVisible == value) return;
                _isTopInfoVisible = value;
                OnPropertyChanged(nameof(IsTopInfoVisible));
            }
        }

        private void ShowTopInfo(string message, IBrush? color = null)
        {
            TopInfoColor = color ?? Brushes.Red;
            TopInfoMessage = message; // тут же обновится IsTopInfoVisible
        }

        private void ClearTopInfo()
        {
            TopInfoMessage = string.Empty; // тут же скроется панель
        }

        partial void OnIsCollectingDataChanged(bool value)
        {
            OnPropertyChanged(nameof(CanEmergencyStop));
        }

        partial void OnServerIpChanged(string value)
        {
            var editedValue = value?.Trim() ?? string.Empty;
            var appliedValue = AppliedServerIp?.Trim() ?? string.Empty;
            if (!string.Equals(editedValue, appliedValue, StringComparison.Ordinal))
            {
                SyncMessage = string.IsNullOrWhiteSpace(editedValue)
                    ? "Есть неприменённые изменения: IP очищен."
                    : "Есть неприменённые изменения.";
                LogInfo("Sync", $"Sync server IP edited but not yet applied: {editedValue}");
            }
            else if (TryBuildSyncEndpointUrl(appliedValue, out var endpointUrl, out _))
            {
                SyncMessage = $"Применено: {endpointUrl}";
            }
            else
            {
                SyncMessage = "IP сервера сайта не задан.";
            }

            OnPropertyChanged(nameof(SyncEndpointUrl));
        }

        // Команда для кнопки Старт
        [RelayCommand]
        private async Task Start()
        {
            if (IsCollectingData)
            {
                LogWarning("Start", "Runtime blocked: measurement already in progress.");
                ShowTopInfo("Старт временно заблокирован: измерение уже выполняется.", Brushes.Red);
                return;
            }

            try
            {
                EnsureCriticalUiStateInitialized();
                EnsureReadinessLayerInitializedForRuntime();
                LogInfo("Start", "Start requested");
                LogInfo("Start", "Start entered");
                CancelMeasurementOperation("start requested new measurement run");
                measurementRunCts = new CancellationTokenSource();
                var runCts = measurementRunCts;
                IsDevicePanelOpen = false;
                ClearTopInfo();//
                IsCollectingData = true;
                ResetVerdictDisplay();
                HardwareSafetyStatus = "Подготовка к измерению.";
                ResultMessage = "Проверка пользователя...";
                ResultColor = Avalonia.Media.Brushes.Black;
                CameraMessage = string.Empty;
                CameraMessageColor = Avalonia.Media.Brushes.Black;
                LogInfo("Start", "Start preconditions checked");
                LogInfo("History", $"History subsystem ready. Measurements collection count={Measurements.Count}.");
                LogInfo("Start", $"Verdict visuals ready. VerdictColor={VerdictColor}; FinalVerdict='{FinalVerdict}'.");
                await CheckEmployeeByCardId();
                if (!IsEmployeeFound)
                {
                    LogWarning("Start", "Runtime blocked: employee not resolved.");
                    ShowTopInfo("Сотрудник не найден", Brushes.Red);
                    IsCollectingData = false;
                    return;
                }

                if (currentEmployeeId <= 0)
                {
                    throw new InvalidOperationException("employee not resolved");
                }

                var currentEmployee = Employees.FirstOrDefault(employee => employee.Id == currentEmployeeId);
                if (currentEmployee is null)
                {
                    throw new InvalidOperationException("current employee is null");
                }

                LogInfo("Start", $"Employee resolved: id={currentEmployee.Id}, card={currentEmployee.CardId}.");
                await EnsurePreflightReadyAsync();
                LogInfo("Start", $"Equipment state validated. {StartBlockingSummary}");
                if (IsStrictHardwareMode && DeviceStatuses.Any(snapshot => snapshot.IsBlocking))
                {
                    throw new InvalidOperationException(StartAvailabilityReason);
                }

                if (IsNoCameraModeEnabled)
                {
                    LogWarning("Start", "Camera stage bypassed by config");
                }
                else
                {
                    LogInfo("Start", "Camera stage entered.");
                }

                bool verified = await VerifyEmployeePresenceAsync();
                if (!verified)
                {
                    LogWarning("Start", "Runtime blocked: presence check rejected.");
                    if (string.IsNullOrWhiteSpace(CameraMessage))
                    {
                        CameraMessage = ResultMessage;
                    }
                    CameraMessageColor = Avalonia.Media.Brushes.Red;
                    return;
                }

                LogInfo("Start", IsNoCameraModeEnabled
                    ? "Camera stage skipped by config."
                    : "Camera stage skipped or completed without blocking runtime.");
                if (string.IsNullOrWhiteSpace(CameraMessage))
                {
                    CameraMessage = "Пользователь верифицирован.";
                    CameraMessageColor = Avalonia.Media.Brushes.Green;
                }

                VitalMeasurement measurement;
                IsOverlayVisible = true;
                LogInfo("Captcha", $"Captcha flow entered. Platform={(OperatingSystem.IsLinux() ? "Linux" : "Windows")}; runtimeStage=pre-measurement; employeeId={currentEmployee.Id}.");
                IsCaptchaVisible = true;
                GenerateCaptcha();
                _measurementCompletion = new TaskCompletionSource<VitalMeasurement>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completionSource = _measurementCompletion;
                if (completionSource is null)
                {
                    throw new InvalidOperationException("runtime state is null");
                }

                LogInfo("Captcha", "Captcha dialog requested.");
                LogInfo("Captcha", "Captcha dialog shown.");
                LogInfo("Start", "ESP32 runtime stage waiting for CAPTCHA-confirmed start.");
                measurement = await completionSource.Task.WaitAsync(runCts.Token);

                if (measurement is null)
                {
                    throw new InvalidOperationException("measurement DTO is null");
                }

                // Анализ и остальное
                LogInfo("Start", "Save measurement starting");
                var (verdict, verdictColor, healthTiles) = CalculateDetailedDiagnosis(measurement);
                if (healthTiles is null)
                {
                    throw new InvalidOperationException("health tiles container is null");
                }

                DetailedHealthTiles = new ObservableCollection<HealthTile>(healthTiles);
                ApplyEmployeeStatusVisuals(measurement);
                UpdateTilesWithStatuses(healthTiles, verdict);
                string humanMessage = VerdictRecommendation;
                measurement.Diagnosis = $"{VerdictReason}. {VerdictRecommendation}";
                measurement.Recommendation = verdict;
                HumanRecommendation = humanMessage;
                ShowDetailedResult = false;
                LogInfo("History", $"History record creation started. EmployeeId={measurement.EmployeeId}, Timestamp={measurement.Timestamp:O}.");
                Measurements.Add(measurement);
                sessionMeasurements.Add(measurement);
                SaveMeasurementsToJson();
                LoadMeasurementsFromJson();
                LogInfo("History", $"History collection refreshed. History items count after refresh: {Measurements.Count}.");
                UpdateLastData();
                ResultMessage = FinalVerdict;
                ResultColor = verdictColor;
                LogInfo("Start", "Start completed");
            }
            catch (OperationCanceledException)
            {
                ResultMessage = "Измерение прервано.";
                ResultColor = Avalonia.Media.Brushes.DarkOrange;
                HardwareSafetyStatus = "Аварийная остановка.";
                LogWarning("Start", "Runtime stage failed: measurement cancelled.");
            }
            catch (Exception ex)
            {
                var message = ex is NullReferenceException
                    ? "Runtime stage failed: null dependency reached."
                    : $"Runtime stage failed: {ex.Message}";
                ResultMessage = $"Ошибка: {message}";
                ResultColor = Avalonia.Media.Brushes.Red;
                HardwareSafetyStatus = ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                    ? "Timeout"
                    : ex.Message.Contains("авар", StringComparison.OrdinalIgnoreCase)
                    ? "Аварийная остановка"
                    : "Измерение прервано";
                LogError("Start", message);
                LogError("Start", $"Start initialization failed but application remains alive: {ex}");
                Console.WriteLine($"Исключение в Start(): {ex}");
            }
            finally
            {
                IsCollectingData = false;
                StopCamera();
                IsOverlayVisible = false;
                IsCaptchaVisible = false;
                _measurementCompletion = null;
                measurementRunCts?.Dispose();
                measurementRunCts = null;
                OnPropertyChanged(nameof(CanEmergencyStop));
            }
        }

        private void EnsureCriticalUiStateInitialized()
        {
            Measurements ??= new ObservableCollection<VitalMeasurement>();
            Employees ??= new ObservableCollection<Employee>();
            DetailedHealthTiles ??= new ObservableCollection<HealthTile>();
            CaptchaItems ??= new ObservableCollection<int>();
            IsCaptchaSelected ??= new ObservableCollection<bool>();
            CaptchaBackgrounds ??= new ObservableCollection<IBrush>();
            CaptchaLefts ??= new ObservableCollection<double>();
            CaptchaTops ??= new ObservableCollection<double>();
            selectedCaptchaIndices ??= new List<int>();
            correctCaptchaIndices ??= new List<int>();
            sessionMeasurements ??= new List<VitalMeasurement>();

            while (CaptchaItems.Count < 9)
            {
                CaptchaItems.Add(0);
            }

            while (IsCaptchaSelected.Count < 9)
            {
                IsCaptchaSelected.Add(false);
            }

            while (CaptchaBackgrounds.Count < 9)
            {
                CaptchaBackgrounds.Add(Avalonia.Media.Brushes.White);
            }

            while (CaptchaLefts.Count < 9)
            {
                CaptchaLefts.Add(0);
            }

            while (CaptchaTops.Count < 9)
            {
                CaptchaTops.Add(0);
            }

            TopInfoMessage ??= string.Empty;
            TopInfoColor ??= Brushes.Red;
            ResultMessage ??= string.Empty;
            ResultColor ??= Brushes.Black;
            CameraMessage ??= string.Empty;
            CameraMessageColor ??= Brushes.Black;
            CaptchaPrompt ??= string.Empty;
            CaptchaMessage ??= string.Empty;
            SyncMessage ??= string.Empty;
            HardwareSafetyStatus ??= "Готов к работе.";
            FinalVerdict ??= string.Empty;
            VerdictReason ??= string.Empty;
            VerdictRecommendation ??= string.Empty;
            VerdictColor ??= Brushes.Gray;
            HumanRecommendation ??= string.Empty;
        }

        private void ResetVerdictDisplay()
        {
            IsVerdictVisible = false;
            FinalVerdict = string.Empty;
            VerdictReason = string.Empty;
            VerdictRecommendation = string.Empty;
            VerdictColor = Brushes.Transparent;
        }

        public string SyncEndpointUrl => TryBuildSyncEndpointUrl(AppliedServerIp, out var endpointUrl, out _) ? endpointUrl : string.Empty;

        private void GenerateCaptcha()
        {
            LogInfo("Captcha", $"Captcha challenge created. Platform={(OperatingSystem.IsLinux() ? "Linux" : "Windows")}.");
            var random = new Random();
            int targetDigit = random.Next(0, 10);  // Случайная цель от 0-9
            CaptchaPrompt = $"Проверка: Нажмите на все цифры {targetDigit}";

            var items = new List<int>();
            correctCaptchaIndices.Clear();
            int targetCount = 0;

            // Генерируем разнообразные цифры (минимум 2 target, и смесь других)
            while (targetCount < 2)
            {
                items.Clear();
                correctCaptchaIndices.Clear();
                targetCount = 0;
                for (int i = 0; i < 9; i++)
                {
                    int num = random.Next(0, 10);
                    // Чтобы больше разнообразия, иногда принудительно меняем, если слишком много одинаковых
                    if (random.Next(0, 2) == 0) num = random.Next(0, 10);  // Двойной random для вариации
                    items.Add(num);
                    if (num == targetDigit)
                    {
                        correctCaptchaIndices.Add(i);
                        targetCount++;
                    }
                }
                // Принудительно добавить 2 target если мало
                if (targetCount < 2)
                {
                    for (int j = targetCount; j < 2; j++)
                    {
                        int randIndex = random.Next(0, 9);
                        items[randIndex] = targetDigit;
                        if (!correctCaptchaIndices.Contains(randIndex))
                        {
                            correctCaptchaIndices.Add(randIndex);
                        }
                    }
                }
            }

            // Генерируем рандомные позиции без пересечения
            var usedPositions = new List<(double left, double top)>();
            for (int i = 0; i < 9; i++)
            {
                double left, top;
                bool overlaps;
                do
                {
                    left = random.Next(20, 400 - 50);
                    top = random.Next(0, 200 - 50);
                    overlaps = usedPositions.Any(p => Math.Abs(left - p.left) < 60 && Math.Abs(top - p.top) < 60);
                } while (overlaps);

                CaptchaLefts[i] = left;
                CaptchaTops[i] = top;
                usedPositions.Add((left, top));
            }

            // Заменяем элементы и фон
            for (int i = 0; i < 9; i++)
            {
                CaptchaItems[i] = items[i];
                CaptchaBackgrounds[i] = Avalonia.Media.Brushes.White;
            }

            selectedCaptchaIndices.Clear();
            CaptchaMessage = "";
            SelectedCount = 0;
            LogInfo("Captcha", $"Captcha dialog shown. Prompt='{CaptchaPrompt}'.");
        }


        [RelayCommand]
        private void CaptchaClick(object parameter)
        {
            int index = int.Parse(parameter.ToString());

            // Toggle выбор
            if (selectedCaptchaIndices.Contains(index))
            {
                selectedCaptchaIndices.Remove(index);
                CaptchaBackgrounds[index] = Avalonia.Media.Brushes.White;  // Отмена — белый
            }
            else
            {
                selectedCaptchaIndices.Add(index);
                CaptchaBackgrounds[index] = Avalonia.Media.Brushes.Gray;  // Выбор — серый
            }

            SelectedCount = selectedCaptchaIndices.Count;
        }


        [RelayCommand]
        private void CheckCaptcha()
        {
            // Проверяем, если все target выбраны и ничего лишнего
            var isCorrect = selectedCaptchaIndices.OrderBy(i => i).SequenceEqual(correctCaptchaIndices.OrderBy(i => i));
            if (isCorrect && correctCaptchaIndices.Any())
            {
                CaptchaMessage = "Верно!";
                IsCaptchaVisible = false;  // Скрываем CAPTCHA
                LogInfo("Captcha", "Captcha completed.");

                // Запускаем процесс измерений
                _ = StartMeasurementProcessAsync();  // Async, результат через TCS
            }
            else
            {
                CaptchaMessage = "Ошибка, попробуйте снова";
                LogWarning("Captcha", "Captcha cancelled or failed validation. Regenerating current challenge state.");
                selectedCaptchaIndices.Clear();
                SelectedCount = 0;

                // Сброс фонов кнопок на белый для новой попытки
                for (int i = 0; i < 9; i++)
                {
                    CaptchaBackgrounds[i] = Avalonia.Media.Brushes.White;
                }
            }
        }

        private async Task StartMeasurementProcessAsync()
        {
            try
            {
                if (currentEmployeeId <= 0)
                {
                    throw new InvalidOperationException("employee not resolved");
                }

                var cancellationToken = measurementRunCts?.Token ?? CancellationToken.None;
                LogInfo("Start", "ESP32 runtime stage starting after CAPTCHA.");
                var measurement = await ExecuteMeasurementScenarioAsync(currentEmployeeId, cancellationToken);
                _measurementCompletion?.TrySetResult(measurement);
            }
            catch (OperationCanceledException ex)
            {
                LogWarning("Captcha", "Captcha flow cancelled before measurement completion.");
                _measurementCompletion?.TrySetException(ex);
            }
            catch (Exception ex)
            {
                LogError("Captcha", $"Captcha-confirmed measurement start failed: {ex.Message}");
                _measurementCompletion?.TrySetException(ex);
            }
        }

        private async Task<VitalMeasurement> ExecuteMeasurementScenarioAsync(int employeeId, CancellationToken cancellationToken)
        {
            if (employeeId <= 0)
            {
                throw new InvalidOperationException("employee not resolved");
            }

            var measurement = await CaptureMeasurementAsync(
                employeeId,
                cancellationToken,
                new MeasurementWorkflowHooks
                {
                    OnStageAsync = HandleMeasurementWorkflowStageAsync
                });
            if (measurement is null)
            {
                throw new InvalidOperationException("measurement DTO is null");
            }

            LogInfo("Start", "Measurement scenario complete");
            return measurement;
        }

        private async Task HandleMeasurementWorkflowStageAsync(MeasurementWorkflowStage stage, CancellationToken cancellationToken)
        {
            switch (stage)
            {
                case MeasurementWorkflowStage.PrepareTemperature:
                    LogInfo("Start", "PrepareTemperature started");
                    LogInfo("Start", "Подготовка к температуре");
                    await RunPreparationStageAsync("Поднесите руку/лоб к датчику температуры", seconds: 3, progressStart: 0, progressEnd: 12, cancellationToken);
                    LogInfo("Start", "PrepareTemperature completed");
                    return;
                case MeasurementWorkflowStage.MeasureTemperature:
                    LogInfo("Start", "Temperature step starting");
                    LogInfo("Start", "MeasureTemperature command sending");
                    CurrentInstruction = "Измерение температуры";
                    ProgressValue = 18;
                    return;
                case MeasurementWorkflowStage.PrepareAlcohol:
                    LogInfo("Start", "PrepareAlcohol started");
                    LogInfo("Start", "Подготовка к алкотестеру");
                    await RunPreparationStageAsync("Подготовьтесь к измерению алкоголя и выполните продув", seconds: 4, progressStart: 22, progressEnd: 40, cancellationToken);
                    LogInfo("Start", "PrepareAlcohol completed");
                    return;
                case MeasurementWorkflowStage.MeasureAlcohol:
                    LogInfo("Start", "Alcohol step starting");
                    CurrentInstruction = "Измерение алкоголя";
                    ProgressValue = 46;
                    return;
                case MeasurementWorkflowStage.PreparePressure:
                    LogInfo("Start", "PreparePressure started");
                    LogInfo("Start", "Подготовка к давлению");
                    await RunPressurePreparationStageAsync(cancellationToken);
                    LogInfo("Start", "PreparePressure completed");
                    return;
                case MeasurementWorkflowStage.MeasurePressure:
                    LogInfo("Start", "Pressure step starting");
                    CurrentInstruction = "Измерение давления";
                    ProgressValue = 78;
                    return;
                case MeasurementWorkflowStage.ProcessingResults:
                    LogInfo("Start", "Обработка результатов");
                    CurrentInstruction = "Обработка результатов";
                    ProgressValue = 92;
                    return;
            }
        }

        private async Task RunPreparationStageAsync(
            string prompt,
            int seconds,
            double progressStart,
            double progressEnd,
            CancellationToken cancellationToken)
        {
            if (seconds <= 0)
            {
                CurrentInstruction = prompt;
                ProgressValue = progressEnd;
                return;
            }

            for (var remaining = seconds; remaining >= 1; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogInfo("Start", $"{prompt}: countdown tick {remaining}");
                CurrentInstruction = $"{prompt}. Начало через {remaining} c.";
                var progress = progressStart + ((seconds - remaining) / (double)seconds) * (progressEnd - progressStart);
                ProgressValue = progress;
                await Task.Delay(1000, cancellationToken);
            }

            CurrentInstruction = prompt;
            ProgressValue = progressEnd;
        }

        private async Task RunPressurePreparationStageAsync(CancellationToken cancellationToken)
        {
            const int totalSeconds = 30;
            const double progressStart = 52;
            const double progressEnd = 72;

            LogInfo("Start", "Pressure prep screen shown");
            CurrentInstruction = "Измерение давления. Пожалуйста, наденьте манжету и плотно закрепите на предплечье, при готовности нажмите старт";
            IsPressurePrepActive = true;
            pressurePrepCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                for (var remaining = totalSeconds; remaining >= 1; remaining--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PressurePrepCountdownText = $"В случае если кнопка не будет нажата, измерение начнется автоматически через: {remaining}";
                    LogInfo("Start", $"Pressure prep auto-start countdown: {remaining}");
                    ProgressValue = progressStart + ((totalSeconds - remaining) / (double)totalSeconds) * (progressEnd - progressStart);

                    var delayTask = Task.Delay(1000, cancellationToken);
                    var completedTask = await Task.WhenAny(pressurePrepCompletion.Task, delayTask);
                    if (completedTask == pressurePrepCompletion.Task)
                    {
                        await pressurePrepCompletion.Task;
                        ProgressValue = progressEnd;
                        PressurePrepCountdownText = string.Empty;
                        return;
                    }
                }

                LogInfo("Start", "Pressure prep auto-start triggered");
                pressurePrepCompletion.TrySetResult(true);
                ProgressValue = progressEnd;
                PressurePrepCountdownText = string.Empty;
            }
            finally
            {
                IsPressurePrepActive = false;
                pressurePrepCompletion = null;
            }
        }

        [RelayCommand]
        private void StartPressurePrep()
        {
            if (!IsPressurePrepActive || pressurePrepCompletion is null)
            {
                return;
            }

            LogInfo("Start", "Pressure prep manual start clicked");
            pressurePrepCompletion.TrySetResult(true);
        }

        [RelayCommand]
        private async Task EmergencyStop()
        {
            HardwareSafetyStatus = "Аварийная остановка.";
            CurrentInstruction = "Аварийная остановка";
            ResultMessage = "Измерение прервано / аварийная остановка.";
            ResultColor = Brushes.Red;
            ShowTopInfo("Старт временно заблокирован: выполнена аварийная остановка.", Brushes.Red);
            LogWarning("Safety", "Emergency stop requested");

            CancelMeasurementOperation("user emergency stop");
            CancelPreflightOperation("user emergency stop");

            try
            {
                if (hardwareMeasurementProvider is IEmergencyStoppableMeasurementProvider emergencyProvider)
                {
                    await emergencyProvider.EmergencyStopAsync("UI emergency stop");
                    HardwareSafetyStatus = "Оборудование сброшено.";
                    LogWarning("Safety", "Оборудование сброшено");
                }
            }
            catch (Exception ex)
            {
                LogError("Safety", $"Emergency stop failed: {ex.Message}");
            }
            finally
            {
                _measurementCompletion?.TrySetException(new OperationCanceledException("Emergency stop requested."));
                IsCollectingData = false;
                IsOverlayVisible = false;
                IsCaptchaVisible = false;
                ProgressValue = 0;
                CurrentInstruction = "Оборудование сброшено";
                OnPropertyChanged(nameof(CanEmergencyStop));
            }
        }

        private void CancelMeasurementOperation(string reason)
        {
            if (measurementRunCts is { IsCancellationRequested: false })
            {
                LogWarning("Start", $"Measurement cancellation source = measurementRunCts; cancellation reason = {reason}");
                measurementRunCts.Cancel();
            }
        }

        private void CancelPreflightOperation(string reason)
        {
            if (preflightCts is { IsCancellationRequested: false })
            {
                LogWarning("Preflight", $"Cancellation source = preflightCts; cancellation reason = {reason}");
                preflightCts.Cancel();
            }
        }


        [RelayCommand]
        private void ApplyServerIp()
        {
            var requestedIp = ServerIp?.Trim() ?? string.Empty;
            LogInfo("Sync", $"Sync server IP apply requested: {requestedIp}");

            if (!TryBuildSyncEndpointUrl(requestedIp, out var endpointUrl, out var validationError))
            {
                SyncMessage = $"Некорректный IP: {validationError}";
                ResultMessage = SyncMessage;
                ResultColor = Brushes.Red;
                LogWarning("Sync", $"Sync server IP validation failed: {validationError}");
                return;
            }

            AppliedServerIp = requestedIp;
            SaveDeviceRuntimeConfig(config => config.ServerIp = requestedIp);
            SyncMessage = $"Применено: {endpointUrl}";
            LogInfo("Sync", $"Sync server IP applied successfully: {requestedIp}");
            LogInfo("Sync", $"Sync endpoint URL updated to: {endpointUrl}");
            OnPropertyChanged(nameof(SyncEndpointUrl));
        }

        [RelayCommand]
        private async Task SyncData()
        {
            if (!TryBuildSyncEndpointUrl(AppliedServerIp, out var requestUrl, out var validationError))
            {
                var message = $"Синхронизация недоступна: {validationError}";
                SyncMessage = message;
                ResultMessage = message;
                ResultColor = Brushes.Red;
                LogWarning("Sync", $"Sync aborted: applied server IP invalid. Value='{AppliedServerIp}'. Reason={validationError}");
                return;
            }

            var payload = Measurements.ToList();
            var payloadPreview = string.Join("; ", payload.Take(2).Select(item =>
                $"EmployeeId={item.EmployeeId}, Timestamp={item.Timestamp:O}, Temp={item.Temperature:F2}, Alco={item.AlcoholLevel:F2}, SYS={item.BloodPressureSystolic:F0}, DAD={item.BloodPressureDiastolic:F0}, Recommendation={item.Recommendation ?? "n/a"}"));
            const int syncTimeoutSeconds = 10;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                SyncMessage = $"Синхронизация: отправка на {requestUrl}";
                LogInfo("Sync", $"Sync request using applied server IP: {AppliedServerIp}");
                LogInfo("Sync", $"Final request URL: {requestUrl}");
                LogInfo("Sync", "HTTP method: POST");
                LogInfo("Sync", $"HTTP timeout: {syncTimeoutSeconds}s");
                LogInfo("Sync", $"Payload count: {payload.Count}");
                LogInfo("Sync", $"Payload preview: {(string.IsNullOrWhiteSpace(payloadPreview) ? "empty" : payloadPreview)}");

                string jsonData = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(syncTimeoutSeconds)
                };
                var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(requestUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();
                stopwatch.Stop();

                var responsePreview = responseBody.Length > 400
                    ? responseBody[..400]
                    : responseBody;
                LogInfo("Sync", $"HTTP status code: {(int)response.StatusCode} {response.StatusCode}");
                LogInfo("Sync", $"Response body preview: {responsePreview}");
                LogInfo("Sync", $"Elapsed time: {stopwatch.ElapsedMilliseconds} ms");

                if (response.IsSuccessStatusCode)
                {
                    ResultMessage = "Данные синхронизированы успешно!";
                    ResultColor = Brushes.Green;
                    SyncMessage = $"Синхронизация успешна: {(int)response.StatusCode} {response.StatusCode}";
                }
                else
                {
                    ResultMessage = $"Ошибка синхронизации: {(int)response.StatusCode} {response.StatusCode}. Проверьте IP сервера сайта и доступность API.";
                    ResultColor = Brushes.Red;
                    SyncMessage = $"Сервер вернул ошибку: {(int)response.StatusCode} {response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var innerMessage = ex.InnerException?.Message ?? "none";
                LogError("Sync", $"Sync exception. Type={ex.GetType().FullName}; Message={ex.Message}; Inner={innerMessage}; FinalRequestUrl={requestUrl}; ServerIp={AppliedServerIp}");

                var friendlyMessage = ex switch
                {
                    TaskCanceledException => "Синхронизация не завершилась вовремя. Проверьте IP сервера сайта и сеть.",
                    HttpRequestException httpEx when httpEx.InnerException is not null => $"Не удалось подключиться к серверу сайта: {httpEx.InnerException.Message}",
                    HttpRequestException => "Не удалось выполнить HTTP-запрос к серверу сайта.",
                    _ => $"Исключение при синхронизации: {ex.Message}"
                };

                ResultMessage = friendlyMessage;
                ResultColor = Brushes.Red;
                SyncMessage = friendlyMessage;
            }
        }





        [RelayCommand]
        private void AddEmployee()  
        {
            LogInfo("Employees", $"Add employee clicked. Name='{NewEmployeeName ?? string.Empty}', CardId='{NewCardId ?? string.Empty}'.");

            var normalizedName = (NewEmployeeName ?? string.Empty).Trim();
            var normalizedCardId = (NewCardId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                LogWarning("Employees", "Add employee validation result: rejected. reason=name-empty");
                ShowTopInfo("Введите ФИО!", Brushes.Red);
                LogWarning("Employees", "Add employee rejected: reason=name-empty");
                return;
            }

            if (string.IsNullOrWhiteSpace(normalizedCardId))
            {
                LogWarning("Employees", "Add employee validation result: rejected. reason=cardid-empty");
                ShowTopInfo("Введите CardId!", Brushes.Red);
                LogWarning("Employees", "Add employee rejected: reason=cardid-empty");
                return;
            }

            if (Employees.Any(e => string.Equals(e.CardId?.Trim(), normalizedCardId, StringComparison.OrdinalIgnoreCase)))
            {
                LogWarning("Employees", $"Add employee validation result: rejected. reason=duplicate-cardid:{normalizedCardId}");
                ShowTopInfo("CardId уже существует!", Brushes.Red);
                LogWarning("Employees", $"Add employee rejected: reason=duplicate-cardid:{normalizedCardId}");
                return;
            }

            if (Employees.Any(e => string.Equals(e.Name?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                LogWarning("Employees", $"Add employee validation result: rejected. reason=duplicate-name:{normalizedName}");
                ShowTopInfo("Сотрудник с таким ФИО уже есть!", Brushes.Red);
                LogWarning("Employees", $"Add employee rejected: reason=duplicate-name:{normalizedName}");
                return;
            }

            LogInfo("Employees", "Add employee validation result: accepted");

            var newId = Employees.Any() ? Employees.Max(e => e.Id) + 1 : 1;
            var newEmployee = new Employee { Id = newId, Name = normalizedName, CardId = normalizedCardId, FaceData = null };

            try
            {
                LogInfo("Employees", $"Add employee save started. Id={newEmployee.Id}, CardId={newEmployee.CardId}.");
                Employees.Add(newEmployee);

                if (!SaveEmployeesToJson())
                {
                    Employees.Remove(newEmployee);
                    ShowTopInfo("Не удалось сохранить сотрудника.", Brushes.Red);
                    LogError("Employees", $"Add employee rejected: reason=save-failed. CardId={newEmployee.CardId}.");
                    return;
                }

                LogInfo("Employees", $"Add employee save completed. Id={newEmployee.Id}, CardId={newEmployee.CardId}.");
                LogInfo("Employees", $"Employees collection count after add: {Employees.Count}");

                CardId = newEmployee.CardId;
                currentEmployeeId = newEmployee.Id;
                IsEmployeeFound = true;
                UpdateEmployeeStatus(newEmployee);
                OnPropertyChanged(nameof(CanStartWorkflow));
                OnPropertyChanged(nameof(StartAvailabilityReason));
                ShowTopInfo("Сотрудник добавлен и подтверждён", Brushes.Green);

                NewEmployeeName = string.Empty;
                NewCardId = string.Empty;
            }
            catch (Exception ex)
            {
                if (Employees.Contains(newEmployee))
                {
                    Employees.Remove(newEmployee);
                }

                LogError("Employees", $"Add employee rejected: reason=exception. Type={ex.GetType().FullName}; Message={ex.Message}");
                ShowTopInfo("Не удалось добавить сотрудника.", Brushes.Red);
            }
        }


        // Новая функция для обновления плиток со статусами
        private void UpdateTilesWithStatuses(List<HealthTile> tiles, string verdict)
        {
            var hrTile = tiles.FirstOrDefault(t => t.Title == "ЧСС");
            LastHeartRate = hrTile != null ? $"{hrTile.Value} уд/мин — {hrTile.Status}" : "Нет данных";

            var satTile = tiles.FirstOrDefault(t => t.Title == "SpO₂");
            LastSaturation = satTile != null ? $"{satTile.Value} — {satTile.Status}" : "Нет данных";

            var bpTile = tiles.FirstOrDefault(t => t.Title == "АД");
            LastBloodPressure = bpTile != null ? $"{bpTile.Value} мм рт. ст. — {bpTile.Status}" : "Нет данных";

            var tempTile = tiles.FirstOrDefault(t => t.Title == "Темп.");
            LastTemperature = tempTile != null ? $"{tempTile.Value} — {tempTile.Status}" : "Нет данных";

            var glucTile = tiles.FirstOrDefault(t => t.Title == "Глюкоза");
            LastGlucose = glucTile != null ? $"{glucTile.Value} ммоль/л — {glucTile.Status}" : "Нет данных";

            var cholTile = tiles.FirstOrDefault(t => t.Title == "Холестерин");
            LastCholesterol = cholTile != null ? $"{cholTile.Value} ммоль/л — {cholTile.Status}" : "Нет данных";

            var alcTile = tiles.FirstOrDefault(t => t.Title == "Алкоголь");
            LastAlcoholLevel = alcTile != null ? $"{alcTile.Value} — {alcTile.Status}" : "Нет данных";

            var actTile = tiles.FirstOrDefault(t => t.Title == "Активность");
            LastActivityLevel = actTile != null ? $"{actTile.Value} шагов — {actTile.Status}" : "Нет данных";

            // Для общей рекомендации (вердикта) в 9-й плитке
            LastRecommendation = verdict;
        }


        // Команда для завершения сессии и сохранения общего PDF
        [RelayCommand]
        private void FinishSession()
        {
            if (sessionMeasurements.Count == 0)
            {
                InstructionMessage = "Нет данных для отчёта.";
                return;
            }

            GenerateSessionReport();
            sessionMeasurements.Clear();
            ResetForNextEmployee();
            InstructionMessage = "Сессия завершена. Отчёт сохранён.";
        }

        /// <summary>
        /// Симуляция чтения ID пропуска.
        /// </summary>
        //[RelayCommand]
        //private async Task SimulateCardRead()
        //{
        //    InstructionMessage = "Симулирую чтение пропуска...";
        //    await Task.Delay(1000);  // Имитация задержки считывания

        //    var cardIds = GetExistingCardIds();  // Получаем реальные ID из БД
        //    if (cardIds.Count == 0)
        //    {
        //        InstructionMessage = "Нет тестовых сотрудников в БД.";
        //        return;
        //    }

        //    var random = new Random();
        //    CardId = cardIds[random.Next(cardIds.Count)];  // Случайный реальный CardId

        //    await CheckEmployeeByCardId();

        //    if (IsEmployeeFound)
        //    {
        //        InstructionMessage = "Сотрудник найден. Продолжаем...";
        //    }
        //    else
        //    {
        //        InstructionMessage = "Сотрудник не найден. Попробуйте другой пропуск.";
        //        ResultMessage = "Сотрудник не найден";
        //        ResultColor = Avalonia.Media.Brushes.Red;
        //        ResetForNextEmployee();
        //    }
        //}


            
        [RelayCommand]
        private async Task SimulateCardRead()
        {
            if (IsStrictHardwareMode)
            {
                ShowTopInfo("Симуляция пропуска отключена в strict production mode.", Brushes.Red);
                LogWarning("CardReader", "Card simulation blocked in strict production mode.");
                return;
            }

            if (Employees.Any())
            {
                var random = new Random();
                var emp = Employees.ElementAt(random.Next(Employees.Count));
                CardId = emp.CardId;
                Console.WriteLine($"Симулирован пропуск: {emp.CardId}");
            }
            else
            {
                CardId = "123456";
            }

            await CheckEmployeeByCardId(); // это включит кнопку Старт
        }

        //// Новая команда для автотеста (симуляция нескольких сотрудников подряд)
        //[RelayCommand]
        //private async Task AutoTest()
        //{
        //    const int numEmployees = 5;  // Количество для теста
        //    var startTime = DateTime.Now;

        //    for (int i = 0; i < numEmployees; i++)
        //    {
        //        Console.WriteLine($"Автотест: Симуляция сотрудника {i + 1}/{numEmployees}");
        //        await SimulateCardRead();  // Симулируем чтение

        //        if (!IsEmployeeFound) continue;  // Если не найден, пропускаем

        //        // Выполняем полный цикл (камера + измерения + вердикт)
        //        bool verified = await VerifyUserWithCamera();
        //        if (!verified) continue;

        //        var measurement = await PerformMeasurementsAsync();
        //        var (recommendation, color) = CalculateAdmission(measurement);
        //        measurement.Recommendation = recommendation;


        //        GenerateReport(measurement);

        //        ResetForNextEmployee();
        //        await Task.Delay(500);  // Пауза между сотрудниками для имитации
        //    }

        //    var endTime = DateTime.Now;
        //    var duration = endTime - startTime;
        //    Console.WriteLine($"Автотест завершён. Время на {numEmployees} сотрудников: {duration.TotalSeconds} сек.");
        //    InstructionMessage = $"Автотест завершён ({duration.TotalSeconds} сек).";
        //}

        /// <summary>
        /// Получение списка существующих CardId из БД для симуляции.
        /// </summary>

        /// <summary>
        /// Проверка сотрудника по CardId в БД.
        /// </summary>
        private async Task CheckEmployeeByCardId()
        {
            await Task.Delay(100);

            var employee = Employees.FirstOrDefault(e => e.CardId == CardId);

            if (employee != null)
            {
                IsEmployeeFound = true;
                currentEmployeeId = employee.Id;
                UpdateEmployeeStatus(employee);
                Console.WriteLine($"Найден сотрудник ID={employee.Id}");
            }
            else
            {
                IsEmployeeFound = false;
                currentEmployeeId = 0;
                UpdateEmployeeStatus();
                Console.WriteLine($"Сотрудник с CardId={CardId} не найден");
            }
        }

        /// <summary>
        /// Симуляция последовательных измерений с подсказками.
        /// </summary>
        /// <summary>
        /// Симуляция последовательных измерений с подсказками (рандом для разных вердиктов).
        /// </summary>
        private async Task<VitalMeasurement> PerformMeasurementsAsync()
        {
            var measurement = new VitalMeasurement { EmployeeId = currentEmployeeId, Timestamp = DateTime.Now };
            var random = new Random();

            InstructionMessage = "Дуньте в алкотестер...";
            await Task.Delay(2000);
            measurement.AlcoholLevel = random.NextDouble() * 1.0;
            InstructionMessage = "Алкотестер: OK";

            InstructionMessage = "Приложите палец к датчику ЧСС/сатурации...";
            await Task.Delay(3000);
            measurement.HeartRate = random.Next(50, 130);
            measurement.Saturation = random.Next(85, 100);
            InstructionMessage = "ЧСС и сатурация: OK";

            InstructionMessage = "Измерьте давление...";
            await Task.Delay(4000);
            measurement.BloodPressureSystolic = random.Next(85, 150);
            measurement.BloodPressureDiastolic = random.Next(55, 95);
            InstructionMessage = "Давление: OK";

            InstructionMessage = "Измерьте температуру...";
            await Task.Delay(2000);
            measurement.Temperature = random.NextDouble() * (38.0 - 35.5) + 35.5;
            InstructionMessage = "Температура: OK";

            InstructionMessage = "Измерьте глюкозу...";
            await Task.Delay(3000);
            measurement.Glucose = random.NextDouble() * (7.5 - 3.5) + 3.5;
            InstructionMessage = "Глюкоза: OK";

            InstructionMessage = "Измерьте холестерин...";
            await Task.Delay(3000);
            measurement.Cholesterol = random.NextDouble() * (6.5 - 4.0) + 4.0;
            InstructionMessage = "Холестерин: OK";

            InstructionMessage = "Регистрация ЭКГ и активности...";
            await Task.Delay(5000);
            measurement.EcgData = "simulated_ecg";
            measurement.ActivityLevel = random.Next(1000, 3000);
            InstructionMessage = "ЭКГ и активность: OK";

            InstructionMessage = "Измерения завершены.";
            return measurement;
        }


        /// <summary>
        /// Генерация общего отчёта в PDF для сессии.
        /// </summary>
        private void GenerateSessionReport()
        {
            if (sessionMeasurements.Count == 0)
            {
                InstructionMessage = "Нет данных для отчёта.";
                return;
            }

            var pdf = new PdfDocument();
            var font = new XFont("Arial", 12, XFontStyle.Regular);
            var boldFont = new XFont("Arial", 14, XFontStyle.Bold);
            int pageNum = 1;

            foreach (var m in sessionMeasurements)
            {
                var page = pdf.AddPage();
                var gfx = XGraphics.FromPdfPage(page);
                double y = 20;

                void WriteLine(string text, bool bold = false)
                {
                    gfx.DrawString(text, bold ? boldFont : font, XBrushes.Black, new XRect(50, y, page.Width - 100, 20), XStringFormats.TopLeft);
                    y += 25;
                }

                WriteLine($"Страница {pageNum}: Сотрудник ID: {m.EmployeeId}", true);
                WriteLine($"Время: {m.Timestamp:dd.MM.yyyy HH:mm:ss}");
                WriteLine("");

                // ←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←
                // ВОТ ЭТО ГЛАВНОЕ — выводим подробный анализ
                if (!string.IsNullOrEmpty(m.Diagnosis))
                {
                    WriteLine("Анализ состояния:", true);
                    var lines = m.Diagnosis.Split('\n');
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            WriteLine($"• {line}");
                    }
                }
                else
                {
                    WriteLine("Подробный анализ отсутствует.");
                }
                // ←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←←

                WriteLine("");
                WriteLine($"ИТОГОВАЯ РЕКОМЕНДАЦИЯ:", true);
                WriteLine(m.Recommendation, true);

                pageNum++;
            }

            var fileName = $"Session_Report_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            pdf.Save(fullPath);
            Console.WriteLine($"PDF-отчёт сохранён: {fullPath}");
            InstructionMessage = $"Отчёт сохранён: {fileName}";
        }


        private string GenerateHumanRecommendation(VitalMeasurement measurement, List<HealthTile> healthTiles)
        {
            var sb = new StringBuilder();
            var random = new Random();  // Для вариативности

            // Получаем ФИО сотрудника для персонализации
            var employee = Employees.FirstOrDefault(e => e.Id == measurement.EmployeeId);
            string employeeName = employee?.Name ?? "Сотрудник";

            // Короткий анализ: 1 предложение с ключевыми отклонениями
            string analysis = "Все показатели в норме.";
            var criticalTiles = healthTiles.Where(t => t.Color == Avalonia.Media.Brushes.Red || t.Color == Avalonia.Media.Brushes.DarkRed).ToList();
            var warningTiles = healthTiles.Where(t => t.Color == Avalonia.Media.Brushes.Orange || t.Color == Avalonia.Media.Brushes.Yellow).ToList();

            if (criticalTiles.Any())
            {
                string[] criticalPhrases = { $"Критические отклонения в {string.Join(", ", criticalTiles.Select(t => t.Title))}: ", $"Обнаружены серьёзные проблемы с {string.Join(" и ", criticalTiles.Select(t => t.Title.ToLower()))}: " };
                analysis = criticalPhrases[random.Next(criticalPhrases.Length)] + string.Join("; ", criticalTiles.Select(t => $"{t.Value} ({t.Status})"));
            }
            else if (warningTiles.Any())
            {
                string[] warningPhrases = { $"Некоторые показатели требуют внимания ({string.Join(", ", warningTiles.Select(t => t.Title))}: ", $"Предупреждения по {string.Join(" и ", warningTiles.Select(t => t.Title.ToLower()))}: " };
                analysis = warningPhrases[random.Next(warningPhrases.Length)] + string.Join("; ", warningTiles.Select(t => $"{t.Value} ({t.Status})"));
            }

            // Второе предложение: совет + вердикт
            string[] advicePhrases = { $"Рекомендация: {measurement.Recommendation}. ", $"Вердикт: {measurement.Recommendation}. " };
            string advice = advicePhrases[random.Next(advicePhrases.Length)];
            if (criticalTiles.Any()) advice += "Немедленно обратитесь к врачу и отстранитесь от работы.";
            else if (warningTiles.Any()) advice += "Мониторьте состояние, избегайте нагрузок.";
            else advice += "Можете приступать к работе.";

            // Персонализация
            sb.AppendLine($"{employeeName}, {analysis} {advice}");

            return sb.ToString();
        }


        private void SaveMeasurementsToJson()
        {
            try
            {
                var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MeasurementsJsonPath);
                LogInfo("History", $"Platform-specific history path resolved to: {jsonPath}");

                // Чтобы сохранить все существующие измерения, сначала загружаем текущий JSON (если есть)
                List<VitalMeasurement> allMeasurements = new List<VitalMeasurement>();
                if (File.Exists(jsonPath))
                {
                    var existingJson = File.ReadAllText(jsonPath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var existing = JsonSerializer.Deserialize<List<VitalMeasurement>>(existingJson, options);
                    if (existing != null)
                    {
                        allMeasurements.AddRange(existing);
                    }
                }

                // Добавляем новые из текущей коллекции Measurements, если они не дубликаты (по Id или Timestamp)
                foreach (var meas in Measurements)
                {
                    if (!allMeasurements.Any(m => m.Id == meas.Id && m.Timestamp == meas.Timestamp))
                    {
                        allMeasurements.Add(meas);
                    }
                }

                // Сортируем или оставляем как есть
                allMeasurements = allMeasurements.OrderByDescending(m => m.Timestamp).ToList();

                var json = JsonSerializer.Serialize(allMeasurements, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonPath, json);
                LogInfo("History", $"History record saved. Count={allMeasurements.Count}.");
                Console.WriteLine($"Saved {allMeasurements.Count} measurements to {jsonPath}.");
            }
            catch (Exception ex)
            {
                LogError("History", $"History save failed: {ex.Message}");
                Console.WriteLine($"Error saving measurements: {ex.Message}");
            }
        }

        private void LoadMeasurementsFromJson()
        {
            try
            {
                var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MeasurementsJsonPath);
                LogInfo("History", $"History load path: {jsonPath}");
                if (File.Exists(jsonPath))
                {
                    var json = File.ReadAllText(jsonPath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var loadedMeasurements = JsonSerializer.Deserialize<List<VitalMeasurement>>(json, options);
                    if (loadedMeasurements != null)
                    {
                        // Загружаем все измерения для всех сотрудников, без фильтра
                        var allSorted = loadedMeasurements
                            .OrderByDescending(m => m.Timestamp)
                            .ToList();

                        Measurements = new ObservableCollection<VitalMeasurement>(allSorted);
                        LogInfo("History", $"History load result count: {Measurements.Count}.");
                        Console.WriteLine($"Loaded {Measurements.Count} measurements (all employees).");
                    }
                    else
                    {
                        LogWarning("History", "History load result count: 0 (deserialized collection is null).");
                        Console.WriteLine("No measurements loaded from JSON.");
                    }
                }
                else
                {
                    LogWarning("History", "History storage file not found during load.");
                    Console.WriteLine($"Measurements JSON file not found at {jsonPath}.");
                }
            }
            catch (Exception ex)
            {
                LogError("History", $"History load failed: {ex.Message}");
                Console.WriteLine($"Error loading measurements: {ex.Message}");
            }
        }

        private bool SaveEmployeesToJson()
        {
            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, EmployeesJsonPath);
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(Employees.ToList(), options);
                File.WriteAllText(fullPath, json);
                Console.WriteLine($"Сотрудники сохранены в {EmployeesJsonPath}");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Employees", $"Employees JSON save failed: {ex.Message}");
                Console.WriteLine($"Ошибка записи JSON сотрудников: {ex.Message}");
                return false;
            }
        }

        private void LoadEmployeesFromJson()
        {
            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, EmployeesJsonPath);

            if (!File.Exists(fullPath))
            {
                Employees = new ObservableCollection<Employee>();
                return;
            }

            try
            {
                var json = File.ReadAllText(fullPath);
                var list = JsonSerializer.Deserialize<List<Employee>>(json);
                Employees = list != null
                    ? new ObservableCollection<Employee>(list)
                    : new ObservableCollection<Employee>();
                Console.WriteLine($"Загружено {Employees.Count} сотрудников из JSON");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка чтения employees.json: {ex.Message}");
                Employees = new ObservableCollection<Employee>();
            }
            if (!Employees.Any())
            {
                // Тестовые данные
                Employees.Add(new Employee { Id = 1, Name = "Test User", CardId = "123456" });
                SaveEmployeesToJson();
            }
        }

        private void UpdateLastData()
        {
            if (Measurements.Any())
            {
                var last = Measurements.Last();

                LastHeartRate = $"{last.HeartRate:F0} уд/мин";  // Уже string, добавьте статус, если нужно
                LastSaturation = $"{last.Saturation:F0}%";
                LastBloodPressure = $"{last.BloodPressureSystolic:F0}/{last.BloodPressureDiastolic:F0}";
                LastTemperature = $"{last.Temperature:F1}°C";

                // Теперь string с форматированием (исправление ошибок)
                LastGlucose = $"{last.Glucose:F1} ммоль/л";
                LastCholesterol = $"{last.Cholesterol:F1} ммоль/л";
                LastAlcoholLevel = $"{last.AlcoholLevel:F2}‰";
                LastActivityLevel = $"{last.ActivityLevel} шагов";

                LastRecommendation = last.Recommendation ?? "Нет данных";
            }
        }
        /// <summary>
        /// Сброс для следующего сотрудника.
        /// </summary>
        private void ResetForNextEmployee()
        {
            CardId = string.Empty;
            IsEmployeeFound = false;
            SetCameraFrame(null);
            InstructionMessage = "Готово. Подходите следующий сотрудник.";
            ResultMessage = string.Empty;
            ResultColor = Avalonia.Media.Brushes.Black;
            CameraMessage = string.Empty;
            CameraMessageColor = Avalonia.Media.Brushes.Black;
            UpdateEmployeeStatus();
        }
        /// <summary>
        /// Генерация отчета в PDF.
        /// </summary>
        private void GenerateReport(VitalMeasurement m)
        {
            var document = new PdfDocument();
            var page = document.AddPage();
            var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 12, XFontStyle.Regular);

            gfx.DrawString($"Отчет для сотрудника ID: {m.EmployeeId}", font, XBrushes.Black, new XRect(10, 10, page.Width, page.Height), XStringFormats.TopLeft);
            gfx.DrawString($"Время: {m.Timestamp}", font, XBrushes.Black, new XRect(10, 30, page.Width, page.Height), XStringFormats.TopLeft);
            // Добавьте все параметры аналогично
            gfx.DrawString($"Рекомендация: {m.Recommendation}", font, XBrushes.Black, new XRect(10, 200, page.Width, page.Height), XStringFormats.TopLeft);

            var reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Report_{m.EmployeeId}_{m.Timestamp:yyyyMMddHHmm}.pdf");
            document.Save(reportPath);
            Console.WriteLine($"Отчет сохранен: {reportPath}");
        }

        // Остальные методы без изменений (InitializeCapture, LoadYoloModel, VerifyUserWithCamera, CalculateAdmission, SaveMeasurement, etc.)
        // Удалите старый SimulateDataCollection, если он не нужен
        // В XAML (MainWindow.axaml) добавьте: <TextBlock Text="{Binding InstructionMessage}" FontWeight="Bold" />


        /// <summary>
        /// Инициализация камеры.
        /// </summary>
        private void InitializeCapture()
        {
            var devices = GetLinuxVideoDevices();
            var selectedDevice = GetPrimaryLinuxVideoDevice(devices);

            if (OperatingSystem.IsLinux())
            {
                InitializeLinuxCapture(selectedDevice, devices);
                return;
            }

            InitializeNonLinuxCapture(selectedDevice, devices);
        }

        private void InitializeNonLinuxCapture(string? selectedDevice, IReadOnlyList<string> devices)
        {
            try
            {
                LogInfo("Camera", devices.Count == 0
                    ? "Camera devices: none"
                    : $"Camera devices: {string.Join(", ", devices)}");

                if (OperatingSystem.IsLinux())
                {
                    LogInfo("Camera", $"Selected Linux camera device: {selectedDevice ?? "not found"} (backend V4L2).");

                    if (!IsCurrentUserInLinuxGroup("video"))
                    {
                        LogWarning("Camera", $"Пользователь '{Environment.UserName}' не состоит в группе video. Доступ к {selectedDevice ?? "/dev/video0"} может быть ограничен.");
                    }
                }

                if (OperatingSystem.IsLinux() && selectedDevice is null)
                {
                    LogError("Camera", "Основное Linux camera device /dev/video0 не найдено.");
                    capture = null;
                    return;
                }

                capture?.Dispose();
                capture = OperatingSystem.IsLinux()
                    ? new VideoCapture(0, VideoCapture.API.V4L2)
                    : new VideoCapture(0);

                if (!capture.IsOpened)
                {
                    LogError("Camera", OperatingSystem.IsLinux()
                        ? $"Camera open failed: {selectedDevice ?? "/dev/video0"} via V4L2."
                        : "Camera open failed.");
                    capture = null;
                }
                else
                {
                    LogInfo("Camera", OperatingSystem.IsLinux()
                        ? $"Camera open success: {selectedDevice ?? "/dev/video0"} via V4L2."
                        : "Camera open success.");
                }
            }
            catch (Exception ex)
            {
                LogError("Camera", OperatingSystem.IsLinux()
                    ? $"Camera open failed: {selectedDevice ?? "/dev/video0"} via V4L2 ({ex.Message})"
                    : $"Camera open failed: {ex.Message}");
                capture = null;
                return;
            }

            if (capture is null || !capture.IsOpened)
            {
                return;
            }

            try
            {
                using var probeFrame = new Mat();
                if (capture.Read(probeFrame) && !probeFrame.IsEmpty)
                {
                    LogInfo("Camera", $"Camera frame received: {probeFrame.Width}x{probeFrame.Height}.");

                    using var decodedBitmap = TryConvertMatToSkBitmap(probeFrame, out var decodeError);
                    if (decodedBitmap is null)
                    {
                        LogWarning("Camera", $"Preview conversion step failed: {decodeError ?? "unknown error"}");
                        CameraMessage = "Камера подключена, превью недоступно.";
                        CameraMessageColor = Avalonia.Media.Brushes.DarkOrange;
                        return;
                    }

                    var cameraRotation = LoadDeviceRuntimeConfig().CameraRotation;
                    using var processedBitmap = ApplyCameraRotation(decodedBitmap, cameraRotation);
                    using var avaloniaBitmap = TryConvertSkBitmapToAvaloniaBitmap(processedBitmap, out var previewError);
                    if (avaloniaBitmap is null)
                    {
                        LogWarning("Camera", $"Preview bitmap step failed: {previewError ?? "unknown error"}");
                        CameraMessage = "Камера подключена, превью недоступно.";
                        CameraMessageColor = Avalonia.Media.Brushes.DarkOrange;
                        return;
                    }

                    LogInfo("Camera", "Preview conversion ready.");
                }
                else
                {
                    LogWarning("Camera", $"Камера {selectedDevice ?? "device 0"} открылась, но первый кадр не получен.");
                }
            }
            catch (Exception ex)
            {
                LogError("Camera", $"Camera frame probe failed: {ex.Message}");
            }
        }

        private void InitializeLinuxCapture(string? selectedDevice, IReadOnlyList<string> devices)
        {
            linuxCameraAvailable = false;
            capture?.Dispose();
            capture = null;

            LogInfo("Camera", devices.Count == 0
                ? "Camera devices: none"
                : $"Camera devices: {string.Join(", ", devices)}");
            LogInfo("Camera", $"Camera open started: {selectedDevice ?? "/dev/video0"} via Linux fallback path.");

            if (!LinuxCameraFrameGrabber.TryGrabFrame(out var frame, out var devicePath, out var error))
            {
                LogError("Camera", $"Camera open failed: {error}");
                return;
            }

            using (frame)
            {
                linuxCameraAvailable = true;
                LogInfo("Camera", $"Camera open success: {devicePath ?? selectedDevice ?? "/dev/video0"} via Linux fallback path.");
                LogInfo("Camera", $"Linux raw frame received: {frame.Width}x{frame.Height}.");
                using var previewBitmap = CreatePreviewBitmapForUi(frame, LoadDeviceRuntimeConfig().CameraRotation);
                var avaloniaBitmap = TryConvertSkBitmapToAvaloniaBitmap(previewBitmap, out var previewError);
                if (avaloniaBitmap is null)
                {
                    SetCameraFrame(null);
                    CameraMessage = "Камера подключена, кадр получен, но preview недоступен.";
                    CameraMessageColor = Avalonia.Media.Brushes.DarkOrange;
                    LogWarning("Camera", $"Linux fallback preview conversion failed: {previewError ?? "unknown error"}");
                    return;
                }

                SetCameraFrame(avaloniaBitmap);
                CameraMessage = "Камера подключена, Linux fallback кадр показан.";
                CameraMessageColor = Avalonia.Media.Brushes.Green;
                LogInfo("Camera", "Linux CameraFrame assigned from rotated bitmap.");
            }
        }

        private sealed class PresenceCheckResult
        {
            public static PresenceCheckResult Verified() => new(true, false, "Пользователь верифицирован.");
            public static PresenceCheckResult Rejected(string message) => new(false, false, message);
            public static PresenceCheckResult TechnicalFailure(string message) => new(false, true, message);

            private PresenceCheckResult(bool isVerified, bool shouldContinueWithoutCamera, string message)
            {
                IsVerified = isVerified;
                ShouldContinueWithoutCamera = shouldContinueWithoutCamera;
                Message = message;
            }

            public bool IsVerified { get; }
            public bool ShouldContinueWithoutCamera { get; }
            public string Message { get; }
        }

        private static string DescribeCameraProcessingNull(string stage, object? instance) =>
            instance is null
                ? $"Camera processing failed: {stage} is null."
                : $"Camera processing failed: {stage}.";

        private async Task<PresenceCheckResult> VerifyUserWithCamera()
        {
            if (!OperatingSystem.IsLinux())
            {
                LoadYoloModel();
            }

            InitializeCapture();

            if (!OperatingSystem.IsLinux() && (capture == null || !capture.IsOpened))
            {
                const string message = "Камера не инициализирована или недоступна.";
                Console.WriteLine(message);
                LogError("Presence", message);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ResultMessage = message;
                    ResultColor = Avalonia.Media.Brushes.Red;
                });
                return PresenceCheckResult.TechnicalFailure(message);
            }

            if (!OperatingSystem.IsLinux() && yoloEngine == null)
            {
                const string message = "YOLO модель не загружена. Проверка невозможна.";
                Console.WriteLine(message);
                LogError("Presence", message);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ResultMessage = message;
                    ResultColor = Avalonia.Media.Brushes.Red;
                });
                return PresenceCheckResult.TechnicalFailure(message);
            }

            var start = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(20);
            var attempts = 0;
            var emptyFrameAttempts = 0;
            var processingErrors = 0;
            var cameraRotation = LoadDeviceRuntimeConfig().CameraRotation;
            const int maxEmptyFrameAttempts = 12;
            const int maxProcessingErrors = 5;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResultMessage = "Контроль присутствия: убедитесь, что в кадре 1 человек.";
                ResultColor = Avalonia.Media.Brushes.Black;
            });

            while (DateTime.UtcNow - start < timeout)
            {
                try
                {
                    if (OperatingSystem.IsLinux())
                    {
                        using var linuxFrame = ReadLinuxCameraFrame(cameraRotation, out var linuxError);
                        if (linuxFrame is null)
                        {
                            emptyFrameAttempts++;
                            attempts++;

                            if (emptyFrameAttempts >= maxEmptyFrameAttempts)
                            {
                                var noFrameMessage = $"Камера открыта, но не отдаёт кадр. {linuxError}";
                                Console.WriteLine(noFrameMessage);
                                LogError("Presence", noFrameMessage);
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    ResultMessage = noFrameMessage;
                                    ResultColor = Avalonia.Media.Brushes.Red;
                                });
                                return PresenceCheckResult.TechnicalFailure(noFrameMessage);
                            }

                            await Task.Delay(120);
                            continue;
                        }

                        emptyFrameAttempts = 0;
                        LogInfo("Camera", $"Camera frame received. Attempt={attempts}, size={linuxFrame.Width}x{linuxFrame.Height}.");
                        var linuxFrameResult = await ProcessLinuxPresenceFrameAsync(linuxFrame, attempts, processingErrors, maxProcessingErrors, cameraRotation);
                        processingErrors = linuxFrameResult.ProcessingErrors;
                        if (linuxFrameResult.Result is not null)
                        {
                            return linuxFrameResult.Result;
                        }

                        attempts++;
                        await Task.Delay(120);
                        continue;
                    }

                    using Mat frame = new Mat();
                    if (!capture.Read(frame) || frame.IsEmpty)
                    {
                        emptyFrameAttempts++;
                        attempts++;

                        if (emptyFrameAttempts >= maxEmptyFrameAttempts)
                        {
                            const string noFrameMessage = "Камера открыта, но не отдаёт кадр. Проверка остановлена.";
                            Console.WriteLine(noFrameMessage);
                            LogError("Presence", noFrameMessage);
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                ResultMessage = noFrameMessage;
                                ResultColor = Avalonia.Media.Brushes.Red;
                            });
                            return PresenceCheckResult.TechnicalFailure(noFrameMessage);
                        }

                        await Task.Delay(120);
                        continue;
                    }

                    emptyFrameAttempts = 0;

                    using var decodedBitmap = TryConvertMatToSkBitmap(frame);
                    if (decodedBitmap is null)
                    {
                        processingErrors++;
                        var decodeMessage = DescribeCameraProcessingNull("decodedBitmap", decodedBitmap);
                        LogError("Presence", decodeMessage);

                        if (processingErrors >= maxProcessingErrors)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                ResultMessage = "Не удалось преобразовать кадр камеры для проверки присутствия.";
                                ResultColor = Avalonia.Media.Brushes.Red;
                            });
                            return PresenceCheckResult.TechnicalFailure(decodeMessage);
                        }

                        await Task.Delay(120);
                        continue;
                    }

                    var windowsFrameResult = await ProcessPresenceFrameAsync(decodedBitmap, attempts, processingErrors, maxProcessingErrors, cameraRotation);
                    processingErrors = windowsFrameResult.ProcessingErrors;
                    if (windowsFrameResult.Result is not null)
                    {
                        return windowsFrameResult.Result;
                    }

                    attempts++;
                    await Task.Delay(120);
                }
                catch (Exception ex)
                {
                    processingErrors++;
                    Console.WriteLine($"Ошибка в VerifyUserWithCamera: {ex.Message}. Stack: {ex.StackTrace}");

                    if (processingErrors >= maxProcessingErrors)
                    {
                        var fatalMessage = $"Ошибка обработки камеры/YOLO: {ex.Message}";
                        LogError("Presence", fatalMessage);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            ResultMessage = fatalMessage;
                            ResultColor = Avalonia.Media.Brushes.Red;
                        });
                        return PresenceCheckResult.TechnicalFailure(fatalMessage);
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ResultMessage = "Ошибка обработки камеры. Повтор попытки…";
                        ResultColor = Avalonia.Media.Brushes.Orange;
                    });

                    await Task.Delay(120);
                }
            }

            const string timeoutMessage = "В кадре не зафиксирован сотрудник, измерение отменено";
            LogWarning("Presence", timeoutMessage);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResultMessage = timeoutMessage;
                ResultColor = Avalonia.Media.Brushes.Red;
                CameraMessage = timeoutMessage;
                CameraMessageColor = Avalonia.Media.Brushes.Red;
            });

            return PresenceCheckResult.Rejected(timeoutMessage);
        }

        /// <summary>
        /// Загрузка YOLO модели.
        /// </summary>
        private void LoadYoloModel()
        {
            if (yoloEngine != null)
            {
                return;
            }

            var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "yolov8n.onnx");
            if (!File.Exists(modelPath))
            {
                Console.WriteLine($"Ошибка: Файл модели {modelPath} не найден.");
                return;
            }

            try
            {
                yoloEngine = new Yolo(new YoloOptions { OnnxModel = modelPath, ExecutionProvider = new CpuExecutionProvider() });
                Console.WriteLine("YOLO модель успешно загружена.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при загрузке YOLO модели: {ex.Message}");
            }
        }

        /// <summary>
        /// Остановка камеры.
        /// </summary>
        private void StopCamera()
        {
            if (capture != null)
            {
                capture.Dispose();
                capture = null;
                LogInfo("Camera", "Камера остановлена.");
            }

            linuxCameraAvailable = false;
        }

        private SKBitmap? ReadLinuxCameraFrame(int cameraRotation, out string? error)
        {
            error = null;

            if (!LinuxCameraFrameGrabber.TryGrabFrame(out var frame, out var devicePath, out error))
            {
                return null;
            }

            linuxCameraAvailable = true;
            LogInfo("Camera", $"Camera open success: {devicePath ?? "/dev/video0"} via Linux fallback path.");
            LogInfo("Camera", $"Linux raw frame received: {frame.Width}x{frame.Height}.");
            using (frame)
            {
                LogInfo("Camera", "Linux raw-frame UI path bypassed/removed.");
                return frame.Copy();
            }
        }

        private async Task<PresenceFrameProcessingResult> ProcessPresenceFrameAsync(
            SKBitmap sourceBitmap,
            int attempts,
            int processingErrors,
            int maxProcessingErrors,
            int cameraRotation = 0)
        {
            using var processedBitmap = cameraRotation == 180 ? ApplyCameraRotation(sourceBitmap, cameraRotation) : sourceBitmap.Copy();
            if (cameraRotation == 180)
            {
                LogInfo("Camera", "Linux rotation configured/applied: 180");
            }

            LogInfo("YOLO", $"YOLO inference started. Frame={processedBitmap.Width}x{processedBitmap.Height}, attempt={attempts}.");
            var results = yoloEngine!.RunObjectDetection(processedBitmap, confidence: 0.5, iou: 0.45);
            if (results is null)
            {
                processingErrors++;
                var resultsMessage = DescribeCameraProcessingNull("yolo results", results);
                LogError("Presence", resultsMessage);
                if (processingErrors >= maxProcessingErrors)
                {
                    return new PresenceFrameProcessingResult(PresenceCheckResult.TechnicalFailure(resultsMessage), processingErrors);
                }

                return new PresenceFrameProcessingResult(null, processingErrors);
            }

            processingErrors = 0;

            var personResults = results
                .Where(r => string.Equals(r.Label?.Name, "person", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int personCount = personResults.Count(r => r.Confidence > 0.5);
            LogInfo("YOLO", $"Person count={personCount}.");

            processedBitmap.Draw(personResults);
            LogInfo("Camera", $"Camera frame received. Attempt={attempts}, persons={personCount}.");

            if (OperatingSystem.IsLinux())
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SetCameraFrame(null);
                    CameraMessage = "Камера подключена, кадр получен, превью временно отключено на Linux.";
                    CameraMessageColor = Avalonia.Media.Brushes.DarkOrange;
                });
                LogWarning("Camera", "Linux preview path skipped intentionally.");
                LogWarning("Camera", "Linux preview disabled for stability.");
            }
            else
            {
            var avaloniaBitmap = TryConvertSkBitmapToAvaloniaBitmap(processedBitmap, out var previewError);
            if (avaloniaBitmap is null)
            {
                var bitmapMessage = string.IsNullOrWhiteSpace(previewError)
                    ? DescribeCameraProcessingNull("preview bitmap", avaloniaBitmap)
                    : $"Camera processing failed: preview bitmap step ({previewError}).";
                LogWarning("Presence", bitmapMessage);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CameraMessage = "Камера подключена, превью недоступно.";
                    CameraMessageColor = Avalonia.Media.Brushes.DarkOrange;
                });
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SetCameraFrame(avaloniaBitmap);
                    CameraMessage = string.Empty;
                    CameraMessageColor = Avalonia.Media.Brushes.Black;
                });
            }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (personCount == 1)
                {
                    ResultMessage = "Контроль присутствия: в кадре 1 человек — можно продолжать.";
                    ResultColor = Avalonia.Media.Brushes.Green;
                    CameraMessage = string.Empty;
                    CameraMessageColor = Avalonia.Media.Brushes.Black;
                }
                else if (personCount == 0)
                {
                    ResultMessage = "В кадре не зафиксирован сотрудник, измерение отменено";
                    ResultColor = Avalonia.Media.Brushes.Red;
                    CameraMessage = ResultMessage;
                    CameraMessageColor = Avalonia.Media.Brushes.Red;
                }
                else
                {
                    ResultMessage = "В кадре двое или более лиц, посторонним пожалуйста выйти из помещения";
                    ResultColor = Avalonia.Media.Brushes.Red;
                    CameraMessage = ResultMessage;
                    CameraMessageColor = Avalonia.Media.Brushes.Red;
                }
            });

            if (personCount == 1)
            {
                LogInfo("YOLO", "Single person confirmed.");
                LogInfo("Presence", "Presence verified by camera and YOLO.");
                return new PresenceFrameProcessingResult(PresenceCheckResult.Verified(), processingErrors);
            }

            return new PresenceFrameProcessingResult(null, processingErrors);
        }

        private async Task<PresenceFrameProcessingResult> ProcessLinuxPresenceFrameAsync(
            SKBitmap sourceBitmap,
            int attempts,
            int processingErrors,
            int maxProcessingErrors,
            int cameraRotation = 0)
        {
            using var processedBitmap = cameraRotation == 180 ? ApplyCameraRotation(sourceBitmap, cameraRotation) : sourceBitmap.Copy();
            if (cameraRotation == 180)
            {
                LogInfo("Camera", "Linux rotation forced: 180");
                LogInfo("Camera", $"Linux rotated bitmap created: {processedBitmap.Width}x{processedBitmap.Height}.");
            }

            var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "yolov8n.onnx");
            LogInfo("YOLO", $"Linux external inference started. Frame={processedBitmap.Width}x{processedBitmap.Height}, attempt={attempts}.");
            var inference = await Task.Run(() => LinuxExternalYoloRunner.Run(processedBitmap, modelPath));
            if (!inference.Success)
            {
                processingErrors++;
                LogWarning("YOLO", $"Linux external inference failed safely: {inference.Error}");
                if (processingErrors >= maxProcessingErrors)
                {
                    return new PresenceFrameProcessingResult(
                        PresenceCheckResult.TechnicalFailure($"Linux external AI inference failed safely: {inference.Error}"),
                        processingErrors);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CameraMessage = "Камера работает, AI detection временно недоступен.";
                    CameraMessageColor = Avalonia.Media.Brushes.DarkOrange;
                });
                return new PresenceFrameProcessingResult(null, processingErrors);
            }

            processingErrors = 0;
            LogInfo("YOLO", $"Linux external inference completed. Person count={inference.PersonCount}, max confidence={inference.MaxConfidence:0.00}.");
            DrawDetections(processedBitmap, inference.Detections);

            var avaloniaBitmap = TryConvertSkBitmapToAvaloniaBitmap(processedBitmap, out var previewError);
            if (avaloniaBitmap is null)
            {
                LogWarning("Camera", $"Linux preview update failed after AI detection: {previewError ?? "unknown error"}");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SetCameraFrame(null);
                    CameraMessage = "AI detection выполнен, но preview недоступен.";
                    CameraMessageColor = Avalonia.Media.Brushes.DarkOrange;
                });
            }
            else
            {
                var firstDetection = inference.Detections.FirstOrDefault();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SetCameraFrame(avaloniaBitmap);
                    CameraMessage = firstDetection is null
                        ? "AI detection выполнен: person not detected."
                        : $"AI detection: person count={inference.PersonCount}, confidence={firstDetection.Confidence:0.00}.";
                    CameraMessageColor = Avalonia.Media.Brushes.Green;
                });
                LogInfo("Camera", "Linux CameraFrame assigned from rotated bitmap.");
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (inference.PersonCount == 1)
                {
                    ResultMessage = "Контроль присутствия: в кадре 1 человек — можно продолжать.";
                    ResultColor = Avalonia.Media.Brushes.Green;
                    CameraMessage = string.Empty;
                    CameraMessageColor = Avalonia.Media.Brushes.Black;
                }
                else if (inference.PersonCount == 0)
                {
                    ResultMessage = "В кадре не зафиксирован сотрудник, измерение отменено";
                    ResultColor = Avalonia.Media.Brushes.Red;
                    CameraMessage = ResultMessage;
                    CameraMessageColor = Avalonia.Media.Brushes.Red;
                }
                else
                {
                    ResultMessage = "В кадре двое или более лиц, посторонним пожалуйста выйти из помещения";
                    ResultColor = Avalonia.Media.Brushes.Red;
                    CameraMessage = ResultMessage;
                    CameraMessageColor = Avalonia.Media.Brushes.Red;
                }
            });

            if (inference.PersonCount == 1)
            {
                LogInfo("YOLO", "Single person confirmed.");
                LogInfo("Presence", "Presence verified by camera and AI.");
                return new PresenceFrameProcessingResult(PresenceCheckResult.Verified(), processingErrors);
            }

            return new PresenceFrameProcessingResult(null, processingErrors);
        }

        private sealed record PresenceFrameProcessingResult(PresenceCheckResult? Result, int ProcessingErrors);

        private void DrawDetections(SKBitmap bitmap, IReadOnlyList<LinuxExternalYoloDetection> detections)
        {
            using var canvas = new SKCanvas(bitmap);
            using var boxPaint = new SKPaint
            {
                Color = SKColors.LimeGreen,
                IsStroke = true,
                StrokeWidth = 3,
                IsAntialias = true
            };
            using var textPaint = new SKPaint
            {
                Color = SKColors.LimeGreen,
                TextSize = 20,
                IsAntialias = true
            };

            foreach (var detection in detections)
            {
                if (detection.Width > 0 && detection.Height > 0)
                {
                    canvas.DrawRect(detection.X, detection.Y, detection.Width, detection.Height, boxPaint);
                }

                canvas.DrawText($"{detection.Label} {detection.Confidence:0.00}", detection.X, Math.Max(24, detection.Y - 6), textPaint);
            }
        }

        private static List<string> GetLinuxVideoDevices()
        {
            if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev"))
            {
                return new List<string>();
            }

            return Directory.GetFiles("/dev", "video*")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? GetPrimaryLinuxVideoDevice(IReadOnlyList<string> devices)
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            return devices.FirstOrDefault(path => string.Equals(path, "/dev/video0", StringComparison.Ordinal))
                ?? devices.FirstOrDefault();
        }

        private static bool IsCurrentUserInLinuxGroup(string groupName)
        {
            if (!OperatingSystem.IsLinux())
            {
                return true;
            }

            try
            {
                var userName = Environment.UserName;
                foreach (var line in File.ReadLines("/etc/group"))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    {
                        continue;
                    }

                    var parts = line.Split(':');
                    if (parts.Length < 4 || !string.Equals(parts[0], groupName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return parts[3]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains(userName, StringComparer.Ordinal);
                }
            }
            catch
            {
            }

            return false;
        }

        private static SKBitmap? TryConvertMatToSkBitmap(Mat frame)
            => TryConvertMatToSkBitmap(frame, out _);

        private static SKBitmap? TryConvertMatToSkBitmap(Mat frame, out string? error)
        {
            error = null;

            try
            {
                using var converted = new Mat();
                if (frame.NumberOfChannels == 4)
                {
                    frame.CopyTo(converted);
                }
                else
                {
                    var conversion = frame.NumberOfChannels switch
                    {
                        1 => ColorConversion.Gray2Bgra,
                        3 => ColorConversion.Bgr2Bgra,
                        _ => ColorConversion.Bgr2Bgra
                    };

                    CvInvoke.CvtColor(frame, converted, conversion);
                }

                var info = new SKImageInfo(converted.Width, converted.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                var bitmap = new SKBitmap(info);
                var byteCount = checked((int)(converted.Step * converted.Rows));
                var buffer = new byte[byteCount];
                Marshal.Copy(converted.DataPointer, buffer, 0, byteCount);
                Marshal.Copy(buffer, 0, bitmap.GetPixels(), byteCount);
                return bitmap;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().FullName}: {ex.Message}";
                return null;
            }
        }

        private static Avalonia.Media.Imaging.Bitmap? TryConvertSkBitmapToAvaloniaBitmap(SKBitmap? bitmap)
            => TryConvertSkBitmapToAvaloniaBitmap(bitmap, out _);

        private SKBitmap CreatePreviewBitmapForUi(SKBitmap sourceBitmap, int cameraRotation)
        {
            LogInfo("Camera", $"Linux rotation forced: {cameraRotation}");
            var previewBitmap = ApplyCameraRotation(sourceBitmap, cameraRotation);
            LogInfo("Camera", $"Linux rotated bitmap created: {previewBitmap.Width}x{previewBitmap.Height}.");
            return previewBitmap;
        }

        private void SetCameraFrame(Avalonia.Media.Imaging.Bitmap? nextFrame)
        {
            var previousFrame = CameraFrame;
            CameraFrame = nextFrame;

            if (!ReferenceEquals(previousFrame, nextFrame))
            {
                previousFrame?.Dispose();
            }
        }

        private static Avalonia.Media.Imaging.Bitmap? TryConvertSkBitmapToAvaloniaBitmap(SKBitmap? bitmap, out string? error)
        {
            error = null;

            if (bitmap is null || bitmap.IsEmpty)
            {
                error = "bitmap is null or empty";
                return null;
            }

            try
            {
                var writableBitmap = new WriteableBitmap(
                    new PixelSize(bitmap.Width, bitmap.Height),
                    new Avalonia.Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Unpremul);

                using var locked = writableBitmap.Lock();
                var rowBytes = bitmap.RowBytes;
                var height = bitmap.Height;
                var sourcePointer = bitmap.GetPixels();
                var rowBuffer = new byte[rowBytes];

                for (var row = 0; row < height; row++)
                {
                    var sourceRow = IntPtr.Add(sourcePointer, row * rowBytes);
                    var destinationRow = IntPtr.Add(locked.Address, row * locked.RowBytes);
                    Marshal.Copy(sourceRow, rowBuffer, 0, rowBytes);
                    Marshal.Copy(rowBuffer, 0, destinationRow, rowBytes);
                }

                return writableBitmap;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().FullName}: {ex.Message}";
                return null;
            }
        }

        private static SKBitmap ApplyCameraRotation(SKBitmap source, int cameraRotation)
        {
            if (cameraRotation != 180)
            {
                return source.Copy();
            }

            var rotated = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(rotated);
            canvas.Translate(source.Width, source.Height);
            canvas.RotateDegrees(180);
            canvas.DrawBitmap(source, 0, 0);
            canvas.Flush();
            return rotated;
        }

        private void ApplyEmployeeStatusVisuals(VitalMeasurement? measurement)
        {
            var alcoholStatus = EvaluateAlcoholStatus(measurement);
            var pressureStatus = EvaluatePressureStatus(measurement);
            var temperatureStatus = EvaluateTemperatureStatus(measurement);
            var verdictPresentation = BuildVerdictPresentation(measurement, alcoholStatus, pressureStatus, temperatureStatus);

            LogInfo("Assessment", $"Alcohol assessment source: {(measurement is null ? "missing" : measurement.AlcoholAssessmentSource)}");
            LogInfo("Assessment", $"Alcohol raw value resolved to: {(measurement is null ? "null" : measurement.AlcoholLevel.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))}");
            if (measurement is not null && !measurement.HasAlcoholValue)
            {
                LogInfo("Assessment", "Alcohol value missing -> using warning/unknown instead of red");
            }
            LogInfo("Assessment", $"Alcohol raw value used for assessment: {(measurement is null ? "null" : measurement.AlcoholLevel.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))}");
            LogInfo("Assessment", $"Alcohol status rule branch selected: {GetAlcoholRuleBranchName(measurement, alcoholStatus)}");
            LogInfo("Assessment", $"Pressure raw values used for assessment: SYS={(measurement is null ? "null" : measurement.BloodPressureSystolic.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))}, DAD={(measurement is null ? "null" : measurement.BloodPressureDiastolic.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))}");
            LogInfo("Assessment", $"Temperature raw value used for assessment: {(measurement is null ? "null" : measurement.Temperature.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))}");

            LogInfo("Assessment", $"Alcohol status evaluated: {alcoholStatus.ToString().ToLowerInvariant()}");
            LogInfo("Assessment", $"Pressure status evaluated: {pressureStatus.ToString().ToLowerInvariant()}");
            LogInfo("Assessment", $"Temperature status evaluated: {temperatureStatus.ToString().ToLowerInvariant()}");

            employeeOverallStatus = EvaluateOverallStatus(alcoholStatus, pressureStatus, temperatureStatus);
            LogInfo("Assessment", $"Overall verdict composed from: alcohol={alcoholStatus.ToString().ToLowerInvariant()}, pressure={pressureStatus.ToString().ToLowerInvariant()}, temperature={temperatureStatus.ToString().ToLowerInvariant()}");
            LogInfo("Assessment", $"Employee overall status: {employeeOverallStatus.ToString().ToLowerInvariant()}");

            VerdictColor = employeeOverallStatus switch
            {
                EmployeeOverallStatus.Healthy => Brushes.Green,
                EmployeeOverallStatus.Risk => Brushes.Red,
                EmployeeOverallStatus.Warning => Brushes.Goldenrod,
                _ => Brushes.Goldenrod
            };

            FinalVerdict = verdictPresentation.Status;
            VerdictReason = verdictPresentation.Reason;
            VerdictRecommendation = verdictPresentation.Recommendation;
            IsVerdictVisible = true;

            var borderColor = employeeOverallStatus switch
            {
                EmployeeOverallStatus.Healthy => "green",
                EmployeeOverallStatus.Risk => "red",
                EmployeeOverallStatus.Warning => "yellow",
                _ => "yellow"
            };
            LogInfo("Assessment", $"UI border color set to: {borderColor}");
        }

        private static VerdictPresentation BuildVerdictPresentation(
            VitalMeasurement? measurement,
            MeasurementStatus alcoholStatus,
            MeasurementStatus pressureStatus,
            MeasurementStatus temperatureStatus)
        {
            var riskReasons = new List<string>();
            var warningReasons = new List<string>();

            if (alcoholStatus == MeasurementStatus.Risk)
            {
                riskReasons.Add("Обнаружен алкоголь");
            }
            else if (measurement is not null && !measurement.HasAlcoholValue)
            {
                warningReasons.Add("Данные алкоголя не получены");
            }
            else if (alcoholStatus == MeasurementStatus.Warning)
            {
                warningReasons.Add("Пограничное значение алкоголя");
            }

            if (temperatureStatus == MeasurementStatus.Risk)
            {
                riskReasons.Add("Повышенная температура");
            }
            else if (temperatureStatus is MeasurementStatus.Warning or MeasurementStatus.Unknown)
            {
                warningReasons.Add("Данные получены не полностью");
            }

            if (measurement is not null
                && (measurement.BloodPressureSystolic == 255 || measurement.BloodPressureDiastolic == 255))
            {
                warningReasons.Add("Данные давления не получены");
            }
            else if (pressureStatus == MeasurementStatus.Risk)
            {
                riskReasons.Add(DescribePressureRiskReason(measurement));
            }
            else if (pressureStatus is MeasurementStatus.Warning or MeasurementStatus.Unknown or MeasurementStatus.Invalid)
            {
                warningReasons.Add("Данные давления не получены");
            }

            if (riskReasons.Count > 0)
            {
                return new VerdictPresentation(
                    "Требуется внимание",
                    JoinReasons(riskReasons),
                    "Обратитесь к медицинскому персоналу учреждения");
            }

            if (warningReasons.Count > 0)
            {
                var reason = JoinReasons(warningReasons);
                var recommendation = warningReasons.Contains("Пограничное значение алкоголя")
                    && warningReasons.Count == 1
                    ? "Требуется дополнительная проверка"
                    : "Повторите измерение";

                return new VerdictPresentation(
                    "Требуется повторное измерение",
                    reason,
                    recommendation);
            }

            return new VerdictPresentation(
                "Допуск разрешён",
                "Показатели в допустимом диапазоне",
                "Сотрудник может быть допущен к работе");
        }

        private static string DescribePressureRiskReason(VitalMeasurement? measurement)
        {
            if (measurement is null)
            {
                return "Давление вне нормы";
            }

            var systolicHigh = measurement.BloodPressureSystolic > 140;
            var diastolicHigh = measurement.BloodPressureDiastolic > 90;
            if (systolicHigh || diastolicHigh)
            {
                return "Повышенное давление";
            }

            var systolicLow = measurement.BloodPressureSystolic < 105;
            var diastolicLow = measurement.BloodPressureDiastolic < 50;
            if (systolicLow || diastolicLow)
            {
                return "Пониженное давление";
            }

            return "Давление вне нормы";
        }

        private static string JoinReasons(IReadOnlyList<string> reasons)
        {
            return string.Join(" и ", reasons.Distinct(StringComparer.Ordinal));
        }

        private static MeasurementStatus EvaluateAlcoholStatus(VitalMeasurement? measurement)
        {
            if (measurement is null)
            {
                return MeasurementStatus.Unknown;
            }

            if (!measurement.HasAlcoholValue)
            {
                return MeasurementStatus.Warning;
            }

            if (measurement.AlcoholLevel < 0 || measurement.AlcoholLevel > 4095)
            {
                return MeasurementStatus.Unknown;
            }

            if (measurement.AlcoholLevel > 3000)
            {
                return MeasurementStatus.Healthy;
            }

            if (measurement.AlcoholLevel < 1000)
            {
                return MeasurementStatus.Risk;
            }

            return MeasurementStatus.Warning;
        }

        private static MeasurementStatus EvaluatePressureStatus(VitalMeasurement? measurement)
        {
            if (measurement is null)
            {
                return MeasurementStatus.Unknown;
            }

            if (measurement.BloodPressureSystolic == 255 || measurement.BloodPressureDiastolic == 255)
            {
                return MeasurementStatus.Warning;
            }

            if (measurement.BloodPressureSystolic <= 0 || measurement.BloodPressureDiastolic <= 0)
            {
                return MeasurementStatus.Warning;
            }

            var isHealthy = measurement.BloodPressureSystolic >= 105
                && measurement.BloodPressureSystolic <= 140
                && measurement.BloodPressureDiastolic >= 50
                && measurement.BloodPressureDiastolic <= 90;

            return isHealthy ? MeasurementStatus.Healthy : MeasurementStatus.Risk;
        }

        private static MeasurementStatus EvaluateTemperatureStatus(VitalMeasurement? measurement)
        {
            if (measurement is null || measurement.Temperature <= 0)
            {
                return MeasurementStatus.Warning;
            }

            return measurement.Temperature > 35
                ? MeasurementStatus.Risk
                : MeasurementStatus.Healthy;
        }

        private static EmployeeOverallStatus EvaluateOverallStatus(
            MeasurementStatus alcoholStatus,
            MeasurementStatus pressureStatus,
            MeasurementStatus temperatureStatus)
        {
            var statuses = new[] { alcoholStatus, pressureStatus, temperatureStatus };
            if (statuses.Any(status => status == MeasurementStatus.Risk))
            {
                return EmployeeOverallStatus.Risk;
            }

            if (statuses.Any(status => status is MeasurementStatus.Warning or MeasurementStatus.Unknown or MeasurementStatus.Invalid))
            {
                return EmployeeOverallStatus.Warning;
            }

            if (statuses.All(status => status == MeasurementStatus.Healthy))
            {
                return EmployeeOverallStatus.Healthy;
            }

            return EmployeeOverallStatus.Warning;
        }

        private static string GetAlcoholRuleBranchName(VitalMeasurement? measurement, MeasurementStatus alcoholStatus)
        {
            if (measurement is null || !measurement.HasAlcoholValue)
            {
                return "warning-no-data";
            }

            return alcoholStatus switch
            {
                MeasurementStatus.Healthy => "green",
                MeasurementStatus.Risk => "red",
                MeasurementStatus.Warning => "yellow",
                _ => "unknown"
            };
        }

        private enum MeasurementStatus
        {
            Unknown,
            Healthy,
            Risk,
            Warning,
            Invalid
        }

        private enum EmployeeOverallStatus
        {
            Unknown,
            Healthy,
            Risk,
            Warning
        }

        private sealed record VerdictPresentation(string Status, string Reason, string Recommendation);

        private DeviceRuntimeConfig LoadDeviceRuntimeConfig()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DeviceConfigFileName);
            if (!File.Exists(configPath))
            {
                return new DeviceRuntimeConfig();
            }

            try
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<DeviceRuntimeConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new DeviceRuntimeConfig();
            }
            catch (Exception ex)
            {
                LogWarning("Config", $"Failed to read {DeviceConfigFileName}: {ex.Message}");
                return new DeviceRuntimeConfig();
            }
        }

        private void SaveDeviceRuntimeConfig(Action<DeviceRuntimeConfig> update)
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DeviceConfigFileName);
            var currentConfig = LoadDeviceRuntimeConfig();
            update(currentConfig);

            try
            {
                var json = JsonSerializer.Serialize(currentConfig, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                LogWarning("Config", $"Failed to save {DeviceConfigFileName}: {ex.Message}");
            }
        }

        private static bool TryBuildSyncEndpointUrl(string? serverIp, out string endpointUrl, out string validationError)
        {
            endpointUrl = string.Empty;
            validationError = string.Empty;

            if (string.IsNullOrWhiteSpace(serverIp))
            {
                validationError = "не задан IP сервера сайта";
                return false;
            }

            if (!IPAddress.TryParse(serverIp.Trim(), out var ipAddress))
            {
                validationError = $"некорректный IP '{serverIp}'";
                return false;
            }

            endpointUrl = $"http://{ipAddress}:3001/api/measurements";
            return true;
        }

        private sealed class DeviceRuntimeConfig
        {
            public int CameraRotation { get; set; }
            public bool LinuxNoCameraMode { get; set; }
            public string? ServerIp { get; set; }

            [JsonExtensionData]
            public Dictionary<string, JsonElement>? AdditionalData { get; set; }
        }

        
         /// <summary>
        /// Симуляция сбора данных.
        /// </summary>
        private async Task<VitalMeasurement> SimulateDataCollection()
        {
            await Task.Delay(5000);

            var random = new Random();
            return new VitalMeasurement
            {
                EmployeeId = currentEmployeeId,
                Timestamp = DateTime.Now,
                HeartRate = random.Next(40, 140),
                Saturation = random.Next(80, 100),
                EcgData = "simulated_ecg",
                ActivityLevel = random.Next(1000, 3000),
                BloodPressureSystolic = random.Next(80, 160),
                BloodPressureDiastolic = random.Next(50, 100),
                Temperature = random.NextDouble() * (38.5 - 35) + 35,
                Glucose = random.NextDouble() * (8 - 3) + 3,
                Cholesterol = random.NextDouble() * (7 - 3) + 3,
                AlcoholLevel = random.NextDouble() * 1.5,
                Diagnosis = "Симулированные данные"
            };
        }

        /// <summary>
        /// Расчёт допуска по порогам.
        /// </summary>
        private (string verdict, Avalonia.Media.IBrush color, List<HealthTile> tiles) CalculateDetailedDiagnosis(VitalMeasurement m)
        {
            var alcoholStatus = EvaluateAlcoholStatus(m);
            var pressureStatus = EvaluatePressureStatus(m);
            var temperatureStatus = EvaluateTemperatureStatus(m);
            var overallStatus = EvaluateOverallStatus(alcoholStatus, pressureStatus, temperatureStatus);

            var tiles = new List<HealthTile>
            {
                BuildAssessmentTile("Алкоголь", m.HasAlcoholValue ? m.AlcoholLevel.ToString("F0") : "нет данных", alcoholStatus),
                BuildAssessmentTile("Температура", m.Temperature.ToString("F2"), temperatureStatus),
                BuildAssessmentTile("Давление", $"{m.BloodPressureSystolic:F0}/{m.BloodPressureDiastolic:F0}", pressureStatus)
            };

            return overallStatus switch
            {
                EmployeeOverallStatus.Healthy => ("Зелёный", Brushes.Green, tiles),
                EmployeeOverallStatus.Risk => ("Красный", Brushes.Red, tiles),
                _ => ("Жёлтый", Brushes.Goldenrod, tiles)
            };
        }

        private static HealthTile BuildAssessmentTile(string title, string value, MeasurementStatus status)
        {
            return status switch
            {
                MeasurementStatus.Healthy => new HealthTile(title, value, "Норма", "Показатель в пределах нормы.", Brushes.Green),
                MeasurementStatus.Risk => new HealthTile(title, value, "Риск", "Показатель требует внимания.", Brushes.Red),
                _ => new HealthTile(title, value, "Предупреждение", "Данных недостаточно или показатель пограничный.", Brushes.Goldenrod)
            };
        }



        [RelayCommand]
        private void FinishAndExit()
        {
            FinishSession();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }
}

