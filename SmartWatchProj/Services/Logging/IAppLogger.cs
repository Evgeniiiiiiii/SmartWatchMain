namespace SmartWatchProj.Services.Logging
{
    public interface IAppLogger
    {
        string LogFilePath { get; }

        void Info(string message);
        void Warning(string message);
        void Error(string message, System.Exception? exception = null);
    }
}
