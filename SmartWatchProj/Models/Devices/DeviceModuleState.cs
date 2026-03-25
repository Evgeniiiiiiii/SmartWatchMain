using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartWatchProj.Models.Devices
{
    public partial class DeviceModuleState : ObservableObject
    {
        [property: JsonPropertyName("sortOrder")]
        [ObservableProperty]
        private int sortOrder;

        [property: JsonPropertyName("deviceId")]
        [ObservableProperty]
        private string deviceId = string.Empty;

        [property: JsonPropertyName("displayName")]
        [ObservableProperty]
        private string displayName = string.Empty;

        [property: JsonPropertyName("moduleType")]
        [ObservableProperty]
        private DeviceModuleType moduleType;

        [property: JsonPropertyName("connectionType")]
        [ObservableProperty]
        private DeviceConnectionType connectionType = DeviceConnectionType.Serial;

        [property: JsonPropertyName("portMode")]
        [ObservableProperty]
        private DevicePortMode portMode = DevicePortMode.Auto;

        [property: JsonPropertyName("portName")]
        [ObservableProperty]
        private string portName = string.Empty;

        [property: JsonPropertyName("baudRate")]
        [ObservableProperty]
        private int baudRate = 115200;

        [property: JsonPropertyName("timeoutMs")]
        [ObservableProperty]
        private int timeoutMs = 10000;

        [property: JsonPropertyName("enabled")]
        [ObservableProperty]
        private bool isEnabled = true;

        [JsonIgnore]
        [ObservableProperty]
        private DeviceStatus status = DeviceStatus.NotConfigured;

        [JsonIgnore]
        [ObservableProperty]
        private string statusMessage = "Не проверено";

        [JsonIgnore]
        [ObservableProperty]
        private string resolvedPortName = string.Empty;

        [JsonIgnore]
        [ObservableProperty]
        private string lastError = string.Empty;

        [JsonIgnore]
        [ObservableProperty]
        private string lastResponsePreview = string.Empty;

        [JsonIgnore]
        [ObservableProperty]
        private DateTimeOffset? lastCheckedAt;

        [JsonIgnore]
        public IReadOnlyList<DevicePortMode> PortModes { get; } = Enum.GetValues<DevicePortMode>();

        [JsonIgnore]
        public bool CanEditPortName => PortMode == DevicePortMode.Manual;

        [JsonIgnore]
        public string StatusText => Status switch
        {
            DeviceStatus.NotConfigured => "Не настроено",
            DeviceStatus.Detecting => "Поиск",
            DeviceStatus.Connected => "Подключено",
            DeviceStatus.NotFound => "Не найдено",
            DeviceStatus.Error => "Ошибка",
            DeviceStatus.Ready => "Готово",
            _ => "Неизвестно"
        };

        [JsonIgnore]
        public IBrush StatusBrush => Status switch
        {
            DeviceStatus.Ready => Brushes.Green,
            DeviceStatus.Connected => Brushes.SteelBlue,
            DeviceStatus.Detecting => Brushes.Goldenrod,
            DeviceStatus.NotFound => Brushes.Orange,
            DeviceStatus.Error => Brushes.Red,
            _ => Brushes.Gray
        };

        [JsonIgnore]
        public string StatusDetails
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LastError))
                {
                    return LastError;
                }

                if (!string.IsNullOrWhiteSpace(ResolvedPortName))
                {
                    return $"Порт: {ResolvedPortName} | {BaudRate} бод";
                }

                return PortMode == DevicePortMode.Auto
                    ? "Автоматический поиск COM-порта"
                    : $"Ручной порт: {(string.IsNullOrWhiteSpace(PortName) ? "не указан" : PortName)}";
            }
        }

        partial void OnStatusChanged(DeviceStatus value)
        {
            NotifyRuntimePropertiesChanged();
        }

        partial void OnStatusMessageChanged(string value)
        {
            OnPropertyChanged(nameof(StatusDetails));
        }

        partial void OnResolvedPortNameChanged(string value)
        {
            OnPropertyChanged(nameof(StatusDetails));
        }

        partial void OnLastErrorChanged(string value)
        {
            OnPropertyChanged(nameof(StatusDetails));
        }

        partial void OnPortModeChanged(DevicePortMode value)
        {
            OnPropertyChanged(nameof(CanEditPortName));
            OnPropertyChanged(nameof(StatusDetails));
        }

        partial void OnPortNameChanged(string value)
        {
            OnPropertyChanged(nameof(StatusDetails));
        }

        partial void OnBaudRateChanged(int value)
        {
            OnPropertyChanged(nameof(StatusDetails));
        }

        partial void OnIsEnabledChanged(bool value)
        {
            if (!value)
            {
                Status = DeviceStatus.NotConfigured;
                StatusMessage = "Отключено в настройках";
            }

            NotifyRuntimePropertiesChanged();
        }

        private void NotifyRuntimePropertiesChanged()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusDetails));
        }
    }
}
