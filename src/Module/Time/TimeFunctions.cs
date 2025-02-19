namespace K4System
{
	using CounterStrikeSharp.API;
	using CounterStrikeSharp.API.Core;
	using CounterStrikeSharp.API.Modules.Utils;

	using MySqlConnector;
	using Nexd.MySQL;
	using Microsoft.Extensions.Logging;
	using System.Text;
	using CounterStrikeSharp.API.Modules.Entities;
	using CounterStrikeSharp.API.Modules.Timers;

	public partial class ModuleTime : IModuleTime
	{
		public async Task LoadTimeData(int slot, string name, string steamid)
		{
			string escapedName = MySqlHelper.EscapeString(name);

			await Database.ExecuteNonQueryAsync($@"
				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4times` (`name`, `steam_id`)
				VALUES (
					'{escapedName}',
					'{steamid}'
				)
				ON DUPLICATE KEY UPDATE
					`name` = '{escapedName}';
			");

			MySqlQueryResult result = await Database.ExecuteQueryAsync($@"
				SELECT *
				FROM `{Config.DatabaseSettings.TablePrefix}k4times`
				WHERE `steam_id` = '{steamid}';
			");

			string[] timeFieldNames = { "all", "ct", "t", "spec", "alive", "dead" };

			DateTime now = DateTime.UtcNow;

			TimeData playerData = new TimeData
			{
				TimeFields = new Dictionary<string, int>(),
				Times = new Dictionary<string, DateTime>
				{
					{ "Connect", now },
					{ "Team", now },
					{ "Death", now }
				}
			};

			foreach (string timeField in timeFieldNames)
			{
				playerData.TimeFields[timeField] = result.Rows > 0 ? result.Get<int>(0, timeField) : 0;
			}

			timeCache[slot] = playerData;
		}

		public void SavePlayerTimeCache(CCSPlayerController player, bool remove)
		{
			var savedSlot = player.Slot;
			var savedName = player.PlayerName;

			SteamID steamid = new SteamID(player.SteamID);

			Task.Run(async () =>
			{
				await SavePlayerTimeCacheAsync(savedSlot, savedName, steamid, remove);
			});
		}

		public async Task SavePlayerTimeCacheAsync(int slot, string name, SteamID steamid, bool remove)
		{
			if (!timeCache.ContainsKey(slot))
			{
				Logger.LogWarning($"SavePlayerTimeCache > Player is not loaded to the cache ({name})");
				return;
			}

			string escapedName = MySqlHelper.EscapeString(name);

			StringBuilder queryBuilder = new StringBuilder();
			queryBuilder.Append($@"
   				INSERT INTO `{Config.DatabaseSettings.TablePrefix}k4times` (`steam_id`, `name`");

			TimeData playerData = timeCache[slot];

			foreach (var field in playerData.TimeFields)
			{
				queryBuilder.Append($", `{field.Key}`");
			}

			queryBuilder.Append($@")
				VALUES ('{steamid.SteamId64}', '{escapedName}'");

			foreach (var field in playerData.TimeFields)
			{
				queryBuilder.Append($", {field.Value}");
			}

			queryBuilder.Append($@")
				ON DUPLICATE KEY UPDATE");

			// Here, we modify the loop to correctly handle the comma
			int fieldCount = playerData.TimeFields.Count;
			int i = 0;
			foreach (var field in playerData.TimeFields)
			{
				queryBuilder.Append($"`{field.Key}` = VALUES(`{field.Key}`)");
				if (++i < fieldCount)
				{
					queryBuilder.Append(", ");
				}
			}

			queryBuilder.Append(";");

			if (!remove)
			{
				queryBuilder.Append($@"
				SELECT * FROM `{Config.DatabaseSettings.TablePrefix}k4times`
				WHERE `steam_id` = '{steamid.SteamId64}';");
			}

			string insertOrUpdateQuery = queryBuilder.ToString();

			MySqlQueryResult result = await Database.ExecuteQueryAsync(insertOrUpdateQuery);

			if (Config.GeneralSettings.LevelRanksCompatibility)
			{
				// ? STEAM_0:0:12345678 -> STEAM_1:0:12345678 just to match lvlranks as we can
				string lvlSteamID = steamid.SteamId2.Replace("STEAM_0", "STEAM_1");

				await Database.ExecuteNonQueryAsync($@"
					INSERT INTO `{Config.DatabaseSettings.LvLRanksTableName}`
					(`steam`, `name`, `playtime`, `lastconnect`)
					VALUES
					('{lvlSteamID}', '{escapedName}', {playerData.TimeFields["all"]}, UNIX_TIMESTAMP())
					ON DUPLICATE KEY UPDATE
					`name` = '{escapedName}',
					`playtime` = (`playtime` + {playerData.TimeFields["all"]}),
					`lastconnect` = UNIX_TIMESTAMP();
				");
			}

			if (!remove)
			{
				var allKeys = playerData.TimeFields.Keys.ToList();

				foreach (string statField in allKeys)
				{
					playerData.TimeFields[statField] = result.Rows > 0 ? result.Get<int>(0, statField) : 0;
				}
			}
			else
			{
				timeCache.Remove(slot);
			}
		}

		public void LoadAllPlayerCache()
		{
			List<CCSPlayerController> players = Utilities.GetPlayers();

			List<Task> loadTasks = players
				.Where(player => player != null && player.IsValid && player.PlayerPawn.IsValid && !player.IsBot && !player.IsHLTV)
				.Select(player => LoadTimeData(player.Slot, player.PlayerName, player.SteamID.ToString()))
				.ToList();

			Task.Run(async () =>
			{
				await Task.WhenAll(loadTasks);
			});
		}

		public void SaveAllPlayerCache(bool clear)
		{
			List<CCSPlayerController> players = Utilities.GetPlayers();

			List<Task> saveTasks = players
				.Where(player => player != null && player.IsValid && player.PlayerPawn.IsValid && !player.IsBot && !player.IsHLTV && player.SteamID != 0 && timeCache.ContainsPlayer(player))
				.Select(player => SavePlayerTimeCacheAsync(player.Slot, player.PlayerName, new SteamID(player.SteamID), clear))
				.ToList();

			Task.Run(async () =>
			{
				await Task.WhenAll(saveTasks);

				if (clear)
					timeCache.Clear();
			});
		}

		public string GetFieldForTeam(CsTeam team)
		{
			switch (team)
			{
				case CsTeam.Terrorist:
					return "t";
				case CsTeam.CounterTerrorist:
					return "ct";
				default:
					return "spec";
			}
		}

		public string FormatPlaytime(int totalSeconds)
		{
			string[] units = { "k4.phrases.shortyear", "k4.phrases.shortmonth", "k4.phrases.shortday", "k4.phrases.shorthour", "k4.phrases.shortminute", "k4.phrases.shortsecond" };
			int[] values = { totalSeconds / 31536000, totalSeconds % 31536000 / 2592000, totalSeconds % 2592000 / 86400, totalSeconds % 86400 / 3600, totalSeconds % 3600 / 60, totalSeconds % 60 };

			StringBuilder formattedTime = new StringBuilder();

			bool addedValue = false;

			Plugin plugin = (this.PluginContext.Plugin as Plugin)!;

			for (int i = 0; i < units.Length; i++)
			{
				if (values[i] > 0)
				{
					formattedTime.Append($"{values[i]}{plugin.Localizer[units[i]]}, ");
					addedValue = true;
				}
			}

			if (!addedValue)
			{
				formattedTime.Append("0s");
			}

			return formattedTime.ToString().TrimEnd(' ', ',');
		}
	}
}