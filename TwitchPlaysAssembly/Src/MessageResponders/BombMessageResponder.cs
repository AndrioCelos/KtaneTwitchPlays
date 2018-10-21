using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Assets.Scripts.Missions;
using Assets.Scripts.Props;
using UnityEngine;

public class BombMessageResponder : MessageResponder
{
	public TwitchBombHandle TwitchBombHandlePrefab;
	public TwitchModule TwitchModulePrefab;
	public ModuleCameras ModuleCamerasPrefab;

	public TwitchPlaysService ParentService;

	public List<BombCommander> BombCommanders = new List<BombCommander>();
	public List<TwitchBombHandle> BombHandles = new List<TwitchBombHandle>();
	public List<TwitchModule> ComponentHandles = new List<TwitchModule>();
	private int _currentBomb = -1;
	private readonly Dictionary<int, string> _notesDictionary = new Dictionary<int, string>();

#pragma warning disable 169
	// ReSharper disable once InconsistentNaming
	private readonly AlarmClock alarmClock;
#pragma warning restore 169

	public static ModuleCameras ModuleCameras;

	public static bool BombActive { get; private set; }

	public static BombMessageResponder Instance;

	public Dictionary<string, Dictionary<string, double>> LastClaimedModule = new Dictionary<string, Dictionary<string, double>>();

	static BombMessageResponder()
	{
		BombActive = false;
	}

	#region Unity Lifecycle

	public static bool EnableDisableInput()
	{
		if (IRCConnection.Instance.State == IRCConnectionState.Connected && TwitchPlaySettings.data.EnableTwitchPlaysMode && !TwitchPlaySettings.data.EnableInteractiveMode && BombActive)
		{
			InputInterceptor.DisableInput();
			return true;
		}
		else
		{
			InputInterceptor.EnableInput();
			return false;
		}
	}

	public static void SetCurrentBomb()
	{
		if (!BombActive || Instance == null) return;

		Instance._currentBomb = Instance.CoroutineQueue.CurrentBombID;
	}

	public static void DropCurrentBomb()
	{
		if (!BombActive || Instance == null) return;
		Instance.CoroutineQueue.AddToQueue(Instance.BombCommanders[Instance._currentBomb != -1 ? Instance._currentBomb : 0].LetGoBomb(), Instance._currentBomb);
	}

	private bool _bombStarted;
	public void OnLightsChange(bool on)
	{
		if (_bombStarted || !on) return;
		_bombStarted = true;

		if (TwitchPlaySettings.data.BombLiveMessageDelay > 0)
		{
			System.Threading.Thread.Sleep(TwitchPlaySettings.data.BombLiveMessageDelay * 1000);
		}

		TwitchPlaysService.Instance.SetHeaderVisbility(true);

		IRCConnection.SendMessage(BombCommanders.Count == 1
			? TwitchPlaySettings.data.BombLiveMessage
			: TwitchPlaySettings.data.MultiBombLiveMessage);

		StartCoroutine(AutoFillEdgework());
		GameRoom.InitializeGameModes(GameRoom.Instance.InitializeOnLightsOn);
	}

