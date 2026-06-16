using System.Text;
using Microsoft.Extensions.Logging;

namespace K4GOTV;

public sealed partial class Plugin
{
	private readonly object _logLock = new();
	private StreamWriter? _logWriter;
	private string? _logFilePath;

	private string LogDirectoryPath =>
		Path.Combine(Core.PluginDataDirectory, Config.CurrentValue.General.LogDirectory);

	private void InitializeLogging()
	{
		if (!Config.CurrentValue.General.EnableFileLogging)
		{
			LogToConsole(LogLevel.Information, "Logging", "File logging disabled by config");
			return;
		}

		try
		{
			Directory.CreateDirectory(LogDirectoryPath);

			var logFileName = Config.CurrentValue.General.LogFileName;
			_logFilePath = Path.Combine(LogDirectoryPath, logFileName);
			_logWriter = new StreamWriter(
				new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
				Encoding.UTF8)
			{
				AutoFlush = true
			};

			WriteLog(LogLevel.Information, "Logging", "File logger initialized", ("logPath", _logFilePath));
		}
		catch (Exception ex)
		{
			LogToConsole(LogLevel.Error, "Logging", $"Failed to initialize file logger | error={ex.Message}");
		}
	}

	private void ShutdownLogging()
	{
		lock (_logLock)
		{
			try
			{
				_logWriter?.Flush();
				_logWriter?.Dispose();
			}
			catch
			{
				// ignored
			}
			finally
			{
				_logWriter = null;
			}
		}
	}

	private void LogConfigSnapshot(string reason)
	{
		var config = Config.CurrentValue;

		WriteLog(LogLevel.Information, "Config", $"Configuration snapshot ({reason})",
			("databaseConfigured", !string.IsNullOrEmpty(config.DatabaseConnection)),
			("general.minimumDemoDuration", config.General.MinimumDemoDuration),
			("general.deleteDemoAfterUpload", config.General.DeleteDemoAfterUpload),
			("general.deleteZippedDemoAfterUpload", config.General.DeleteZippedDemoAfterUpload),
			("general.deleteEveryDemoFromServerAfterServerStart", config.General.DeleteEveryDemoFromServerAfterServerStart),
			("general.logUploads", config.General.LogUploads),
			("general.logDeletions", config.General.LogDeletions),
			("general.regularFileNamingPattern", config.General.RegularFileNamingPattern),
			("general.cropRoundsFileNamingPattern", config.General.CropRoundsFileNamingPattern),
			("general.demoDirectory", config.General.DemoDirectory),
			("general.autoCleanupEnabled", config.General.AutoCleanupEnabled),
			("general.autoCleanupIntervalMinutes", config.General.AutoCleanupIntervalMinutes),
			("general.autoCleanupFileAgeHours", config.General.AutoCleanupFileAgeHours),
			("general.enableFileLogging", config.General.EnableFileLogging),
			("general.logDirectory", config.General.LogDirectory),
			("general.logFileName", config.General.LogFileName),
			("general.logVerboseEvents", config.General.LogVerboseEvents),
			("discord.webhookConfigured", !string.IsNullOrWhiteSpace(config.Discord.WebhookURL)),
			("discord.webhookUploadFile", config.Discord.WebhookUploadFile),
			("discord.serverBoost", config.Discord.ServerBoost),
			("autoRecord.enabled", config.AutoRecord.Enabled),
			("autoRecord.cropRounds", config.AutoRecord.CropRounds),
			("autoRecord.stopOnIdle", config.AutoRecord.StopOnIdle),
			("autoRecord.recordWarmup", config.AutoRecord.RecordWarmup),
			("autoRecord.idlePlayerCountThreshold", config.AutoRecord.IdlePlayerCountThreshold),
			("autoRecord.idleTimeSeconds", config.AutoRecord.IdleTimeSeconds),
			("mega.enabled", config.Mega.Enabled),
			("mega.retentionEnabled", config.Mega.RetentionEnabled),
			("mega.retentionHours", config.Mega.RetentionHours),
			("demoRequest.enabled", config.DemoRequest.Enabled),
			("demoRequest.printAll", config.DemoRequest.PrintAll),
			("demoRequest.deleteUnused", config.DemoRequest.DeleteUnused),
			("ftp.enabled", config.Ftp.Enabled),
			("ftp.host", config.Ftp.Host),
			("ftp.port", config.Ftp.Port),
			("ftp.useSftp", config.Ftp.UseSftp),
			("ftp.retentionEnabled", config.Ftp.RetentionEnabled),
			("ftp.retentionHours", config.Ftp.RetentionHours));
	}

	private void LogGameState(string context)
	{
		if (!Config.CurrentValue.General.LogVerboseEvents)
			return;

		var gameRules = TryGetGameRules();

		WriteLog(LogLevel.Debug, "State", $"Game state ({context})",
			("map", GetSafeMapName()),
			("round", GetSafeRound()),
			("totalRoundsPlayed", gameRules?.TotalRoundsPlayed),
			("warmupPeriod", gameRules?.WarmupPeriod),
			("playerCount", GetSafePlayerCount()),
			("isRecording", _isRecording),
			("fileName", _fileName ?? "null"),
			("canAutoStartRecording", CanAutoStartRecording()));
	}

	private void WriteLog(LogLevel level, string category, string message, params (string Key, object? Value)[] details)
	{
		var detailText = FormatDetails(details);
		var fullMessage = string.IsNullOrEmpty(detailText) ? message : $"{message} | {detailText}";

		LogToConsole(level, category, fullMessage);

		if (!Config.CurrentValue.General.EnableFileLogging || _logWriter == null)
			return;

		var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {fullMessage}";

		lock (_logLock)
		{
			try
			{
				_logWriter.WriteLine(line);
			}
			catch (Exception ex)
			{
				LogToConsole(LogLevel.Error, "Logging", $"Failed to write log file entry | error={ex.Message}");
			}
		}
	}

	private void LogToConsole(LogLevel level, string category, string message)
	{
		switch (level)
		{
			case LogLevel.Error:
			case LogLevel.Critical:
				Core.Logger.LogError("[{Category}] {Message}", category, message);
				break;
			case LogLevel.Warning:
				Core.Logger.LogWarning("[{Category}] {Message}", category, message);
				break;
			case LogLevel.Debug:
			case LogLevel.Trace:
				Core.Logger.LogDebug("[{Category}] {Message}", category, message);
				break;
			default:
				Core.Logger.LogInformation("[{Category}] {Message}", category, message);
				break;
		}
	}

	private static string FormatDetails(params (string Key, object? Value)[] details)
	{
		if (details.Length == 0)
			return string.Empty;

		return string.Join(" | ", details.Select(d => $"{d.Key}={FormatLogValue(d.Value)}"));
	}

	private static string FormatLogValue(object? value) =>
		value switch
		{
			null => "null",
			bool b => b ? "true" : "false",
			string s => string.IsNullOrEmpty(s) ? "\"\"" : s,
			IEnumerable<(string Name, ulong SteamId)> requesters => $"[{string.Join(", ", requesters.Select(r => $"{r.Name}({r.SteamId})"))}]",
			_ => value.ToString() ?? "null"
		};
}
