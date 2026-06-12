using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

namespace K4GOTV;

[PluginMetadata(
	Id = "k4.gotv",
	Version = "1.0.3FIX",
	Name = "K4 - GOTV",
	Author = "K4ryuu+AI",
	Description = "Advanced GOTV handler with Discord, database, FTP, SFTP and Mega integration.")]
public sealed partial class Plugin(ISwiftlyCore core) : BasePlugin(core)
{
	private const string ConfigFileName = "k4-gotv.jsonc";
	private const string ConfigSection = "K4GOTV";

	public static IOptionsMonitor<PluginConfig> Config { get; private set; } = null!;

	private DatabaseService? _database;

	private CancellationTokenSource? _cleanupTimerCts;
	private CancellationTokenSource? _ftpRetentionTimerCts;
	private CancellationTokenSource? _megaRetentionTimerCts;

	private string DemoDirectory => Path.Combine(Core.CSGODirectory, Config.CurrentValue.General.DemoDirectory);
	private string RetentionFilePath => Path.Combine(Core.PluginDataDirectory, "uploads_retention.json");
	private string PayloadTemplatePath => Path.Combine(Core.PluginPath, "resources", "payload.json");

	private static int MaxDiscordFileSizeMB => Config.CurrentValue.Discord.ServerBoost switch { 2 => 50, 3 => 100, _ => 25 };

	public override void Load(bool hotReload)
	{
		Core.Configuration
			.InitializeJsonWithModel<PluginConfig>(ConfigFileName, ConfigSection)
			.Configure(builder =>
			{
				builder.AddJsonFile(ConfigFileName, optional: false, reloadOnChange: true);
			});

		ServiceCollection services = new();
		services.AddSwiftly(Core)
			.AddOptionsWithValidateOnStart<PluginConfig>()
			.BindConfiguration(ConfigSection);

		var provider = services.BuildServiceProvider();
		Config = provider.GetRequiredService<IOptionsMonitor<PluginConfig>>();

		InitializeLogging();
		Config.OnChange(_ => LogConfigSnapshot("reload"));

		Directory.CreateDirectory(DemoDirectory);

		WriteLog(LogLevel.Information, "Plugin", "Plugin loaded",
			("hotReload", hotReload),
			("demoDirectory", DemoDirectory),
			("pluginDataDirectory", Core.PluginDataDirectory),
			("pluginPath", Core.PluginPath));

		LogConfigSnapshot("load");

		if (!hotReload && Config.CurrentValue.General.DeleteEveryDemoFromServerAfterServerStart)
		{
			WriteLog(LogLevel.Information, "Plugin", "Scheduling server-start demo cleanup");
			Task.Run(DeleteEveryLocalDemoFileAfterServerStartAsync);
		}

		InitializeDatabase();
		RegisterEvents();
		RegisterCommands();
		StartTimers();

		if (hotReload && Config.CurrentValue.AutoRecord.Enabled && GetRealPlayerCount() > 0 && CanAutoStartRecording())
		{
			WriteLog(LogLevel.Information, "Plugin", "Hot reload auto-start recording requested",
				("playerCount", GetRealPlayerCount()));
			StartRecording("autodemo");
		}
		else if (hotReload)
		{
			WriteLog(LogLevel.Information, "Plugin", "Hot reload auto-start recording skipped",
				("autoRecordEnabled", Config.CurrentValue.AutoRecord.Enabled),
				("playerCount", GetRealPlayerCount()),
				("canAutoStartRecording", CanAutoStartRecording()));
		}
	}

	public override void Unload()
	{
		WriteLog(LogLevel.Information, "Plugin", "Plugin unloading");
		StopRecording(isMapUnload: true, reason: "plugin_unload");

		_cleanupTimerCts?.Cancel();
		_ftpRetentionTimerCts?.Cancel();
		_megaRetentionTimerCts?.Cancel();

		ShutdownLogging();
	}

	private void InitializeDatabase()
	{
		if (!string.IsNullOrEmpty(Config.CurrentValue.DatabaseConnection))
		{
			WriteLog(LogLevel.Information, "Database", "Initializing database connection");
			_database = new DatabaseService(Core, Config.CurrentValue.DatabaseConnection);
			Task.Run(_database.InitializeAsync);
		}
		else
		{
			WriteLog(LogLevel.Information, "Database", "Database connection not configured, skipping");
		}
	}

	private void StartTimers()
	{
		if (Config.CurrentValue.General.AutoCleanupEnabled)
		{
			WriteLog(LogLevel.Information, "Timers", "Starting auto-cleanup timer",
				("intervalMinutes", Config.CurrentValue.General.AutoCleanupIntervalMinutes),
				("fileAgeHours", Config.CurrentValue.General.AutoCleanupFileAgeHours));
			_cleanupTimerCts = Core.Scheduler.RepeatBySeconds(Config.CurrentValue.General.AutoCleanupIntervalMinutes * 60f, () => Task.Run(CleanupOldFiles));
		}

		if (Config.CurrentValue.Ftp.RetentionEnabled)
		{
			WriteLog(LogLevel.Information, "Timers", "Starting FTP retention timer",
				("retentionHours", Config.CurrentValue.Ftp.RetentionHours));
			_ftpRetentionTimerCts = Core.Scheduler.RepeatBySeconds(3600f, () => Task.Run(CleanFtpRetentionAsync));
		}

		if (Config.CurrentValue.Mega.RetentionEnabled)
		{
			WriteLog(LogLevel.Information, "Timers", "Starting Mega retention timer",
				("retentionHours", Config.CurrentValue.Mega.RetentionHours));
			_megaRetentionTimerCts = Core.Scheduler.RepeatBySeconds(3600f, () => Task.Run(CleanMegaRetentionAsync));
		}
	}

	private int GetRealPlayerCount() =>
		Core.PlayerManager.GetAllPlayers().Count(p => p.IsValid && !p.IsFakeClient && p.Controller?.IsHLTV != true);
}