	private void OnEnable()
	{
		Instance = this;
		BombActive = true;
		EnableDisableInput();
		Leaderboard.Instance.ClearSolo();
		LogUploader.Instance.Clear();

		_bombStarted = false;
		ParentService.GetComponent<KMGameInfo>().OnLightsChange += OnLightsChange;

		StartCoroutine(CheckForBomb());
		try
		{
			string path = Path.Combine(Application.persistentDataPath, "TwitchPlaysLastClaimed.json");
			LastClaimedModule = SettingsConverter.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(path));
		}
		catch (Exception ex)
		{
			DebugHelper.LogException(ex, "Couldn't read TwitchPlaysLastClaimed.json:");
			LastClaimedModule = new Dictionary<string, Dictionary<string, double>>();
		}
	}

	public string GetBombResult(bool lastBomb = true)
	{
		bool hasDetonated = false;
		bool hasBeenSolved = true;
		float timeStarting = float.MaxValue;
		float timeRemaining = float.MaxValue;
		string timeRemainingFormatted = "";

		foreach (BombCommander commander in BombCommanders)
		{
			hasDetonated |= commander.Bomb.HasDetonated;
			hasBeenSolved &= commander.IsSolved;
			if (timeRemaining > commander.CurrentTimer)
			{
				timeStarting = commander.BombStartingTimer;
				timeRemaining = commander.CurrentTimer;
			}

			if (!string.IsNullOrEmpty(timeRemainingFormatted))
			{
				timeRemainingFormatted += ", " + commander.GetFullFormattedTime;
			}
			else
			{
				timeRemainingFormatted = commander.GetFullFormattedTime;
			}
		}

		string bombMessage;
		if (hasDetonated)
		{
			bombMessage = string.Format(TwitchPlaySettings.data.BombExplodedMessage, timeRemainingFormatted);
			Leaderboard.Instance.BombsExploded += BombCommanders.Count;
			if (!lastBomb)
				return bombMessage;

			Leaderboard.Instance.Success = false;
			TwitchPlaySettings.ClearPlayerLog();
		}
		else if (hasBeenSolved)
		{
			bombMessage = string.Format(TwitchPlaySettings.data.BombDefusedMessage, timeRemainingFormatted);
			Leaderboard.Instance.BombsCleared += BombCommanders.Count;
			bombMessage += TwitchPlaySettings.GiveBonusPoints();

			if (lastBomb)
			{
				Leaderboard.Instance.Success = true;
			}

			if (Leaderboard.Instance.CurrentSolvers.Count != 1)
				return bombMessage;

			float elapsedTime = timeStarting - timeRemaining;
			string userName = "";
			foreach (string uName in Leaderboard.Instance.CurrentSolvers.Keys)
			{
				userName = uName;
				break;
			}
			if (Leaderboard.Instance.CurrentSolvers[userName] == (Leaderboard.RequiredSoloSolves * BombCommanders.Count))
			{
				Leaderboard.Instance.AddSoloClear(userName, elapsedTime, out float previousRecord);
				if (TwitchPlaySettings.data.EnableSoloPlayMode)
				{
					//Still record solo information, should the defuser be the only one to actually defuse a 11 * bomb-count bomb, but display normal leaderboards instead if
					//solo play is disabled.
					TimeSpan elapsedTimeSpan = TimeSpan.FromSeconds(elapsedTime);
					string soloMessage = string.Format(TwitchPlaySettings.data.BombSoloDefusalMessage, Leaderboard.Instance.SoloSolver.UserName, (int) elapsedTimeSpan.TotalMinutes, elapsedTimeSpan.Seconds);
					if (elapsedTime < previousRecord)
					{
						TimeSpan previousTimeSpan = TimeSpan.FromSeconds(previousRecord);
						soloMessage += string.Format(TwitchPlaySettings.data.BombSoloDefusalNewRecordMessage, (int) previousTimeSpan.TotalMinutes, previousTimeSpan.Seconds);
					}
					soloMessage += TwitchPlaySettings.data.BombSoloDefusalFooter;
					ParentService.StartCoroutine(SendDelayedMessage(1.0f, soloMessage));
				}
				else
				{
					Leaderboard.Instance.ClearSolo();
				}
			}
			else
			{
				Leaderboard.Instance.ClearSolo();
			}
		}
		else
		{
			bombMessage = string.Format(TwitchPlaySettings.data.BombAbortedMessage, timeRemainingFormatted);
			Leaderboard.Instance.Success = false;
			TwitchPlaySettings.ClearPlayerLog();
		}
		return bombMessage;
	}

	private void OnDisable()
	{
		GameRoom.ShowCamera();
		BombActive = false;
		EnableDisableInput();
		bool claimsEnabled = TwitchModule.ClaimsEnabled;
		TwitchModule.ClaimedList.Clear();
		TwitchModule.ClearUnsupportedModules();
		if (!claimsEnabled)
			TwitchModule.ClaimsEnabled = true;
		StopAllCoroutines();
		Leaderboard.Instance.BombsAttempted++;
		// ReSharper disable once DelegateSubtraction
		ParentService.GetComponent<KMGameInfo>().OnLightsChange -= OnLightsChange;
		TwitchPlaysService.Instance.SetHeaderVisbility(false);

		LogUploader.Instance.Post();
		ParentService.StartCoroutine(SendDelayedMessage(1.0f, GetBombResult(), SendAnalysisLink));
		if (!claimsEnabled)
			ParentService.StartCoroutine(SendDelayedMessage(1.1f, "Claims have been enabled."));

		if(ModuleCameras != null)
			ModuleCameras.gameObject.SetActive(false);

		foreach (TwitchBombHandle handle in BombHandles.Where(x => x != null))
		{
			Destroy(handle.gameObject, 2.0f);
		}
		BombHandles.Clear();
		BombCommanders.Clear();

		DestroyComponentHandles();

		MusicPlayer.StopAllMusic();

		GameRoom.Instance?.OnDisable();

		try
		{
			string path = Path.Combine(Application.persistentDataPath, "TwitchPlaysLastClaimed.json");
			File.WriteAllText(path, SettingsConverter.Serialize(LastClaimedModule));
		}
		catch (Exception ex)
		{
			DebugHelper.LogException(ex, "Couldn't write TwitchPlaysLastClaimed.json:");
		}
	}

	public void DestroyComponentHandles()
	{
		if (ComponentHandles == null) return;

		foreach (TwitchModule handle in ComponentHandles)
		{
			if (handle != null)
				Destroy(handle.gameObject, 2.0f);
		}
		ComponentHandles.Clear();
	}

	#endregion

	#region Protected/Private Methods

	private IEnumerator AutoFillEdgework()
	{
		while (BombActive)
		{
			if (TwitchPlaySettings.data.EnableAutomaticEdgework)
				foreach (BombCommander commander in BombCommanders) commander.FillEdgework(commander.TwitchBombHandle.BombID != _currentBomb);
			yield return new WaitForSeconds(0.1f);
		}
	}

	private IEnumerator CheckForBomb()
	{
		yield return new WaitUntil(() => (SceneManager.Instance.GameplayState.Bombs != null && SceneManager.Instance.GameplayState.Bombs.Count > 0));
		List<Bomb> bombs = SceneManager.Instance.GameplayState.Bombs;

		try
		{
			ModuleCameras = Instantiate(ModuleCamerasPrefab);
		}
		catch (Exception ex)
		{
			DebugHelper.LogException(ex, "Failed to Instantiate the module camera system due to an exception:");
			ModuleCameras = null;
		}

		if (GameRoom.GameRoomTypes.Where((t, i) => t() != null && GameRoom.CreateRooms[i](FindObjectsOfType(t()), out GameRoom.Instance)).Any())
		{
			GameRoom.Instance.InitializeBombs(bombs);
		}

		if(ModuleCameras != null)
			ModuleCameras.ChangeBomb(BombCommanders[0]);

		try
		{
			GameRoom.Instance.InitializeBombNames();
		}
		catch (Exception ex)
		{
			DebugHelper.LogException(ex, "An exception has occured while setting the bomb names");
		}
		StartCoroutine(GameRoom.Instance.ReportBombStatus());

		try
		{
			if (GameRoom.Instance.HoldBomb)
				CoroutineQueue.AddToQueue(BombHandles[0].OnMessageReceived(new Message(BombHandles[0].BombName, "red", "bomb hold")), _currentBomb);
		}
		catch (Exception ex)
		{
			DebugHelper.LogException(ex, "An exception has occured attempting to hold the bomb.");
		}

		for (int i = 0; i < 4; i++)
		{
			_notesDictionary[i] = (OtherModes.ZenModeOn && i == 3) ? TwitchPlaySettings.data.ZenModeFreeSpace : TwitchPlaySettings.data.NotesSpaceFree;

			if (ModuleCameras != null)
				ModuleCameras.SetNotes(i, _notesDictionary[i]);
		}

		if (EnableDisableInput())
		{
			TwitchModule.SolveUnsupportedModules(true);
		}

		while (OtherModes.ZenModeOn)
		{
			foreach (BombCommander bomb in BombCommanders)
			{
				if (bomb.TimerComponent == null || bomb.TimerComponent.GetRate() < 0) continue;
				bomb.TimerComponent.SetRateModifier(-bomb.TimerComponent.GetRate());
			}
			yield return null;
		}
	}

	internal void InitializeModuleCodes()
	{
		// This method assigns a unique code to each module.

		if (TwitchPlaySettings.data.EnableLetterCodes)
		{
			// Ignore initial “the” in module names
			string SanitizedName(TwitchModule handle) => Regex.Replace(handle.BombComponent.GetModuleDisplayName(), @"^the\s+", "", RegexOptions.IgnoreCase);

			// First, assign codes “naively”
			Dictionary<string, List<TwitchModule>> dic1 = new Dictionary<string, List<TwitchModule>>();
			int numeric = 0;
			foreach (TwitchModule handle in ComponentHandles)
			{
				if (handle.BombComponent == null || handle.BombComponent.ComponentType == ComponentTypeEnum.Timer || handle.BombComponent.ComponentType == ComponentTypeEnum.Empty)
					continue;

				string moduleName = SanitizedName(handle);
				if (moduleName != null)
				{
					string code = moduleName.Where(ch => (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z' && ch != 'O')).Take(2).Join("");
					if (code.Length < 2 && moduleName.Length >= 2)
						code = moduleName.Where(char.IsLetterOrDigit).Take(2).Join("").ToUpperInvariant();
					if (code.Length == 0)
						code = (++numeric).ToString();
					handle.Code = code;
					dic1.AddSafe(code, handle);
				}
				else
				{
					handle.Code = (++numeric).ToString();
					dic1.AddSafe(handle.Code, handle);
				}
			}

			// If this assignment succeeded in generating unique codes, use it
			if (dic1.Values.All(list => list.Count < 2))
				return;

			// See if we can make them all unique by just changing some non-unique ones to different letters in the module name
			Dictionary<string, List<TwitchModule>> dic2 = dic1.Where(kvp => kvp.Value.Count < 2).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
			foreach (KeyValuePair<string, List<TwitchModule>> kvp in dic1)
			{
				if (kvp.Value.Count < 2)
					continue;

				dic2.AddSafe(kvp.Key, kvp.Value[0]);
				for (int i = 1; i < kvp.Value.Count; i++)
				{
					string moduleName = SanitizedName(kvp.Value[i]);
					for (int chIx = 1; chIx < moduleName.Length; chIx++)
					{
						string newCode = (moduleName[0] + "" + moduleName[chIx]).ToUpperInvariant();
						if (moduleName[chIx] == 'O' || !char.IsLetter(moduleName[chIx]) || dic2.ContainsKey(newCode))
							continue;

						kvp.Value[i].Code = newCode;
						dic2.AddSafe(newCode, kvp.Value[i]);
						goto processed;
					}
					dic2.AddSafe(kvp.Key, kvp.Value[i]);
					processed:;
				}
			}

			// If this assignment succeeded in generating unique codes, use it
			if (dic2.Values.All(list => list.Count < 2))
				return;

			int globalNumber = 1;

			// If still no success, gonna have to use numbers
			while (true)
			{
				KeyValuePair<string, List<TwitchModule>> tooMany = dic2.FirstOrDefault(kvp => kvp.Value.Count > 1);
				// We did it — all unique
				if (tooMany.Key == null)
					break;

				// Find other non-unique modules with the same first letter
				List<TwitchModule> all = dic2.Where(kvp => kvp.Key[0] == tooMany.Key[0] && kvp.Value.Count > 1).SelectMany(kvp => kvp.Value.Skip(1)).ToList();
				int number = 1;
				foreach (TwitchModule module in all)
				{
					dic2[module.Code].Remove(module);
					while (dic2.ContainsKey(module.Code[0] + number.ToString()))
						number++;
					if (number < 10)
						module.Code = module.Code[0] + (number++).ToString();
					else
					{
						while (dic2.ContainsKey(globalNumber.ToString()))
							globalNumber++;
						module.Code = (globalNumber++).ToString();
					}
					dic2.AddSafe(module.Code, module);
				}
			}
		}
		else
		{
			int num = 1;
			foreach (TwitchModule handle in ComponentHandles) handle.Code = num++.ToString();
		}

		foreach (TwitchModule handle in ComponentHandles.Where(c => c.IsKey))
		{
			string moduleName = handle.BombComponent.GetModuleDisplayName();
			IRCConnection.SendMessage($"Module {handle.Code} {(moduleName.EqualsAny("The Swan", "The Time Keeper") ? "is" : "is a")} {moduleName}");
		}
	}

	public void SetBomb(Bomb bomb, int id)
	{
		if (BombCommanders.Count == 0)
			_currentBomb = id == -1 ? -1 : 0;
		BombCommanders.Add(new BombCommander(bomb));
		CreateBombHandleForBomb(bomb, id);
		CreateComponentHandlesForBomb(bomb);
	}

	public void OnMessageReceived(string userNickName, string text, bool isWhisper = false) => OnMessageReceived(new Message(userNickName, null, text, isWhisper));

	protected override void OnMessageReceived(Message message)
	{
		string text = message.Text;
		bool isWhisper = message.IsWhisper;
		string userNickName = message.UserNickName;

		Match match;
		int index;
		if ((!text.StartsWith("!") && !isWhisper) || text.Equals("!")) return;
		if (text.StartsWith("!"))
			text = text.Substring(1).Trim();

		if (IsAuthorizedDefuser(userNickName, isWhisper))
		{
			if (text.RegexMatch(out match, "^notes(-?[0-9]+) (.+)$") && int.TryParse(match.Groups[1].Value, out index))
			{
				string notes = match.Groups[2].Value;

				IRCConnection.SendMessage(string.Format(TwitchPlaySettings.data.NotesTaken, index--, notes), userNickName, !isWhisper);

				_notesDictionary[index] = notes;
				if (ModuleCameras != null)
					ModuleCameras.SetNotes(index, notes);
				return;
			}

			if (text.RegexMatch(out match, "^notes(-?[0-9]+)append (.+)", "^appendnotes(-?[0-9]+) (.+)") && int.TryParse(match.Groups[1].Value, out index))
			{
				string notes = match.Groups[2].Value;

				IRCConnection.SendMessage(string.Format(TwitchPlaySettings.data.NotesAppended, index--, notes), userNickName, !isWhisper);
				if (!_notesDictionary.ContainsKey(index))
					_notesDictionary[index] = TwitchPlaySettings.data.NotesSpaceFree;

				_notesDictionary[index] += " " + notes;
				if (ModuleCameras != null)
					ModuleCameras.AppendNotes(index, notes);
				return;
			}

			if (text.RegexMatch(out match, "^clearnotes(-?[0-9]+)$", "^notes(-?[0-9]+)clear$") && int.TryParse(match.Groups[1].Value, out index))
			{
				IRCConnection.SendMessage(string.Format(TwitchPlaySettings.data.NoteSlotCleared, index--), userNickName, !isWhisper);

				_notesDictionary[index] = (OtherModes.ZenModeOn && index == 3) ? TwitchPlaySettings.data.ZenModeFreeSpace : TwitchPlaySettings.data.NotesSpaceFree;
				if (ModuleCameras != null)
					ModuleCameras.SetNotes(index, _notesDictionary[index]);
				return;
			}

			if (text.Equals("snooze", StringComparison.InvariantCultureIgnoreCase))
			{
				if (GameRoom.Instance is ElevatorGameRoom) return; //There is no alarm clock in the elevator room.
				DropCurrentBomb();
				CoroutineQueue.AddToQueue(AlarmClockHoldableHandler.Instance.RespondToCommand(userNickName, "alarmclock snooze", isWhisper));
				return;
			}

			if (text.RegexMatch(out match, "^claims (.+)"))
			{
				if (TwitchPlaySettings.data.AnarchyMode)
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, claiming modules is not available in anarchy mode.", userNickName, !isWhisper);
					return;
				}
				else if (isWhisper && TwitchPlaySettings.data.EnableWhispers)
					IRCConnection.SendMessage("Checking other people's claims in whispers is not supported", userNickName, false);
				else
					OnMessageReceived(new Message(match.Groups[1].Value, message.UserColorCode, "!claims", isWhisper));
				return;
			}

			if (text.Equals("claims", StringComparison.InvariantCultureIgnoreCase))
			{
				if (TwitchPlaySettings.data.AnarchyMode)
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, claiming modules is not available in anarchy mode.", userNickName, !isWhisper);
					return;
				}
				List<string> claimed = (
					from handle in ComponentHandles
					where handle.PlayerName != null && handle.PlayerName.Equals(userNickName, StringComparison.InvariantCultureIgnoreCase) && !handle.Solved
					select string.Format(TwitchPlaySettings.data.OwnedModule, handle.Code, handle.HeaderText)).ToList();
				if (claimed.Count > 0)
				{
					string newMessage = string.Format(TwitchPlaySettings.data.OwnedModuleList, userNickName, string.Join(", ", claimed.ToArray(), 0, Math.Min(claimed.Count, 5)));
					if (claimed.Count > 5)
						newMessage += "...";
					IRCConnection.SendMessage(newMessage, userNickName, !isWhisper);
				}
				else
					IRCConnection.SendMessage(string.Format(TwitchPlaySettings.data.NoOwnedModules, userNickName), userNickName, !isWhisper);
				return;
			}

			if (text.RegexMatch("^(?:claim ?|view ?|all ?){2,3}$"))
			{
				if (text.Contains("claim") && text.Contains("all"))
				{
					if (isWhisper)
					{
						IRCConnection.SendMessage($"Sorry {userNickName}, claiming modules is not allowed in whispers", userNickName, false);
					}
					else if (TwitchPlaySettings.data.AnarchyMode)
					{
						IRCConnection.SendMessage("Sorry {0}, claiming modules is not allowed in anarchy mode", userNickName);
						return;
					}
					foreach (TwitchModule handle in ComponentHandles)
					{
						if (handle == null || !GameRoom.Instance.IsCurrentBomb(handle.BombID)) continue;
						handle.AddToClaimQueue(userNickName, text.Contains("view"));
					}
					return;
				}
			}

			if (text.StartsWith("claim ", StringComparison.InvariantCultureIgnoreCase))
			{
				if (isWhisper)
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, claiming modules is not allowed in whispers", userNickName, false);
					return;
				}
				string[] split = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string claim in split.Skip(1))
				{
					TwitchModule handle = ComponentHandles.FirstOrDefault(x => x.Code.Equals(claim, StringComparison.InvariantCultureIgnoreCase));
					if (handle == null || !GameRoom.Instance.IsCurrentBomb(handle.BombID)) continue;
					handle.AddToClaimQueue(userNickName);
				}
				return;
			}

			if (text.RegexMatch("^(unclaim|release) ?all$"))
			{
				if (isWhisper)
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, unclaiming modules is not allowed in whispers", userNickName, false);
					return;
				}
				foreach (TwitchModule handle in ComponentHandles)
				{
					handle.RemoveFromClaimQueue(userNickName);
				}
				string[] moduleIDs = ComponentHandles.Where(x => !x.Solved && x.PlayerName != null && x.PlayerName.Equals(userNickName, StringComparison.InvariantCultureIgnoreCase))
					.Select(x => x.Code).ToArray();
				text = $"unclaim {string.Join(" ", moduleIDs)}";
			}

			if (text.RegexMatch(out match, "^(?:unclaim|release) (.+)"))
			{
				if (isWhisper)
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, unclaiming modules is not allowed in whispers", userNickName, false);
					return;
				}
				string[] split = match.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string claim in split)
				{
					TwitchModule handle = ComponentHandles.FirstOrDefault(x => x.Code.Equals(claim));
					if (handle == null || !GameRoom.Instance.IsCurrentBomb(handle.BombID)) continue;
					// ReSharper disable once MustUseReturnValue
					handle.OnMessageReceived(message.Duplicate("unclaim"));
				}
				return;
			}

			if (text.Equals("unclaimed", StringComparison.InvariantCultureIgnoreCase) && !TwitchPlaySettings.data.AnarchyMode)
			{
				IEnumerable<string> unclaimed = ComponentHandles.Where(handle => !handle.Claimed && !handle.Solved && GameRoom.Instance.IsCurrentBomb(handle.BombID)).Shuffle().Take(3)
					.Select(handle => string.Format($"{handle.HeaderText} ({handle.Code})")).ToList();

				IRCConnection.SendMessage(unclaimed.Any() 
					? $"Unclaimed Modules: {unclaimed.Join(", ")}" 
					: string.Format(TwitchPlaySettings.data.NoUnclaimed, userNickName), userNickName, !isWhisper);

				return;
			}

			if (text.Equals("unsolved", StringComparison.InvariantCultureIgnoreCase))
			{
				IEnumerable<string> unsolved = ComponentHandles.Where(handle => !handle.Solved && GameRoom.Instance.IsCurrentBomb(handle.BombID)).Shuffle().Take(3)
					.Select(handle => $"{handle.HeaderText} ({handle.Code}) - {(handle.PlayerName == null ? "Unclaimed" : "Claimed by " + handle.PlayerName)}").ToList();
				if (unsolved.Any()) IRCConnection.SendMessage($"Unsolved Modules: {unsolved.Join(", ")}", userNickName, !isWhisper);
				else
				{
					IRCConnection.SendMessage("There are no unsolved modules, something went wrong as this message should never be displayed.", userNickName, !isWhisper);
					IRCConnection.SendMessage("Please file a bug at https://github.com/samfun123/KtaneTwitchPlays", userNickName, !isWhisper); //this should never happen
				}
				return;
			}

			if (text.RegexMatch(out match, "^(?:find|search) (.+)"))
			{
				string[] queries = match.Groups[1].Value.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries);

				foreach (string query in queries)
				{
					string trimmed = query.Trim();
					IEnumerable<string> modules = ComponentHandles.Where(handle => handle.HeaderText.ContainsIgnoreCase(trimmed) && GameRoom.Instance.IsCurrentBomb(handle.BombID))
						.OrderByDescending(handle => handle.HeaderText.EqualsIgnoreCase(trimmed)).ThenBy(handle => handle.Solved).ThenBy(handle => handle.PlayerName != null).Take(3)
						.Select(handle => $"{handle.HeaderText} ({handle.Code}) - {(handle.Solved ? "Solved" : (handle.PlayerName == null ? "Unclaimed" : "Claimed by " + handle.PlayerName))}").ToList();

					IRCConnection.SendMessage(modules.Any() 
						? $"Modules: {modules.Join(", ")}" 
						: $"Couldn't find any modules containing \"{trimmed}\".", userNickName, !isWhisper);
				}

				return;
			}

			if (text.RegexMatch(out match, "^(?:findplayer|playerfind|searchplayer|playersearch) (.+)") && !TwitchPlaySettings.data.AnarchyMode)
			{
				string[] queries = match.Groups[1].Value.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
				foreach (string query in queries)
				{
					string trimmed = query.Trim();
					IEnumerable<TwitchModule> modules = ComponentHandles.Where(handle => handle.HeaderText.ContainsIgnoreCase(trimmed) && GameRoom.Instance.IsCurrentBomb(handle.BombID))
						.OrderByDescending(handle => handle.HeaderText.EqualsIgnoreCase(trimmed)).ThenBy(handle => handle.Solved).ToList();
					IEnumerable<string> playerModules = modules.Where(handle => handle.PlayerName != null).OrderByDescending(handle => handle.HeaderText.EqualsIgnoreCase(trimmed))
						.Select(handle => string.Format($"{handle.HeaderText} ({handle.Code}) - Claimed by {handle.PlayerName}")).ToList();
					if (modules.Any())
					{
						IRCConnection.SendMessage(playerModules.Any() 
							? $"Modules: {playerModules.Join(", ")}" 
							: "None of the specified modules are claimed/have been solved.", userNickName, !isWhisper);
					}
					else IRCConnection.SendMessage($"Could not find any modules containing \"{trimmed}\".", userNickName, !isWhisper);
				}
			}

			if (text.RegexMatch(out match, "^(claim ?(?:any|van|mod) ?(?:view)?|view ?claim ?(?:any|van|mod))"))
			{
				if (isWhisper)
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, claiming modules is not allowed in whispers", userNickName, false);
					return;
				}
				bool vanilla = match.Groups[1].Value.Contains("van");
				bool modded = match.Groups[1].Value.Contains("mod");
				bool view = match.Groups[1].Value.Contains("view");
				string[] avoid = new[] { "Souvenir", "Forget Me Not", "Turn The Key", "Turn The Keys", "The Swan", "Forget Everything", "The Time Keeper" };

				TwitchModule unclaimed = ComponentHandles
					.Where(handle => (vanilla ? !handle.IsMod : !modded || handle.IsMod) && !handle.Claimed && !handle.Solved && !avoid.Contains(handle.HeaderText) && GameRoom.Instance.IsCurrentBomb(handle.BombID))
					.Shuffle().FirstOrDefault();

				if (unclaimed != null)
					text = unclaimed.Code + (view ? " claimview" : " claim");
				else
					IRCConnection.SendMessage($"There are no more unclaimed{(vanilla ? " vanilla" : modded ? " modded" : null)} modules.");
			}

			if (text.RegexMatch(out match, "^(?:findsolved|solvedfind|searchsolved|solvedsearch) (.+)"))
			{
				string[] queries = match.Groups[1].Value.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
				foreach (string query in queries)
				{
					string trimmed = query.Trim();
					IEnumerable<BombCommander> commanders = BombCommanders.Where(handle => handle.SolvedModules.Keys.ToArray().Any(x => x.ContainsIgnoreCase(trimmed))).ToList();
					IEnumerable<TwitchModule> modules = commanders.SelectMany(x => x.SolvedModules.Where(y => y.Key.ContainsIgnoreCase(trimmed)))
						.OrderByDescending(x => x.Key.EqualsIgnoreCase(trimmed)).SelectMany(x => x.Value).ToList();
					IEnumerable<string> playerModules = modules.Where(handle => handle.PlayerName != null)
						.Select(handle => string.Format($"{handle.HeaderText} ({handle.Code}) - Claimed by {handle.PlayerName}", handle.HeaderText, handle.Code, "Claimed by " + handle.PlayerName)).ToList();
					if (commanders.Any())
					{
						IRCConnection.SendMessage(playerModules.Any() 
							? $"Modules: {playerModules.Join(", ")}" 
							: "None of the specified modules have been solved.", userNickName, !isWhisper);
					}
					else IRCConnection.SendMessage($"Could not find any modules containing \"{trimmed}\".", userNickName, !isWhisper);
				}
			}

			if (text.RegexMatch(out match, "^((?:(?:find|search)|claim|view|all){2,4}) (.+)"))
			{
				bool validFind = match.Groups[1].Value.Contains("find") || match.Groups[1].Value.Contains("search");
				bool validClaim = match.Groups[1].Value.Contains("claim");
				if (!validFind || !validClaim) return;

				if (isWhisper)
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, claiming modules is not allowed in whispers", userNickName, false);
					return;
				}

				bool validView = match.Groups[1].Value.Contains("view");
				bool validAll = match.Groups[1].Value.Contains("all");

				string[] queries = match.Groups[2].Value.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries);
				int counter = 0;
				List<string> failedQueries = new List<string>();

				foreach (string query in queries)
				{
					if (counter == 2) break;
					string trimmed = query.Trim();
					IEnumerable<string> modules = ComponentHandles.Where(handle => handle.HeaderText.ContainsIgnoreCase(trimmed) && GameRoom.Instance.IsCurrentBomb(handle.BombID) && !handle.Solved && !handle.Claimed)
						.OrderByDescending(handle => handle.HeaderText.EqualsIgnoreCase(trimmed)).Select(handle => $"{handle.Code}").ToList();
					if (modules.Any())
					{
						if (!validAll) modules = modules.Take(1);
						foreach (string module in modules)
						{
							TwitchModule handle = ComponentHandles.FirstOrDefault(x => x.Code.Equals(module));
							if (handle == null || !GameRoom.Instance.IsCurrentBomb(handle.BombID)) continue;
							handle.AddToClaimQueue(userNickName, validView);
						}
						if (validAll) counter++;
					}
					else failedQueries.Add(trimmed);
				}
				if (failedQueries.Count > 0) IRCConnection.SendMessage($"Couldn't find any modules containing \"{failedQueries.Join("\", \"")}\".");

				return;
			}

			if (text.Equals("newbomb", StringComparison.InvariantCultureIgnoreCase) && OtherModes.ZenModeOn)
			{
				if (isWhisper)
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, the newbomb command is not allowed in whispers", userNickName, false);
					return;
				}
				Leaderboard.Instance.GetRank(userNickName, out Leaderboard.LeaderboardEntry entry);
				if (entry.SolveScore >= TwitchPlaySettings.data.MinScoreForNewbomb || UserAccess.HasAccess(userNickName, AccessLevel.Defuser, true))
				{
					OtherModes.DisableLeaderboard(true);
					TwitchPlaySettings.AddRewardBonus(-TwitchPlaySettings.GetRewardBonus());

					foreach (TwitchBombHandle handle in BombHandles.Where(x => GameRoom.Instance.IsCurrentBomb(x.BombID)))
					{
						handle.BombCommander.TimerComponent.StopTimer();
					}

					foreach (TwitchModule handle in ComponentHandles.Where(x => GameRoom.Instance.IsCurrentBomb(x.BombID)))
					{
						if (!handle.Solved) handle.SolveSilently();
					}
					return;
				}
				else
				{
					IRCConnection.SendMessage($"Sorry {userNickName}, you don't have enough points to use the newbomb command.");
					return;
				}
			}
			if (text.Equals("filledgework", StringComparison.InvariantCultureIgnoreCase) && (UserAccess.HasAccess(userNickName, AccessLevel.Mod, true) || TwitchPlaySettings.data.EnableFilledgeworkForEveryone || TwitchPlaySettings.data.AnarchyMode))
			{
				foreach (BombCommander commander in BombCommanders) commander.FillEdgework(_currentBomb != commander.TwitchBombHandle.BombID);
				return;
			}
		}

		if (text.RegexMatch(out match, "^notes(-?[0-9]+)$") && int.TryParse(match.Groups[1].Value, out index))
		{
			if (!_notesDictionary.ContainsKey(index - 1))
				_notesDictionary[index - 1] = TwitchPlaySettings.data.NotesSpaceFree;
			IRCConnection.SendMessage(string.Format(TwitchPlaySettings.data.Notes, index, _notesDictionary[index - 1]), userNickName, !isWhisper);
			return;
		}

		if ((text.Equals("enablecamerawall", StringComparison.InvariantCultureIgnoreCase) || text.Equals("enablemodulewall", StringComparison.InvariantCultureIgnoreCase)) && TwitchPlaySettings.data.AnarchyMode)
		{
			if (TwitchPlaySettings.data.EnableAutomaticCameraWall)
			{
				IRCConnection.SendChatMessage("The camera wall is being controlled automatically and cannot be enabled.");
				return;
			}

			ModuleCameras.EnableCameraWall();
		}

		if ((text.Equals("disablecamerawall", StringComparison.InvariantCultureIgnoreCase) || text.Equals("disablemodulewall", StringComparison.InvariantCultureIgnoreCase)) && TwitchPlaySettings.data.AnarchyMode)
		{
			if (TwitchPlaySettings.data.EnableAutomaticCameraWall)
			{
				IRCConnection.SendChatMessage("The camera wall is being controlled automatically and cannot be disabled.");
				return;
			}

			ModuleCameras.DisableCameraWall();
		}

		switch (UserAccess.HighestAccessLevel(userNickName))
		{
			case AccessLevel.Streamer:
			case AccessLevel.SuperUser:
				if (text.RegexMatch(out match, @"^setmultiplier ([0-9]+(?:\.[0-9]+)*)$"))
				{
					OtherModes.SetMultiplier(float.Parse(match.Groups[1].Value));
					return;
				}

				if (text.Equals("solvebomb", StringComparison.InvariantCultureIgnoreCase))
				{
					OtherModes.DisableLeaderboard();

					foreach (TwitchBombHandle handle in BombHandles.Where(x => GameRoom.Instance.IsCurrentBomb(x.BombID)))
					{
						handle.BombCommander.TimerComponent.StopTimer();
					}

					foreach (TwitchModule handle in ComponentHandles.Where(x => GameRoom.Instance.IsCurrentBomb(x.BombID)))
					{
						if (!handle.Solved) handle.SolveSilently();
					}
					return;
				}
				goto case AccessLevel.Admin;
			case AccessLevel.Admin:
				if (text.Equals("enablecamerawall", StringComparison.InvariantCultureIgnoreCase) || text.Equals("enablemodulewall", StringComparison.InvariantCultureIgnoreCase))
				{
					ModuleCameras.EnableCameraWall();
				}

				if (text.Equals("disablecamerawall", StringComparison.InvariantCultureIgnoreCase) || text.Equals("disablemodulewall", StringComparison.InvariantCultureIgnoreCase))
				{
					ModuleCameras.DisableCameraWall();
				}

				if (text.Equals("enableclaims", StringComparison.InvariantCultureIgnoreCase))
				{
					TwitchModule.ClaimsEnabled = true;
					IRCConnection.SendChatMessage("Claims have been enabled.");
				}

				if (text.Equals("disableclaims", StringComparison.InvariantCultureIgnoreCase))
				{
					TwitchModule.ClaimsEnabled = false;
					IRCConnection.SendChatMessage("Claims have been disabled.");
				}
				goto case AccessLevel.Mod;
			case AccessLevel.Mod:
				if (text.RegexMatch(out match, @"^assign (\S+) (.+)"))
				{
					string[] split = match.Groups[2].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
					foreach (string assign in split)
					{
						TwitchModule handle = ComponentHandles.FirstOrDefault(x => x.Code.Equals(assign));
						if (handle == null || !GameRoom.Instance.IsCurrentBomb(handle.BombID)) continue;
						// ReSharper disable once MustUseReturnValue
						handle.OnMessageReceived(message.Duplicate($"assign {match.Groups[1].Value}"));
					}
					return;
				}

				if (text.RegexMatch("^bot ?unclaim( ?all)?$"))
				{
					userNickName = IRCConnection.Instance.UserNickName;
					foreach (TwitchModule handle in ComponentHandles)
					{
						handle.RemoveFromClaimQueue(userNickName);
					}
					string[] moduleIDs = ComponentHandles.Where(x => !x.Solved && x.PlayerName != null && x.PlayerName.Equals(userNickName, StringComparison.InvariantCultureIgnoreCase))
						.Select(x => x.Code).ToArray();
					foreach (string claim in moduleIDs)
					{
						TwitchModule handle = ComponentHandles.FirstOrDefault(x => x.Code.Equals(claim));
						if (handle == null || !GameRoom.Instance.IsCurrentBomb(handle.BombID)) continue;
						// ReSharper disable once MustUseReturnValue
						handle.OnMessageReceived(new Message(userNickName, message.UserColorCode, "unclaim"));
					}
					return;
				}

				if (text.RegexMatch(@"^disableinteractive$"))
				{
					ModuleCameras.DisableInteractive();
					return;
				}
				break;
		}

		GameRoom.Instance.RefreshBombID(ref _currentBomb);

		if (_currentBomb > -1)
		{
			//Check for !bomb messages, and pass them off to the currently held bomb.
			if (text.RegexMatch(out match, "^bomb (.+)"))
			{
				string internalCommand = match.Groups[1].Value;
				text = $"bomb{_currentBomb + 1} {internalCommand}";
			}

			if (text.RegexMatch(out match, "^edgework$"))
			{
				text = $"edgework{_currentBomb + 1}";
			}
			else
			{
				if (text.RegexMatch(out match, "^edgework (.+)"))
				{
					string internalCommand = match.Groups[1].Value;
					text = $"edgework{_currentBomb + 1} {internalCommand}";
				}
			}
		}

		foreach (TwitchBombHandle handle in BombHandles)
		{
			if (handle == null) continue;
			IEnumerator onMessageReceived = handle.OnMessageReceived(message.Duplicate(text));
			if (onMessageReceived == null)
			{
				continue;
			}

			if (_currentBomb != handle.BombID)
			{
				if (!GameRoom.Instance.IsCurrentBomb(handle.BombID))
					continue;

				CoroutineQueue.AddToQueue(BombHandles[_currentBomb].HideMainUIWindow(), handle.BombID);
				CoroutineQueue.AddToQueue(handle.ShowMainUIWindow(), handle.BombID);
				CoroutineQueue.AddToQueue(BombCommanders[_currentBomb].LetGoBomb(), handle.BombID);

				_currentBomb = handle.BombID;
			}
			CoroutineQueue.AddToQueue(onMessageReceived, handle.BombID);
		}

		foreach (TwitchModule componentHandle in ComponentHandles)
		{
			if (!GameRoom.Instance.IsCurrentBomb(componentHandle.BombID)) continue;
			if (!text.StartsWith(componentHandle.Code + " ", StringComparison.InvariantCultureIgnoreCase)) continue;
			IEnumerator onMessageReceived = componentHandle.OnMessageReceived(message.Duplicate(text.Substring(componentHandle.Code.Length + 1)));
			if (onMessageReceived == null) continue;

			if (_currentBomb != componentHandle.BombID)
			{
				CoroutineQueue.AddToQueue(BombHandles[_currentBomb].HideMainUIWindow(), componentHandle.BombID);
				CoroutineQueue.AddToQueue(BombHandles[componentHandle.BombID].ShowMainUIWindow(), componentHandle.BombID);
				CoroutineQueue.AddToQueue(BombCommanders[_currentBomb].LetGoBomb(), componentHandle.BombID);
				_currentBomb = componentHandle.BombID;
			}
			CoroutineQueue.AddToQueue(onMessageReceived, componentHandle.BombID);
		}

		if (TwitchPlaySettings.data.BombCustomMessages.ContainsKey(text.ToLowerInvariant()))
		{
			IRCConnection.SendMessage(TwitchPlaySettings.data.BombCustomMessages[text.ToLowerInvariant()], userNickName, !isWhisper);
		}
	}

	// ReSharper disable once UnusedParameter.Local
	private void CreateBombHandleForBomb(MonoBehaviour bomb, int id)
	{
		TwitchBombHandle bombHandle = Instantiate(TwitchBombHandlePrefab);
		bombHandle.BombID = id;
		bombHandle.BombCommander = BombCommanders[BombCommanders.Count - 1];
		bombHandle.CoroutineQueue = CoroutineQueue;
		BombHandles.Add(bombHandle);
		BombCommanders[BombCommanders.Count - 1].TwitchBombHandle = bombHandle;
	}

	public bool CreateComponentHandlesForBomb(Bomb bomb)
	{
		string[] keyModules =
		{
			"SouvenirModule", "MemoryV2", "TurnTheKey", "TurnTheKeyAdvanced", "theSwan", "HexiEvilFMN", "taxReturns", "timeKeeper"
		};
		bool foundComponents = false;

		List<BombComponent> bombComponents = bomb.BombComponents.Shuffle().ToList();

		BombCommander bombCommander = BombCommanders[BombCommanders.Count - 1];

		foreach (BombComponent bombComponent in bombComponents)
		{
			ComponentTypeEnum componentType = bombComponent.ComponentType;
			bool keyModule = false;
			string moduleName;

			// ReSharper disable once SwitchStatementMissingSomeCases
			switch (componentType)
			{
				case ComponentTypeEnum.Empty:
					continue;

				case ComponentTypeEnum.Timer:
					BombCommanders[BombCommanders.Count - 1].TimerComponent = (TimerComponent) bombComponent;
					continue;

				case ComponentTypeEnum.NeedyCapacitor:
				case ComponentTypeEnum.NeedyKnob:
				case ComponentTypeEnum.NeedyVentGas:
				case ComponentTypeEnum.NeedyMod:
					moduleName = bombComponent.GetModuleDisplayName();
					keyModule = true;
					foundComponents = true;
					break;

				case ComponentTypeEnum.Mod:
					KMBombModule module = bombComponent.GetComponent<KMBombModule>();
					keyModule = keyModules.Contains(module.ModuleType);
					goto default;

				default:
					moduleName = bombComponent.GetModuleDisplayName();
					bombCommander.BombSolvableModules++;
					foundComponents = true;
					break;
			}

			if (!bombCommander.SolvedModules.ContainsKey(moduleName))
				bombCommander.SolvedModules[moduleName] = new List<TwitchModule>();

			TwitchModule handle = Instantiate(TwitchModulePrefab, bombComponent.transform, false);
			handle.BombCommander = bombCommander;
			handle.BombComponent = bombComponent;
			handle.CoroutineQueue = CoroutineQueue;
			handle.BombID = _currentBomb == -1 ? -1 : BombCommanders.Count - 1;
			handle.IsKey = keyModule;

			handle.transform.SetParent(bombComponent.transform.parent, true);
			handle.BasePosition = handle.transform.localPosition;

			ComponentHandles.Add(handle);
		}

		return foundComponents;
	}

	private IEnumerator SendDelayedMessage(float delay, string message, Action callback = null)
	{
		yield return new WaitForSeconds(delay);
		IRCConnection.SendChatMessage(message);

		callback?.Invoke();
	}

	private void SendAnalysisLink()
	{
		if (LogUploader.Instance.PostToChat()) return;
		Debug.Log("[BombMessageResponder] Analysis URL not found, instructing LogUploader to post when it's ready");
		LogUploader.Instance.postOnComplete = true;
	}
	#endregion
}
