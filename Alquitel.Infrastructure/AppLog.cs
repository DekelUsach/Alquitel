using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Alquitel.Infrastructure
{
    /// <summary>
    /// Static logging facade. Initialized once at app startup via <see cref="Initialize"/>.
    /// Writes rolling daily files to <c>%LocalAppData%\Alquitel\logs\app-YYYYMMDD.log</c>.
    /// </summary>
    public static class AppLog
    {
        private static ILogger _logger = Logger.None;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            try
            {
                var logFile = Path.Combine(AppPaths.LogsFolder, "app-.log");
                _logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.File(logFile,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Debug()
                    .CreateLogger();
                _initialized = true;
                _logger.Information("AppLog initialized — root: {Root}", AppPaths.AppDataRoot);
            }
            catch
            {
                _logger = Logger.None;
            }
        }

        public static void Information(string template, params object?[] args) => _logger.Information(template, args);
        public static void Warning(string template, params object?[] args) => _logger.Warning(template, args);
        public static void Warning(Exception ex, string template, params object?[] args) => _logger.Warning(ex, template, args);
        public static void Error(string template, params object?[] args) => _logger.Error(template, args);
        public static void Error(Exception ex, string template, params object?[] args) => _logger.Error(ex, template, args);
        public static void Fatal(Exception ex, string template, params object?[] args) => _logger.Fatal(ex, template, args);

        public static void Shutdown()
        {
            if (_logger is Logger l) l.Dispose();
        }
    }
}
