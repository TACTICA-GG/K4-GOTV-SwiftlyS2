using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace K4GOTV;

public sealed partial class Plugin
{
	private CancellationTokenSource? _idleTimerCts;

	private bool _isRecording;
	private string? _fileName;
	private string _currentMapName = "unknown";
	private string _currentServerName = "CS2 Server";
	private double _demoStartTime;
	private DateTime _realStartTime;
	private double _lastPlayerCheckTime;
	private bool _demoRequestedThisRound;
	private int _lastKnownPlayerCount;
	private readonly List<(string Name, ulong SteamId)> _requesters = [];

	private bool CanAutoStartRecording()
	{
		if (Config.CurrentValue.AutoRecord.RecordWarmup)
			return true;

		var gameRules = TryGetGameRules();
		return gameRules?.WarmupPeriod != true;
	}

	private void StartRecording(string baseName)
	{
		var gameRules = TryGetGameRules();

		WriteLog(LogLevel.Information, "Recording", "StartRecording called",
			("baseName", baseName),
			("isRecording", _isRecording),
			("recordWarmup", Config.CurrentValue.AutoRecord.RecordWarmup),
			("warmupPeriod", gameRules?.WarmupPeriod),
			("canAutoStartRecording", CanAutoStartRecording()),
			("playerCount", GetSafePlayerCount()),
			("round", GetSafeRound()),
			("map", GetSafeMapName()));

		if (_isRecording)
		{
			WriteLog(LogLevel.Information, "Recording", "StartRecording skipped, already recording",
				("fileName", _fileName ?? "null"));
			return;
		}

		if (!CanAutoStartRecording())
		{
			WriteLog(LogLevel.Information, "Recording", "StartRecording skipped, warmup active and RecordWarmup disabled",
				("warmupPeriod", gameRules?.WarmupPeriod),
				("recordWarmup", Config.CurrentValue.AutoRecord.RecordWarmup));
			return;
		}

		_currentMapName = GetSafeMapName();
		_currentServerName = GetSafeServerName();

		var pattern = Config.CurrentValue.AutoRecord.CropRounds
			? Config.CurrentValue.General.CropRoundsFileNamingPattern
			: Config.CurrentValue.General.RegularFileNamingPattern;

		_fileName = SanitizeFileName(BuildFileName(pattern, baseName));
		var fullPath = Path.Combine(DemoDirectory, $"{_fileName}.dem");

		var counter = 1;
		while (File.Exists(fullPath))
		{
			WriteLog(LogLevel.Debug, "Recording", "Demo file already exists, trying alternate name",
				("existingPath", fullPath),
				("counter", counter));
			_fileName = $"{_fileName}_{counter++}";
			fullPath = Path.Combine(DemoDirectory, $"{_fileName}.dem");
		}

		_lastKnownPlayerCount = Math.Max(1, GetSafePlayerCount());

		WriteLog(LogLevel.Information, "Recording", "Executing tv_record",
			("command", $"tv_record \"{fullPath}\""),
			("fileName", _fileName),
			("fullPath", fullPath),
			("rawMap", GetSafeMapName()),
			("mapForFile", GetSafeMapNameForFile()),
			("pattern", pattern),
			("cropRounds", Config.CurrentValue.AutoRecord.CropRounds),
			("serverName", _currentServerName));

		try
		{
			Core.Engine.ExecuteCommand($"tv_record \"{fullPath}\"");
		}
		catch (Exception ex)
		{
			WriteLog(LogLevel.Error, "Recording", "Failed to execute tv_record",
				("fullPath", fullPath),
				("error", ex.Message));
			return;
		}

		_isRecording = true;
		_demoStartTime = GetSafeCurrentTime();
		_realStartTime = DateTime.UtcNow;
		_lastPlayerCheckTime = _demoStartTime;

		WriteLog(LogLevel.Information, "Recording", "Recording started",
			("fileName", _fileName),
			("map", _currentMapName),
			("serverName", _currentServerName),
			("round", GetSafeRound()),
			("playerCount", _lastKnownPlayerCount),
			("demoStartTime", _demoStartTime),
			("realStartTimeUtc", _realStartTime.ToString("O")),
			("stopOnIdle", Config.CurrentValue.AutoRecord.StopOnIdle),
			("idlePlayerCountThreshold", Config.CurrentValue.AutoRecord.IdlePlayerCountThreshold),
			("idleTimeSeconds", Config.CurrentValue.AutoRecord.IdleTimeSeconds));

		if (Config.CurrentValue.AutoRecord.StopOnIdle)
		{
			_idleTimerCts?.Cancel();
			_idleTimerCts = Core.Scheduler.RepeatBySeconds(1f, CheckIdleState);
			WriteLog(LogLevel.Debug, "Recording", "Idle monitor started");
		}
	}

