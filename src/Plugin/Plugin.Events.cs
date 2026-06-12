using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

namespace K4GOTV;

public sealed partial class Plugin
{
	private void RegisterEvents()
	{
		Core.Event.OnMapLoad += OnMapLoad;
		Core.Event.OnMapUnload += OnMapUnload;

		Core.GameEvent.HookPost<EventCsWinPanelMatch>(OnMatchEnd);
		Core.GameEvent.HookPost<EventRoundStart>(OnRoundStart);
		Core.GameEvent.HookPost<EventPlayerActivate>(OnPlayerActivate);

		WriteLog(LogLevel.Information, "Events", "Event hooks registered",
			("mapLoad", true),
			("mapUnload", true),
			("matchEnd", true),
			("roundStart", true),
			("playerActivate", true));
	}

	private void RegisterCommands()
	{
		if (Config.CurrentValue.DemoRequest.Enabled)
		{
			Core.Command.RegisterCommand("demo", OnDemoRequest);
			WriteLog(LogLevel.Information, "Commands", "Registered command", ("command", "demo"));
		}
		else
		{
			WriteLog(LogLevel.Information, "Commands", "Demo request command disabled by config");
		}
	}

	private void OnMapLoad(IOnMapLoadEvent @event)
	{
		WriteLog(LogLevel.Information, "Events", "OnMapLoad",
			("map", GetSafeMapName()),
			("demoDirectory", DemoDirectory));

		Core.Scheduler.DelayBySeconds(0.1f, () =>
		{
			Directory.CreateDirectory(DemoDirectory);
			WriteLog(LogLevel.Debug, "Events", "Demo directory ensured", ("demoDirectory", DemoDirectory));
		});
	}

	private void OnMapUnload(IOnMapUnloadEvent @event)
	{
		WriteLog(LogLevel.Information, "Events", "OnMapUnload",
			("map", _currentMapName),
			("isRecording", _isRecording),
			("fileName", _fileName ?? "null"));

		StopRecording(isMapUnload: true, reason: "map_unload");
	}

	private HookResult OnMatchEnd(EventCsWinPanelMatch @event)
	{
		WriteLog(LogLevel.Information, "Events", "OnMatchEnd",
			("isRecording", _isRecording),
			("fileName", _fileName ?? "null"),
			("round", GetSafeRound()),
			("playerCount", GetSafePlayerCount()));

		StopRecording(reason: "match_end");
		return HookResult.Continue;
	}

	private HookResult OnRoundStart(EventRoundStart @event)
	{
		LogGameState("OnRoundStart");

		if (!Config.CurrentValue.AutoRecord.Enabled)
		{
			WriteLog(LogLevel.Debug, "Events", "OnRoundStart ignored, auto-record disabled");
			return HookResult.Continue;
		}

		WriteLog(LogLevel.Information, "Events", "OnRoundStart",
			("round", GetSafeRound()),
			("playerCount", GetRealPlayerCount()),
			("isRecording", _isRecording),
			("cropRounds", Config.CurrentValue.AutoRecord.CropRounds),
			("canAutoStartRecording", CanAutoStartRecording()));

		if (Config.CurrentValue.AutoRecord.CropRounds && _isRecording)
		{
			WriteLog(LogLevel.Information, "Events", "OnRoundStart stopping recording for crop rounds",
				("fileName", _fileName ?? "null"),
				("round", GetSafeRound()));
			StopRecording(reason: "crop_round");
		}

		if (Config.CurrentValue.AutoRecord.CropRounds)
		{
			WriteLog(LogLevel.Debug, "Events", "OnRoundStart cleared demo requesters",
				("previousRequesterCount", _requesters.Count));
			_requesters.Clear();
		}

		if (!_isRecording && GetRealPlayerCount() > 0 && CanAutoStartRecording())
		{
			WriteLog(LogLevel.Information, "Events", "OnRoundStart scheduling StartRecording",
				("playerCount", GetRealPlayerCount()),
				("round", GetSafeRound()));
			Core.Scheduler.NextWorldUpdate(() => StartRecording("autodemo"));
		}
		else if (!_isRecording)
		{
			WriteLog(LogLevel.Information, "Events", "OnRoundStart skipped StartRecording",
				("playerCount", GetRealPlayerCount()),
				("canAutoStartRecording", CanAutoStartRecording()),
				("isRecording", _isRecording));
		}

		return HookResult.Continue;
	}

	private HookResult OnPlayerActivate(EventPlayerActivate @event)
	{
		var player = Core.PlayerManager.GetPlayer(@event.UserId);
		if (player?.IsValid != true || player.IsFakeClient || player.Controller?.IsHLTV == true)
			return HookResult.Continue;

		_lastPlayerCheckTime = Core.Engine.GlobalVars.CurrentTime;

		WriteLog(LogLevel.Debug, "Events", "OnPlayerActivate",
			("userId", @event.UserId),
			("playerName", player.Controller?.PlayerName ?? "Unknown"),
			("steamId", player.Controller?.SteamID ?? 0),
			("playerCount", GetRealPlayerCount()),
			("isRecording", _isRecording),
			("autoRecordEnabled", Config.CurrentValue.AutoRecord.Enabled),
			("canAutoStartRecording", CanAutoStartRecording()));

		if (!_isRecording && Config.CurrentValue.AutoRecord.Enabled && CanAutoStartRecording())
		{
			WriteLog(LogLevel.Information, "Events", "OnPlayerActivate triggering StartRecording",
				("playerName", player.Controller?.PlayerName ?? "Unknown"),
				("playerCount", GetRealPlayerCount()));
			StartRecording("autodemo");
		}

		return HookResult.Continue;
	}

	private void OnDemoRequest(ICommandContext ctx)
	{
		var player = ctx.Sender;
		if (player?.IsValid != true)
		{
			WriteLog(LogLevel.Warning, "Events", "OnDemoRequest ignored, invalid sender");
			return;
		}

		var steamId = player.Controller?.SteamID ?? 0;
		var name = player.Controller?.PlayerName ?? "Unknown";

		WriteLog(LogLevel.Information, "Events", "OnDemoRequest",
			("playerName", name),
			("steamId", steamId),
			("printAll", Config.CurrentValue.DemoRequest.PrintAll),
			("alreadyRequestedThisRound", _demoRequestedThisRound),
			("requesterCount", _requesters.Count));

		var localizer = Core.Translation.GetPlayerLocalizer(player);

		if (Config.CurrentValue.DemoRequest.PrintAll && !_demoRequestedThisRound)
		{
			foreach (var p in Core.PlayerManager.GetAllPlayers().Where(p => p.IsValid && !p.IsFakeClient))
			{
				var loc = Core.Translation.GetPlayerLocalizer(p);
				p.SendChat($" {loc["k4.general.prefix"]} {loc["k4.chat.demo.request.all", player.Controller?.PlayerName ?? "Unknown"]}");
			}
		}
		else
		{
			player.SendChat($" {localizer["k4.general.prefix"]} {localizer["k4.chat.demo.request.self"]}");
		}

		if (!_requesters.Any(r => r.SteamId == steamId))
			_requesters.Add((name, steamId));

		_demoRequestedThisRound = true;

		WriteLog(LogLevel.Information, "Events", "OnDemoRequest completed",
			("requesterCount", _requesters.Count),
			("requesters", _requesters));
	}
}
