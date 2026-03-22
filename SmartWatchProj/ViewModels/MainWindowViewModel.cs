using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SkiaSharp;
using SmartWatchProj.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using YoloDotNet;
using YoloDotNet.Core;
using YoloDotNet.Extensions;
using YoloDotNet.Models;
using System.Text.Json;
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
        [ObservableProperty] private Avalonia.Media.IBrush verdictColor = Avalonia.Media.Brushes.Gray;
        // [ObservableProperty] private string humanRecommendation = ""; // Убрали из UI, оставили для PDF
        [ObservableProperty] private bool showDetailedResult = false;
        [ObservableProperty] private string newEmployeeName;
        [ObservableProperty] private string newCardId;

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

        [ObservableProperty] private string serverUrl = "http://192.168.1.62:3001/api/measurements"; // IP сайта + порт + endpoint
        [ObservableProperty] private string syncMessage = ""; // Статус для UI

        [ObservableProperty] private string adminPassword = "";  // Пароль админа
        [ObservableProperty] private bool isAdminAuthenticated = false;  // Флаг аутентификации

        [ObservableProperty] private bool isOverlayVisible = false;  // Видимость оверлея
        [ObservableProperty] private bool isCaptchaVisible = false;  // Видимость CAPTCHA (true сначала)
        [ObservableProperty] private string captchaPrompt = "";  // Текст "Проверка: Нажмите на все цифры X"
        [ObservableProperty] private string currentInstruction = "";  // Текст текущей инструкции
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

        private const string EmployeesJsonPath = "employees.json";
        private const string MeasurementsJsonPath = "measurements.json";
        private int currentEmployeeId;
        private VideoCapture? capture;
        private Yolo? yoloEngine;
        private PdfDocument sessionReport = new PdfDocument();
        private List<VitalMeasurement> sessionMeasurements = new List<VitalMeasurement>();


        public MainWindowViewModel()
        {
            try
            {
                LoadEmployeesFromJson();
                LoadMeasurementsFromJson();
                InitializeCapture();
                LoadYoloModel();
                Console.WriteLine($"Инициализация успешна. Время: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
                for (int i = 0; i < 9; i++)
                {
                    captchaLefts.Add(0);
                    captchaTops.Add(0);
                }
                for (int i = 0; i < 9; i++)
                {
                    captchaBackgrounds.Add(Avalonia.Media.Brushes.White);  // Белый фон по умолчанию
                }
                CaptchaItems = new ObservableCollection<int>(Enumerable.Repeat(0, 9));  // Инициализация с 9 плейсхолдерами
                isCaptchaSelected = new ObservableCollection<bool>(Enumerable.Repeat(false, 9));
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

        // Команда для кнопки Старт
        [RelayCommand]
        private async Task Start()
        {
            try
            {
                ClearTopInfo();//
                IsCollectingData = true;
                ResultMessage = "Проверка пользователя...";
                ResultColor = Avalonia.Media.Brushes.Black;
                await CheckEmployeeByCardId();
                if (!IsEmployeeFound)
                {
                    ShowTopInfo("Сотрудник не найден", Brushes.Red);
                    IsCollectingData = false;
                    return;
                }
                bool verified = await VerifyUserWithCamera();
                if (!verified)
                {
                    CameraMessage = "Проверка не пройдена. Повторите.";
                    CameraMessageColor = Avalonia.Media.Brushes.Red;
                    return;
                }
                CameraMessage = "Пользователь верифицирован.";
                CameraMessageColor = Avalonia.Media.Brushes.Green;

                // Показываем оверлей и CAPTCHA
                IsOverlayVisible = true;
                IsCaptchaVisible = true;
                GenerateCaptcha();

                // Ожидаем завершения измерений
                _measurementCompletion = new TaskCompletionSource<VitalMeasurement>();
                var measurement = await _measurementCompletion.Task;

                // Анализ и остальное
                var (verdict, verdictColor, healthTiles) = CalculateDetailedDiagnosis(measurement);
                DetailedHealthTiles = new ObservableCollection<HealthTile>(healthTiles);
                VerdictColor = verdictColor;
                UpdateTilesWithStatuses(healthTiles, verdict);
                string humanMessage = GenerateHumanRecommendation(measurement, healthTiles);
                measurement.Diagnosis = humanMessage;
                measurement.Recommendation = verdict;
                HumanRecommendation = humanMessage;
                ShowDetailedResult = true;
                measurements.Add(measurement);
                sessionMeasurements.Add(measurement);
                SaveMeasurementsToJson();
                UpdateLastData();
                ResultMessage = $"Результат: {verdict}";
                ResultColor = verdictColor;
            }
            catch (Exception ex)
            {
                ResultMessage = $"Ошибка: {ex.Message}";
                ResultColor = Avalonia.Media.Brushes.Red;
                Console.WriteLine($"Исключение в Start(): {ex}");
            }
            finally
            {
                IsCollectingData = false;
                StopCamera();
                IsOverlayVisible = false;
            }
        }

        private void GenerateCaptcha()
        {
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

                // Запускаем процесс измерений
                _ = StartMeasurementProcessAsync();  // Async, результат через TCS
            }
            else
            {
                CaptchaMessage = "Ошибка, попробуйте снова";
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
            string[] instructions = new string[]
            {
        "Подключение часов",
        "Прислонитесь к измерителю температуры",
        "Дуньте в алкотестер",
        "Поместите манжету на руку",
        "Поместите палец в глюкометр"
            };

            int totalSteps = instructions.Length;
            for (int i = 0; i < totalSteps; i++)
            {
                CurrentInstruction = instructions[i];
                ProgressValue = (i / (double)totalSteps) * 100;
                await Task.Delay(5000);  // Симуляция
            }

            ProgressValue = 100;
            await Task.Delay(1000);

            // Симуляция measurement
            var random = new Random();
            var measurement = new VitalMeasurement
            {
                EmployeeId = currentEmployeeId,
                Timestamp = DateTime.Now,
                HeartRate = random.Next(50, 150),
                Saturation = random.Next(85, 100),
                EcgData = "simulated_ecg",
                ActivityLevel = random.Next(1000, 3000),
                BloodPressureSystolic = random.Next(90, 160),
                BloodPressureDiastolic = random.Next(60, 100),
                Temperature = 35 + random.NextDouble() * 3,
                Glucose = 4 + random.NextDouble() * 3,
                Cholesterol = 4 + random.NextDouble() * 3,
                AlcoholLevel = random.NextDouble() * 1.5
            };

            _measurementCompletion?.SetResult(measurement);
        }


        [RelayCommand]
        private async Task SyncData()
        {
            try
            {
                string serverUrl = "http://192.168.1.62:3001/api/measurements";  // URL сервера

                // Сериализуем текущие measurements из коллекции (не из файла, чтобы актуально)
                string jsonData = JsonSerializer.Serialize(Measurements.ToList(), new JsonSerializerOptions { WriteIndented = true });

                using var client = new HttpClient();
                var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(serverUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    ResultMessage = "Данные синхронизированы успешно!";
                    ResultColor = Brushes.Green;
                }
                else
                {
                    string error = await response.Content.ReadAsStringAsync();
                    ResultMessage = $"Ошибка синхронизации: {response.StatusCode} - {error}";
                    ResultColor = Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                ResultMessage = $"Исключение при синхронизации: {ex.Message}";
                ResultColor = Brushes.Red;
            }
        }





        [RelayCommand]
        private void AddEmployee()  
        {
            if (string.IsNullOrWhiteSpace(NewEmployeeName) || string.IsNullOrWhiteSpace(NewCardId))
            {
                ShowTopInfo("Введите ФИО и CardId!", Brushes.Red);
                return;
            }

            if (Employees.Any(e => e.CardId == NewCardId))
            {
                ShowTopInfo("CardId уже существует!", Brushes.Red);
                return;
            }

            // Опционально: проверка на дубликат ФИО
            if (Employees.Any(e => e.Name == NewEmployeeName))
            {
                ShowTopInfo("Сотрудник с таким ФИО уже есть!", Brushes.Red);
                return;
            }

            int newId = Employees.Any() ? Employees.Max(e => e.Id) + 1 : 1;  // Авто-ID
            var newEmployee = new Employee { Id = newId, Name = NewEmployeeName, CardId = NewCardId, FaceData = null };
            Employees.Add(newEmployee);
            SaveEmployeesToJson();
            ShowTopInfo("Сотрудник добавлен", Brushes.Green);

            // Очистить поля после добавления
            NewEmployeeName = string.Empty;
            NewCardId = string.Empty;
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
                Console.WriteLine($"Найден сотрудник ID={employee.Id}");
            }
            else
            {
                IsEmployeeFound = false;
                currentEmployeeId = 0;
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
                Console.WriteLine($"Saved {allMeasurements.Count} measurements to {jsonPath}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving measurements: {ex.Message}");
            }
        }

        private void LoadMeasurementsFromJson()
        {
            try
            {
                var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MeasurementsJsonPath);
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
                        Console.WriteLine($"Loaded {Measurements.Count} measurements (all employees).");
                    }
                    else
                    {
                        Console.WriteLine("No measurements loaded from JSON.");
                    }
                }
                else
                {
                    Console.WriteLine($"Measurements JSON file not found at {jsonPath}.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading measurements: {ex.Message}");
            }
        }

        private void SaveEmployeesToJson()
        {
            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, EmployeesJsonPath);
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(employees.ToList(), options);
                File.WriteAllText(fullPath, json);
                Console.WriteLine($"Сотрудники сохранены в {EmployeesJsonPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка записи JSON сотрудников: {ex.Message}");
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
            CameraFrame = null;
            InstructionMessage = "Готово. Подходите следующий сотрудник.";
            ResultMessage = string.Empty;
            ResultColor = Avalonia.Media.Brushes.Black;
            CameraMessage = string.Empty;
            CameraMessageColor = Avalonia.Media.Brushes.Black;
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
            capture = new VideoCapture(0);  // Камера 0, измените индекс если нужно
            if (!capture.IsOpened)
            {
                Console.WriteLine("Ошибка инициализации камеры.");
                capture = null;
            }
            else
            {
                Console.WriteLine("Камера инициализирована.");
            }
        }

        /// <summary>
        /// Загрузка YOLO модели.
        /// </summary>
        private void LoadYoloModel()
        {
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
        /// Проверка пользователя через камеру с YOLO (детекция людей).
        /// </summary>
        private async Task<bool> VerifyUserWithCamera()
        {
            // Переинициализация камеры перед использованием
            InitializeCapture();

            if (capture == null || !capture.IsOpened)
            {
                Console.WriteLine("Камера не инициализирована.");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ResultMessage = "Камера не инициализирована.";
                    ResultColor = Avalonia.Media.Brushes.Red;
                });
                return false;
            }

            if (yoloEngine == null)
            {
                Console.WriteLine("YOLO модель не загружена. Проверка невозможна.");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ResultMessage = "YOLO модель не загружена. Проверка невозможна.";
                    ResultColor = Avalonia.Media.Brushes.Red;
                });
                return false;
            }

            // Таймаут 30 секунд
            var start = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(30);

            int attempts = 0;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResultMessage = "Контроль присутствия: убедитесь, что в кадре 1 человек.";
                ResultColor = Avalonia.Media.Brushes.Black;
            });

            while (DateTime.UtcNow - start < timeout)
            {
                try
                {
                    using Mat frame = new Mat();
                    if (!capture.Read(frame) || frame.IsEmpty)
                    {
                        Console.WriteLine("Пустой кадр, пропуск...");
                        attempts++;
                        await Task.Delay(100);
                        continue;
                    }

                    // Emgu.CV -> JPEG bytes (как у тебя: через VectorOfByte)
                    using VectorOfByte vec = new VectorOfByte();
                    CvInvoke.Imencode(".jpg", frame, vec);
                    byte[] jpegData = vec.ToArray();

                    using SKBitmap skBitmap = SKBitmap.Decode(jpegData);

                    // YOLO детекция
                    var results = yoloEngine.RunObjectDetection(skBitmap, confidence: 0.5, iou: 0.45);

                    // Фильтр только person
                    var personResults = results.Where(r => r.Label.Name == "person").ToList();
                    int personCount = personResults.Count(r => r.Confidence > 0.5);

                    // Рисуем bounding boxes только вокруг person
                    skBitmap.Draw(personResults);

                    Console.WriteLine($"Attempt {attempts}: Persons detected: {personCount}");

                    // Кодируем модифицированный skBitmap обратно в jpeg для UI
                    using (MemoryStream modifiedMs = new MemoryStream())
                    {
                        skBitmap.Encode(modifiedMs, SKEncodedImageFormat.Jpeg, 100);
                        modifiedMs.Seek(0, SeekOrigin.Begin);

                        var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(modifiedMs);

                        // Обновляем превью + пишем сообщение в панель информации (ResultMessage)
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            CameraFrame = avaloniaBitmap;

                            if (personCount == 1)
                            {
                                ResultMessage = "Контроль присутствия: в кадре 1 человек — можно продолжать.";
                                ResultColor = Avalonia.Media.Brushes.Green;
                            }
                            else if (personCount == 0)
                            {
                                ResultMessage = "Контроль присутствия: человек не обнаружен. Подойдите ближе и смотрите в камеру.";
                                ResultColor = Avalonia.Media.Brushes.Orange;
                            }
                            else
                            {
                                ResultMessage = "Два или более человек в кадре. Посторонние выйдите из кадра.";
                                ResultColor = Avalonia.Media.Brushes.Red;
                            }
                        });
                    }

                    if (personCount == 1)
                        return true;

                    attempts++;
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка в VerifyUserWithCamera: {ex.Message}. Stack: {ex.StackTrace}");

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ResultMessage = "Ошибка обработки камеры. Повтор попытки…";
                        ResultColor = Avalonia.Media.Brushes.Orange;
                    });

                    await Task.Delay(100);
                }
            }

            // Таймаут
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResultMessage = "Проверка завершена: за 30 секунд не удалось получить кадр с 1 человеком.";
                ResultColor = Avalonia.Media.Brushes.Red;
            });

            return false;
        }



        /// <summary>
        /// Конвертер Mat в Avalonia Bitmap (с использованием временного файла).
        /// </summary>
        private Avalonia.Media.Imaging.Bitmap ToAvaloniaBitmap(Mat mat)
        {
            var tempPath = Path.GetTempFileName() + ".png"; // Временный файл
            try
            {
                CvInvoke.Imwrite(tempPath, mat); // Сохраняем Mat как PNG

                using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                return new Avalonia.Media.Imaging.Bitmap(fs);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath); // Удаляем временный файл
                }
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
                Console.WriteLine("Камера остановлена.");
            }
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
            var tiles = new List<HealthTile>();
            var random = new Random();  // Для вариативности текстов

            // Шаблоны советов для каждого уровня (массивы для случайного выбора)
            string[] lowAdvice = { "Обратитесь к врачу для проверки.", "Рекомендуем отдохнуть и проконсультироваться." };
            string[] warningAdvice = { "Мониторьте состояние, избегайте нагрузок.", "Возможно, нужна корректировка образа жизни." };
            string[] normalAdvice = { "Всё в порядке, продолжайте в том же духе.", "Нормальные показатели, поддерживайте баланс." };
            string[] highAdvice = { "Срочно к специалисту!", "Избегайте физических нагрузок до обследования." };

            // ЧСС
            string hrAdvice = "";
            if (m.HeartRate < 50) { tiles.Add(new HealthTile("ЧСС", $"{m.HeartRate:F0}", "Очень низкая", highAdvice[random.Next(highAdvice.Length)], Avalonia.Media.Brushes.Red)); }
            else if (m.HeartRate < 60) { tiles.Add(new HealthTile("ЧСС", $"{m.HeartRate:F0}", "Низкая", warningAdvice[random.Next(warningAdvice.Length)], Avalonia.Media.Brushes.Orange)); }
            else if (m.HeartRate <= 100) { tiles.Add(new HealthTile("ЧСС", $"{m.HeartRate:F0}", "Норма", normalAdvice[random.Next(normalAdvice.Length)], Avalonia.Media.Brushes.Green)); }
            else if (m.HeartRate <= 120) { tiles.Add(new HealthTile("ЧСС", $"{m.HeartRate:F0}", "Повышенная", warningAdvice[random.Next(warningAdvice.Length)], Avalonia.Media.Brushes.Orange)); }
            else { tiles.Add(new HealthTile("ЧСС", $"{m.HeartRate:F0}", "Тахикардия", highAdvice[random.Next(highAdvice.Length)], Avalonia.Media.Brushes.Red)); }

            // Сатурация (аналогично, добавьте шаблоны)
            if (m.Saturation >= 98) tiles.Add(new HealthTile("SpO₂", $"{m.Saturation}%", "Отлично", normalAdvice[random.Next(normalAdvice.Length)], Avalonia.Media.Brushes.Green));
            else if (m.Saturation >= 95) tiles.Add(new HealthTile("SpO₂", $"{m.Saturation}%", "Норма", normalAdvice[random.Next(normalAdvice.Length)], Avalonia.Media.Brushes.Green));
            else if (m.Saturation >= 92) tiles.Add(new HealthTile("SpO₂", $"{m.Saturation}%", "Снижена", warningAdvice[random.Next(warningAdvice.Length)], Avalonia.Media.Brushes.Orange));
            else tiles.Add(new HealthTile("SpO₂", $"{m.Saturation}%", "Гипоксия", highAdvice[random.Next(highAdvice.Length)], Avalonia.Media.Brushes.Red));

            // Давление (добавьте специфические советы)
            string bp = $"{m.BloodPressureSystolic:F0}/{m.BloodPressureDiastolic:F0}";
            string[] bpHighAdvice = { "Контролируйте давление, избегайте соли и стресса.", "Рекомендуем измерять ежедневно." };
            if (m.BloodPressureSystolic < 90 || m.BloodPressureDiastolic < 60)
                tiles.Add(new HealthTile("АД", bp, "Гипотония", lowAdvice[random.Next(lowAdvice.Length)], Avalonia.Media.Brushes.Orange));
            else if (m.BloodPressureSystolic <= 129 && m.BloodPressureDiastolic <= 84)
                tiles.Add(new HealthTile("АД", bp, "Идеальное", normalAdvice[random.Next(normalAdvice.Length)], Avalonia.Media.Brushes.Green));
            else if (m.BloodPressureSystolic <= 139 || m.BloodPressureDiastolic <= 89)
                tiles.Add(new HealthTile("АД", bp, "Норма", normalAdvice[random.Next(normalAdvice.Length)], Avalonia.Media.Brushes.Green));
            else if (m.BloodPressureSystolic <= 159 || m.BloodPressureDiastolic <= 99)
                tiles.Add(new HealthTile("АД", bp, "Гипертония I", warningAdvice[random.Next(warningAdvice.Length)], Avalonia.Media.Brushes.Orange));
            else
                tiles.Add(new HealthTile("АД", bp, "Гипертония II+", bpHighAdvice[random.Next(bpHighAdvice.Length)], Avalonia.Media.Brushes.Red));

            // Температура, Глюкоза, Холестерин, Алкоголь, Активность — аналогично добавьте шаблоны с random.Next()
            // ... (скопируйте структуру из вашего кода, добавив Advice как выше)

            // Общий вердикт (без изменений)
            bool hasCritical = tiles.Any(t => t.Color == Avalonia.Media.Brushes.Red || t.Color == Avalonia.Media.Brushes.DarkRed);
            bool hasWarning = tiles.Any(t => t.Color == Avalonia.Media.Brushes.Orange || t.Color == Avalonia.Media.Brushes.Yellow);

            string verdict = hasCritical ? "Риск" :
                             hasWarning ? "Внимание" :
                                           "Норма";

            var verdictColor = hasCritical ? Avalonia.Media.Brushes.Red :
                               hasWarning ? Avalonia.Media.Brushes.Orange :
                                             Avalonia.Media.Brushes.Green;

            return (verdict, verdictColor, tiles);
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