	private void StopRecording(bool isMapUnload = false, string reason = "manual")
	{
		WriteLog(LogLevel.Information, "Recording", "StopRecording called",
			("reason", reason),
			("isMapUnload", isMapUnload),
			("isRecording", _isRecording),
			("fileName", _fileName ?? "null"),
			("map", _currentMapName),
			("round", isMapUnload ? "n/a" : GetSafeRound()),
			("playerCount", _lastKnownPlayerCount),
			("requesterCount", _requesters.Count));

		_idleTimerCts?.Cancel();
		_idleTimerCts = null;

		if (!_isRecording || string.IsNullOrEmpty(_fileName))
		{
			WriteLog(LogLevel.Information, "Recording", "StopRecording skipped, nothing to stop",
				("reason", reason),
				("isMapUnload", isMapUnload));

			if (!isMapUnload)
				ResetRecordingState(reason);

			return;
		}

		var stoppedFileName = _fileName;
		var demoPath = Path.Combine(DemoDirectory, $"{stoppedFileName}.dem");

		double duration = 0;
		string mapName = _currentMapName;
		string serverName = _currentServerName;
		int round = 1;
		int playerCount = _lastKnownPlayerCount;
		var requesters = new List<(string Name, ulong SteamId)>();

		if (isMapUnload)
		{
			try { duration = (DateTime.UtcNow - _realStartTime).TotalSeconds; } catch { duration = 0; }

			WriteLog(LogLevel.Information, "Recording", "StopRecording map unload path",
				("reason", reason),
				("fileName", stoppedFileName),
				("demoPath", demoPath),
				("durationSeconds", duration));
		}
		else
		{
			var currentTime = GetSafeCurrentTime();
			duration = Math.Max(0, currentTime - _demoStartTime);
			requesters = _requesters.ToList();
			mapName = GetSafeMapName();
			serverName = GetSafeServerName();
			round = GetSafeRound();
			UpdateLastKnownPlayerCount();
			playerCount = Math.Max(_lastKnownPlayerCount, requesters.Count);

			WriteLog(LogLevel.Information, "Recording", "Executing tv_stoprecord",
				("reason", reason),
				("fileName", stoppedFileName),
				("demoPath", demoPath),
				("durationSeconds", duration),
				("round", round),
				("playerCount", playerCount),
				("requesters", requesters));

			try
			{
				Core.Engine.ExecuteCommand("tv_stoprecord");
			}
			catch (Exception ex)
			{
				WriteLog(LogLevel.Error, "Recording", "Failed to stop GOTV recording",
					("reason", reason),
					("error", ex.Message));
			}
		}

		ResetRecordingState(reason);

		Task.Run(async () =>
		{
			try
			{
				if (isMapUnload)
				{
					WriteLog(LogLevel.Debug, "Recording", "Waiting before demo verification after map unload",
						("delayMs", 3000));
					await Task.Delay(3000);
				}

				WriteLog(LogLevel.Information, "Recording", "Waiting for demo file to be finalized on disk",
					("expectedPath", demoPath),
					("timeoutSeconds", 60));

				var finalDemoPath = await WaitForDemoFileAsync(demoPath, TimeSpan.FromSeconds(60));

				if (finalDemoPath == null)
				{
					WriteLog(LogLevel.Error, "Recording", "Demo file could not be verified on disk",
						("expectedPath", demoPath),
						("reason", reason));
					return;
				}

				var demoInfo = new FileInfo(finalDemoPath);
				WriteLog(LogLevel.Information, "Recording", "Demo file verified, starting processing",
					("finalDemoPath", finalDemoPath),
					("fileSizeBytes", demoInfo.Length),
					("durationSeconds", duration),
					("round", round),
					("playerCount", playerCount),
					("requesters", requesters));

				await ProcessDemoAsync(
					stoppedFileName,
					finalDemoPath,
					requesters,
					TimeSpan.FromSeconds(duration > 0 ? duration : 300),
					round,
					playerCount,
					mapName,
					serverName
				);
			}
			catch (Exception ex)
			{
				WriteLog(LogLevel.Error, "Recording", "Error in background upload thread",
					("reason", reason),
					("error", ex.Message));
			}
		});
	}

