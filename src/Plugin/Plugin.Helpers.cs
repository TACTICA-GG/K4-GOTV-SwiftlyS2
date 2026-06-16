using SwiftlyS2.Shared.SchemaDefinitions;

namespace K4GOTV;

public sealed partial class Plugin
{
	private CCSGameRules? TryGetGameRules()
	{
		try
		{
			return Core.EntitySystem.GetGameRules();
		}
		catch (Exception ex)
		{
			WriteLog(Microsoft.Extensions.Logging.LogLevel.Debug, "State", "TryGetGameRules failed",
				("error", ex.Message));
			return null;
		}
	}

	private string GetSafeMapNameForFile()
	{
		return SanitizeFileName(NormalizeWorkshopMapName(GetSafeMapName()));
	}

	private static string NormalizeWorkshopMapName(string mapName)
	{
		if (string.IsNullOrWhiteSpace(mapName))
			return "unknown";

		mapName = mapName.Trim().Replace('\\', '/');

		if (!mapName.Contains('/'))
			return mapName;

		var parts = mapName.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
			return "unknown";

		if (parts[0].Equals("workshop", StringComparison.OrdinalIgnoreCase))
		{
			if (parts.Length >= 3)
				return $"ws_{parts[1]}_{parts[^1]}";

			if (parts.Length == 2)
				return $"ws_{parts[1]}";
		}

		return parts[^1];
	}

	private static string SanitizeFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return "unknown";

		var invalid = Path.GetInvalidFileNameChars();
		var builder = new System.Text.StringBuilder(name.Length);

		foreach (var character in name)
		{
			if (invalid.Contains(character) || character is '/' or '\\' or ':')
				builder.Append('_');
			else
				builder.Append(character);
		}

		var sanitized = builder.ToString().Trim('_', '.', ' ');
		if (string.IsNullOrWhiteSpace(sanitized))
			return "unknown";

		const int maxLength = 96;
		return sanitized.Length > maxLength ? sanitized[..maxLength].TrimEnd('_') : sanitized;
	}
}