	public async Task<string?> WaitForDemoFileAsync(string expectedPath, TimeSpan timeout)
	{
		const int requiredStableChecks = 2;
		var startedAt = DateTime.UtcNow;
		long lastSize = -1;
		var stableCount = 0;
		string? candidatePath = null;

		WriteLog(LogLevel.Debug, "Recording", "WaitForDemoFileAsync started",
			("expectedPath", expectedPath),
			("timeoutSeconds", timeout.TotalSeconds),
			("requiredStableChecks", requiredStableChecks));

		while (DateTime.UtcNow - startedAt < timeout)
		{
			var resolvedPath = ResolveDemoFilePath(expectedPath);

			if (resolvedPath != null)
			{
				candidatePath = resolvedPath;
				var info = new FileInfo(resolvedPath);

				if (info.Length > 0 && IsDemoFileReadable(resolvedPath))
				{
					if (info.Length == lastSize)
					{
						stableCount++;
						WriteLog(LogLevel.Debug, "Recording", "WaitForDemoFileAsync stability check passed",
							("path", resolvedPath),
							("fileSizeBytes", info.Length),
							("stableCount", stableCount),
							("requiredStableChecks", requiredStableChecks));

						if (stableCount >= requiredStableChecks)
						{
							WriteLog(LogLevel.Information, "Recording", "WaitForDemoFileAsync demo ready",
								("path", resolvedPath),
								("fileSizeBytes", info.Length),
								("elapsedMs", (DateTime.UtcNow - startedAt).TotalMilliseconds));
							return resolvedPath;
						}
					}
					else
					{
						WriteLog(LogLevel.Debug, "Recording", "WaitForDemoFileAsync file still growing",
							("path", resolvedPath),
							("previousSizeBytes", lastSize),
							("currentSizeBytes", info.Length));
						stableCount = 0;
						lastSize = info.Length;
					}
				}
				else
				{
					WriteLog(LogLevel.Debug, "Recording", "WaitForDemoFileAsync file found but not readable yet",
						("path", resolvedPath),
						("fileSizeBytes", info.Length),
						("readable", IsDemoFileReadable(resolvedPath)));
					stableCount = 0;
					lastSize = -1;
				}
			}

			await Task.Delay(1000);
		}

		WriteLog(LogLevel.Warning, "Recording", "WaitForDemoFileAsync timed out",
			("expectedPath", expectedPath),
			("candidatePath", candidatePath ?? "null"),
			("lastSizeBytes", lastSize),
			("stableCount", stableCount),
			("timeoutSeconds", timeout.TotalSeconds));

		return null;
	}

	private static string? ResolveDemoFilePath(string expectedPath)
	{
		if (File.Exists(expectedPath))
		{
			var info = new FileInfo(expectedPath);
			if (info.Length > 0)
				return expectedPath;
		}

		if (File.Exists(expectedPath + ".dem"))
		{
			var info = new FileInfo(expectedPath + ".dem");
			if (info.Length > 0)
				return expectedPath + ".dem";
		}

		var directory = Path.GetDirectoryName(expectedPath);
		var baseName = Path.GetFileNameWithoutExtension(expectedPath);

		if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
		{
			var files = Directory.GetFiles(directory, $"{baseName}*.dem");
			if (files.Length > 0)
			{
				var file = files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
				if (new FileInfo(file).Length > 0)
					return file;
			}
		}

		return null;
	}

	private static bool IsDemoFileReadable(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			return stream.Length > 0;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private void ResetRecordingState(string reason = "unknown")
	{
		WriteLog(LogLevel.Debug, "Recording", "ResetRecordingState",
			("reason", reason),
			("previousFileName", _fileName ?? "null"),
			("previousRequesterCount", _requesters.Count));

		_isRecording = false;
		_fileName = null;
		_currentMapName = "unknown";
		_currentServerName = "CS2 Server";
		_demoStartTime = 0;
		_demoRequestedThisRound = false;
		_lastKnownPlayerCount = 0;

		try
		{
			_requesters.Clear();
		}
		catch
		{
			// ignored
		}
	}

	private void CheckIdleState()
	{
		try
		{
			if (!_isRecording)
				return;

			UpdateLastKnownPlayerCount();

			var playerCount = GetSafePlayerCount();

			if (playerCount < Config.CurrentValue.AutoRecord.IdlePlayerCountThreshold)
			{
				var idleTime = GetSafeCurrentTime() - _lastPlayerCheckTime;

				WriteLog(LogLevel.Debug, "Recording", "Idle check below threshold",
					("playerCount", playerCount),
					("threshold", Config.CurrentValue.AutoRecord.IdlePlayerCountThreshold),
					("idleTimeSeconds", idleTime),
					("idleLimitSeconds", Config.CurrentValue.AutoRecord.IdleTimeSeconds));

				if (idleTime > Config.CurrentValue.AutoRecord.IdleTimeSeconds)
				{
					WriteLog(LogLevel.Information, "Recording", "Stopping recording due to idle",
						("playerCount", playerCount),
						("idleTimeSeconds", idleTime),
						("fileName", _fileName ?? "null"));
					StopRecording(reason: "idle");
				}
			}
			else
			{
				_lastPlayerCheckTime = GetSafeCurrentTime();
			}
		}
		catch (Exception ex)
		{
			WriteLog(LogLevel.Error, "Recording", "CheckIdleState failed", ("error", ex.Message));
		}
	}

	private void UpdateLastKnownPlayerCount()
	{
		var playerCount = GetSafePlayerCount();

		if (playerCount > _lastKnownPlayerCount)
		{
			WriteLog(LogLevel.Debug, "Recording", "Updated last known player count",
				("previous", _lastKnownPlayerCount),
				("current", playerCount));
			_lastKnownPlayerCount = playerCount;
		}
	}

	private string BuildFileName(string pattern, string baseName)
	{
		return pattern
			.Replace("{fileName}", baseName)
			.Replace("{map}", GetSafeMapNameForFile())
			.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
			.Replace("{time}", DateTime.Now.ToString("HH-mm-ss"))
			.Replace("{timestamp}", DateTime.Now.ToString("yyyyMMdd_HHmmss"))
			.Replace("{round}", GetSafeRound().ToString())
			.Replace("{playerCount}", GetSafePlayerCount().ToString());
	}

	private double GetSafeCurrentTime()
	{
		try
		{
			return Core.Engine.GlobalVars.CurrentTime;
		}
		catch
		{
			return _demoStartTime;
		}
	}

	private string GetSafeMapName()
	{
		try
		{
			var mapName = Core.Engine.GlobalVars.MapName.ToString();
			mapName = mapName.Trim().TrimStart('/');

			if (string.IsNullOrWhiteSpace(mapName))
				return "unknown";

			return mapName;
		}
		catch
		{
			return "unknown";
		}
	}

	private string GetSafeServerName()
	{
		try
		{
			var hostname = Core.ConVar.Find<string>("hostname")?.Value?.ToString();
			return string.IsNullOrWhiteSpace(hostname) ? "Unknown Server" : hostname;
		}
		catch
		{
			return "Unknown Server";
		}
	}

	private int GetSafeRound()
	{
		try
		{
			var gameRules = TryGetGameRules();
			return (gameRules?.TotalRoundsPlayed ?? 0) + 1;
		}
		catch
		{
			return 0;
		}
	}

	private int GetSafePlayerCount()
	{
		try
		{
			return GetRealPlayerCount();
		}
		catch
		{
			return 0;
		}
	}
}

