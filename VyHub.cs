// #define TESTING

using System;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
using UnityEngine.Networking;
using Time = UnityEngine.Time;

#if RUST
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
#endif

namespace Oxide.Plugins
{
	[Info("VyHub", "VyHub", "1.3.0")]
	[Description(
		"VyHub plugin to manage and monetize your Rust / 7 Days to Die server. You can create your webstore for free with VyHub!")]
	public class VyHub : CovalencePlugin
	{
		#region Fields

		private string _serverBundleID = string.Empty;

		private const string GameType = "STEAM";

		private const string
			CMD_EDIT_CONFIG = "vh_config",
			CMD_SETUP_CONFIG = "vh_setup",
			CMD_WARN = "warn";

		private readonly List<UnityWebRequest> _activeRequests = new List<UnityWebRequest>();

		private List<string> _warnedUsers = new List<string>();
		
		#endregion

		#region Config

		private Configuration _config;

		private class Configuration
		{
			[JsonProperty("Advert Settings")]
			public AdvertSettings AdvertSettings = new AdvertSettings
			{
				Prefix = "[★] ",
				Interval = 180f
			};

			[JsonProperty("API Settings")]
			public APISettings API = new APISettings
			{
				URL = "https://api.vyhub.app/<name>/v1",
				Key = "Admin -> Settings -> Server -> Setup",
				ServerID = "Admin -> Settings -> Server -> Setup"
			};
		}

		private class AdvertSettings
		{
			[JsonProperty("Prefix")]
			public string Prefix;

			[JsonProperty("Interval")]
			public float Interval;
		}

		private class APISettings
		{
			[JsonProperty("URL")] public string URL;

			[JsonProperty("Key")] public string Key;

			[JsonProperty("Server ID")]
			public string ServerID;

			[JsonIgnore] public Dictionary<string, string> Headers;

			[JsonIgnore] public string ServerEndpoint;

			[JsonIgnore] public string FetchAdvertsEndpoint;

			[JsonIgnore] public string UserEndpoint;
			
			[JsonIgnore] public string UserActivityEndpoint;

			[JsonIgnore] public string PlaytimeDefinitionEndpoint;

			[JsonIgnore] public string PlaytimeEndpoint;

			[JsonIgnore] public string ServerBundleEndpoint;

			[JsonIgnore] public string BansEndpoint;

			[JsonIgnore] public string WarningsEndpoint;

			[JsonIgnore] public string GroupEndpoint;

			[JsonIgnore] public string UserRewardsEndpoint;

			[JsonIgnore] public string SendRewardsEndpoint;

			public void InitOrUpdate()
			{
				Headers = new Dictionary<string, string>
				{
					["Content-Type"] = "application/json",
					["Authorization"] = $"Bearer {Key}",
					["Accept"] = "application/json"
				};

				ServerEndpoint = $"{URL}/server/{ServerID}";
				FetchAdvertsEndpoint = $"{URL}/advert/?active=true";
				UserEndpoint = $"{URL}/user/";
				UserActivityEndpoint = $"{URL}/server/{ServerID}/user-activity";
				PlaytimeDefinitionEndpoint = $"{URL}/user/attribute/definition";
				PlaytimeEndpoint = $"{URL}/user/attribute";
				ServerBundleEndpoint = $"{URL}/server/bundle/";
				BansEndpoint = $"{URL}/ban/";
				WarningsEndpoint = $"{URL}/warning/";
				GroupEndpoint = $"{URL}/group/";
				UserRewardsEndpoint =
					$"{URL}/packet/reward/applied/user?active=true&foreign_ids=true&status=OPEN&for_server_id={ServerID}";
				SendRewardsEndpoint = $"{URL}/packet/reward/applied/";
			}
		}

		protected override void LoadConfig()
		{
			base.LoadConfig();
			try
			{
				_config = Config.ReadObject<Configuration>();
				if (_config == null) throw new Exception();
				SaveConfig();
			}
			catch
			{
				PrintError("Your configuration file contains an error. Using default configuration values.");
				LoadDefaultConfig();
			}
		}

		protected override void SaveConfig()
		{
			Config.WriteObject(_config);
		}

		protected override void LoadDefaultConfig()
		{
			_config = new Configuration();
		}

		#endregion

		#region Data

		#region Users

		private Dictionary<string, VyHubUser> _vyHubUsers = new Dictionary<string, VyHubUser>();

		public VyHubUser GetVyHubUser(string targetID)
		{
			VyHubUser user;
			return _vyHubUsers.TryGetValue(targetID, out user) ? user : null;
		}

		private void LoadPlayerData(string targetID, Action<VyHubUser> callback = null)
		{
			GetOrCreateUser(targetID, user => callback?.Invoke(user));
		}

		private void UnloadPlayerData(string targetID)
		{
			_vyHubUsers.Remove(targetID);
		}

		#endregion

		#region Rewards

		private void SaveExecutedRewards()
		{
			Interface.Oxide.DataFileSystem.WriteObject($"{Name}/ExecutedRewards", _executedRewards);
		}

		private void LoadExecutedRewards()
		{
			try
			{
				_executedRewards = Interface.Oxide.DataFileSystem.ReadObject<List<string>>($"{Name}/ExecutedRewards");
			}
			catch (Exception e)
			{
				PrintError(e.ToString());
			}

			if (_executedRewards == null) _executedRewards = new List<string>();
		}

		#endregion

		#endregion

		#region Hooks

		private void Init()
		{
			LoadExecutedRewards();

			LoadCachedBans();
		}

		private void OnServerInitialized()
		{
			RegisterCommands();

			_config.API.InitOrUpdate();

#if RUST
			InitColors();
#endif

			GetServerInformation(() =>
			{
				GetPlaytimeDefinition(() =>
				{
					InitFetching();

					InitAdverts();

					LoadPlayers();
				});
			});
		}

		private void Unload()
		{
			_coroutinesSendExecutedRewards.ToList().ForEach(coroutine =>
			{
				if (coroutine != null) Rust.Global.Runner.StopCoroutine(coroutine);
			});

#if RUST
			UnloadDashboardUIs();
#endif

			DisposeActiveRequests();

			StopFetching();

			SendPlayerTime(true);

			StopAdverts();

			SaveCachedBans();

			_config = null;
		}

		#region Players

		private void OnUserConnected(IPlayer player)
		{
			if (player == null) return;

			TryAddToPlayTimes(player.Id);

			GetOrCreateUser(player.Id, user =>
			{
				GetUserRewards(user.ID, rewards =>
				{
					var reward = rewards.FirstOrDefault();
					if (reward.Value != null && reward.Value.Count > 0)
						_rewards[player.Id] = reward.Value;

					ExecuteReward(new List<string>
					{
						"CONNECT",
						"SPAWN"
					}, player.Id);
				});

				SyncGroups(user);
			});
		}

		private void OnUserDisconnected(IPlayer player)
		{
			if (player == null) return;

			SendPlayerTime(player.Id, true);

			UnloadPlayerData(player.Id);

			ExecuteReward(new List<string>
			{
				"DISCONNECT"
			}, player.Id);
		}

		private void OnUserRespawned(IPlayer player)
		{
			if (player == null) return;

			ExecuteReward(new List<string>
			{
				"SPAWN"
			}, player.Id);
		}

#if RUST
		private void OnPlayerDeath(BasePlayer player, HitInfo info)
		{
			if (player == null || player.IsNpc) return;

			ExecuteReward(new List<string>
			{
				"DEATH"
			}, player.UserIDString);
		}
#endif

		#endregion

		#region Ban

		private void OnUserBanned(string name, string id, string address, string reason)
		{
			_cachedBans.Add(id);

			FetchVyHubBans(vyHubBans =>
			{
				if (!vyHubBans.ContainsKey(id))
				{
					Puts("Adding banned player to VyHub");
					CreateVyHubBanWithoutCreator(null, reason, id, DateTime.UtcNow);
				}
			});
		}

		private void OnUserUnbanned(string name, string playerID, string ipAddress)
		{
			_cachedBans.Remove(playerID);

			FetchVyHubBans(vyHubBans =>
			{
				if (vyHubBans.ContainsKey(playerID))
				{
					Puts("Removed banned player from VyHub");
					UnbanVyHubUser(playerID);
				}
			});
		}

		#endregion

		#region Permissions

		private void OnUserGroupAdded(string id, string groupName)
		{
			var user = GetVyHubUser(id);
			if (user == null) return;

			if (_groupsBackLog.Remove(GetGroupBacklogKey(id, groupName, GroupOperation.add)) == false)
				AddUserToVyHubGroup(user, groupName,
					membership =>
					{
						Log(LogType.INFO,
							$"Added VyHub group membership in group {groupName} for player {user.Username}.");
					});
		}

		private void OnUserGroupRemoved(string id, string groupName)
		{
			var user = GetVyHubUser(id);
			if (user == null) return;

			var remove = _groupsBackLog.Remove(GetGroupBacklogKey(id, groupName, GroupOperation.remove));
			if (remove == false)
				RemoveUserFromVyHubGroup(user, groupName,
					membership =>
					{
						Log(LogType.INFO,
							$"Ended VyHub group membership in group {groupName} for player {user.Username}.");
					});
		}

		#endregion

		#endregion

		#region Commands

		private readonly string
			MSG_EDIT_CONFIG_ERR_SYNTAX = $"Error syntax! Use: /{CMD_EDIT_CONFIG} <api_key/api_url/server_id> <value>",
			MSG_SETUP_CONFIG_ERR_SYNTAX = $"Error syntax! Use: /{CMD_EDIT_CONFIG} <api_key> <api_url> <server_id>",
			MSG_WARN_ERR_SYNTAX = $"Error syntax! Use: /{CMD_WARN} <steam id> <reason>";

		private void CmdEditConfig(IPlayer player, string command, string[] args)
		{
			if (!player.IsServer) return;

			if (args.Length < 2)
			{
				player.Reply(MSG_EDIT_CONFIG_ERR_SYNTAX);
				return;
			}

			var value = string.Join(" ", args.Skip(1));
			if (string.IsNullOrWhiteSpace(value))
			{
				player.Reply(MSG_EDIT_CONFIG_ERR_SYNTAX);
				return;
			}

			var param = args[0];
			switch (param)
			{
				case "api_key":
				{
					_config.API.Key = value;
					break;
				}
				case "api_url":
				{
					_config.API.URL = value;
					break;
				}
				case "server_id":
				{
					_config.API.ServerID = value;
					break;
				}
				default:
				{
					player.Reply(MSG_EDIT_CONFIG_ERR_SYNTAX);
					return;
				}
			}

			player.Reply($"You have successfully set the \"{param}\" parameter to \"{value}\"!");

			_config.API.InitOrUpdate();

			SaveConfig();

			GetServerInformation();
		}

		private void CmdSetupConfig(IPlayer player, string command, string[] args)
		{
			if (!player.IsServer) return;

			if (args.Length < 3)
			{
				player.Reply(MSG_SETUP_CONFIG_ERR_SYNTAX);
				return;
			}

			var api_key = args[0];
			var api_url = args[1];
			var server_id = args[2];
			if (string.IsNullOrWhiteSpace(api_key) || string.IsNullOrWhiteSpace(api_url) ||
			    string.IsNullOrWhiteSpace(server_id))
			{
				player.Reply(MSG_SETUP_CONFIG_ERR_SYNTAX);
				return;
			}

			_config.API = new APISettings
			{
				URL = api_url,
				Key = api_key,
				ServerID = server_id
			};

			_config.API.InitOrUpdate();

			player.Reply(
				$"You have successfully set the following API parameters in the config: URL={api_url}, KEY={api_key}, Server ID={server_id}.");

			SaveConfig();

			GetServerInformation();
		}

		private void CmdWarn(IPlayer player, string command, string[] args)
		{
			if (!player.IsServer) return;

			if (args.Length < 2)
			{
				player.Reply(MSG_WARN_ERR_SYNTAX);
				return;
			}

			var targetPlayer = covalence.Players.FindPlayerById(args[0]);
			if (targetPlayer == null || !targetPlayer.IsConnected)
			{
				player.Reply("The player must be online!");
				return;
			}

			CreateWarning(player, targetPlayer, args[1]);
		}

		#endregion

		#region Dashboard

#if RUST

		#region Fields

		[PluginReference] private Plugin ImageLibrary = null;

		private enum DashboardTab
		{
			Players = 0
		}

		private Dictionary<ulong, ConfirmData> _confirmPlayers = new Dictionary<ulong, ConfirmData>();

		private class ConfirmData
		{
			public string Reason = string.Empty;

			public string Duration = string.Empty;
		}

		private const string 
			DashboardLayer = "UI.VyHub.Dashboard",
			DashboardMainLayer = "UI.VyHub.Dashboard.Main",
			DashboardConfirmLayer = "UI.VyHub.Dashboard.Confirm",
			DashboardConfirmMainLayer = "UI.VyHub.Dashboard.Confirm.Main", 
			DashboardOpenCmd = "dashboard",
			DashboardConsoleCmd = "UI_VyHub_Dashboard",
			DASHBOARD_ACTION_BAN = "ban",
			DASHBOARD_ACTION_WARN = "warn",
			DASHBOARD_ACTION_UNBAN = "unban";

		private const int 
			DASHBOARD_TABLE_PLAYERS_CONST_X_INDENT = 205,
			DASHBOARD_TABLE_PLAYERS_ON_LINE = 2,
			DASHBOARD_TABLE_PLAYERS_MAX_LINES = 8,
			DASHBOARD_TABLE_PLAYERS_TOTAL_AMOUNT =
			DASHBOARD_TABLE_PLAYERS_ON_LINE * DASHBOARD_TABLE_PLAYERS_MAX_LINES;

		private const float 
			DASHBOARD_HEADER_BTN_WIDTH = 25,
			DASHBOARD_TAB_HEIGHT = 36,
			DASHBOARD_TAB_MARGIN = 0,
			DASHBOARD_TABLE_PLAYERS_WIDTH = 292f,
			DASHBOARD_TABLE_PLAYERS_AVATAR_SIZE = 36f,
			DASHBOARD_TABLE_PLAYERS_HEIGHT = 36f,
			DASHBOARD_TABLE_PLAYERS_X_MARGIN = 16f,
			DASHBOARD_TABLE_PLAYERS_Y_MARGIN = 8f,
			DASHBOARD_TABLE_PLAYERS_BTN_WIDTH = 65f,
			DASHBOARD_TABLE_PLAYERS_BTN_HEIGHT = 20f,
			DASHBOARD_TABLE_PLAYERS_BTN_MARGIN = 5f,
			DASHBOARD_CONFIRM_HEIGHT = 230f,
			DASHBOARD_CONFIRM_BANS_HEIGHT = 285f;

		#endregion

		#region Colors

		private string _color1;
		private string _color2;
		private string _color3;
		private string _color4;
		private string _color5;
		private string _color6;
		private string _color7;
		private string _color8;
		private string _color9;
		private string _color10;

		private void InitColors()
		{
			_color1 = HexToCuiColor("#0E0E10");
			_color2 = HexToCuiColor("#161617");
			_color3 = HexToCuiColor("#FFFFFF");
			_color4 = HexToCuiColor("#4B68FF");
			_color5 = HexToCuiColor("#4B68FF", 50);
			_color6 = HexToCuiColor("#242425");
			_color7 = HexToCuiColor("#000000");
			_color8 = HexToCuiColor("#FFFFFF", 50);
			_color9 = HexToCuiColor("#FF4B4B");
			_color10 = HexToCuiColor("#B19F56");
		}

		private static string HexToCuiColor(string HEX, float Alpha = 100)
		{
			if (string.IsNullOrEmpty(HEX)) HEX = "#FFFFFF";

			var str = HEX.Trim('#');
			if (str.Length != 6) throw new Exception(HEX);
			var r = byte.Parse(str.Substring(0, 2), NumberStyles.HexNumber);
			var g = byte.Parse(str.Substring(2, 2), NumberStyles.HexNumber);
			var b = byte.Parse(str.Substring(4, 2), NumberStyles.HexNumber);

			return $"{(double) r / 255} {(double) g / 255} {(double) b / 255} {Alpha / 100f}";
		}

		#endregion

		#region Interface

		private void DashboardUI(BasePlayer player, DashboardTab selectedTab = DashboardTab.Players, int page = 0,
			string search = "", bool first = false)
		{
			var container = new CuiElementContainer();

			float xSwitch;
			float ySwitch;
			float localSwitch;

			#region Background

			if (first)
			{
				CuiHelper.DestroyUi(player, DashboardLayer + ".Background");

				container.Add(new CuiPanel
				{
					RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
					Image =
					{
						Color = "0 0 0 0.9",
						Material = "assets/content/ui/uibackgroundblur.mat"
					},
					CursorEnabled = true
				}, "Overlay", DashboardLayer + ".Background");

				container.Add(new CuiButton
				{
					RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
					Text = {Text = ""},
					Button =
					{
						Color = "0 0 0 0",
						Close = DashboardLayer + ".Background"
					}
				}, DashboardLayer + ".Background");

				container.Add(new CuiPanel()
				{
					RectTransform =
					{
						AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
						OffsetMin = "-410 -210",
						OffsetMax = "410 210"
					},
					Image =
					{
						Color = _color1
					}
				}, DashboardLayer + ".Background", DashboardLayer);
			}

			#endregion

			#region Main

			container.Add(new CuiPanel()
			{
				RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
				Image =
				{
					Color = "0 0 0 0"
				}
			}, DashboardLayer, DashboardMainLayer);

			#endregion

			#region Header

			container.Add(new CuiPanel
			{
				RectTransform =
				{
					AnchorMin = "0 1", AnchorMax = "1 1",
					OffsetMin = "0 -50",
					OffsetMax = "0 0"
				},
				Image = {Color = _color2}
			}, DashboardMainLayer, DashboardMainLayer + ".Header");

			container.Add(new CuiLabel
			{
				RectTransform =
				{
					AnchorMin = "0 0", AnchorMax = "1 1",
					OffsetMin = "30 0",
					OffsetMax = "0 0"
				},
				Text =
				{
					Text = "Dashboard",
					Align = TextAnchor.MiddleLeft,
					Font = "robotocondensed-bold.ttf",
					FontSize = 14,
					Color = _color3
				}
			}, DashboardMainLayer + ".Header");

			xSwitch = -25f;

			#region Close

			container.Add(new CuiButton
			{
				RectTransform =
				{
					AnchorMin = "1 1", AnchorMax = "1 1",
					OffsetMin = $"{xSwitch - DASHBOARD_HEADER_BTN_WIDTH} -37.5",
					OffsetMax = $"{xSwitch} -12.5"
				},
				Text =
				{
					Text = "✕",
					Align = TextAnchor.MiddleCenter,
					Font = "robotocondensed-bold.ttf",
					FontSize = 10,
					Color = _color3
				},
				Button =
				{
					Close = DashboardLayer + ".Background",
					Color = _color4
				}
			}, DashboardMainLayer + ".Header");

			#endregion

			#endregion

			#region Tabs

			container.Add(new CuiPanel()
			{
				RectTransform =
				{
					AnchorMin = "0 0", AnchorMax = "0 1",
					OffsetMin = "15 15",
					OffsetMax = "185 -65"
				},
				Image =
				{
					Color = _color2
				}
			}, DashboardMainLayer, DashboardMainLayer + ".Tabs");

			ySwitch = 0f;

			foreach (DashboardTab targetTab in Enum.GetValues(typeof(DashboardTab)))
			{
				var isSelectedTab = targetTab == selectedTab;

				if (isSelectedTab)
				{
					container.Add(new CuiPanel()
						{
							RectTransform =
							{
								AnchorMin = "0 1", AnchorMax = "1 1",
								OffsetMin = $"0 {ySwitch - DASHBOARD_TAB_HEIGHT}",
								OffsetMax = $"0 {ySwitch}"
							},
							Image =
							{
								Color = _color5
							}
						}, DashboardMainLayer + ".Tabs", DashboardMainLayer + $".Tab.{targetTab}");

					container.Add(new CuiPanel()
						{
							RectTransform =
							{
								AnchorMin = "0 0", AnchorMax = "0 1",
								OffsetMin = "0 0",
								OffsetMax = "5 0"
							},
							Image =
							{
								Color = _color4
							}
						}, DashboardMainLayer + $".Tab.{targetTab}");

					container.Add(new CuiButton()
						{
							RectTransform =
							{
								AnchorMin = "0 0",
								AnchorMax = "1 1",
								OffsetMin = "0 0",
								OffsetMax = "0 0"
							},
							Text =
							{
								Text = $"{targetTab.ToString()}",
								Align = TextAnchor.MiddleCenter,
								Font = "robotocondensed-regular.ttf",
								Color = _color3
							},
							Button =
							{
								Color = "0 0 0 0",
								Command = $"{DashboardConsoleCmd} page {targetTab} 0 {search}"
							}
						}, DashboardMainLayer + $".Tab.{targetTab}");
				}
				else
				{
					container.Add(new CuiButton()
						{
							RectTransform =
							{
								AnchorMin = "0 1", AnchorMax = "1 1",
								OffsetMin = $"0 {ySwitch - DASHBOARD_TAB_HEIGHT}",
								OffsetMax = $"0 {ySwitch}"
							},
							Text =
							{
								Text = $"{targetTab.ToString()}",
								Align = TextAnchor.MiddleCenter,
								Font = "robotocondensed-regular.ttf"
							},
							Button =
							{
								Color = "0 0 0 0",
								Command = $"{DashboardConsoleCmd} page {targetTab} 0 {search}"
							}
						}, DashboardMainLayer + ".Tabs", DashboardMainLayer + $".Tab.{targetTab}");
				}

				ySwitch = ySwitch - DASHBOARD_TAB_HEIGHT - DASHBOARD_TAB_MARGIN;
			}

			#endregion

			#region Selected Tab

			switch (selectedTab)
			{
				case DashboardTab.Players:
					var members = GetPlayers(page, search);

					#region Header

					xSwitch = xSwitch - DASHBOARD_HEADER_BTN_WIDTH - 5f;

					#region Next Page

					container.Add(new CuiButton
					{
						RectTransform =
						{
							AnchorMin = "1 1", AnchorMax = "1 1",
							OffsetMin = $"{xSwitch - DASHBOARD_HEADER_BTN_WIDTH} -37.5",
							OffsetMax = $"{xSwitch} -12.5"
						},
						Text =
						{
							Text = "▶",
							Align = TextAnchor.MiddleCenter,
							Font = "robotocondensed-bold.ttf",
							FontSize = 10,
							Color = _color3
						},
						Button =
						{
							Color = _color6,
							Command = members.Length > (page + 1) * DASHBOARD_TABLE_PLAYERS_TOTAL_AMOUNT
								? $"{DashboardConsoleCmd} page {selectedTab} {page + 1} {search}"
								: ""
						}
					}, DashboardMainLayer + ".Header");

					#endregion

					xSwitch = xSwitch - DASHBOARD_HEADER_BTN_WIDTH - 5f;

					#region Back Page

					container.Add(new CuiButton
					{
						RectTransform =
						{
							AnchorMin = "1 1", AnchorMax = "1 1",
							OffsetMin = $"{xSwitch - DASHBOARD_HEADER_BTN_WIDTH} -37.5",
							OffsetMax = $"{xSwitch} -12.5"
						},
						Text =
						{
							Text = "◀",
							Align = TextAnchor.MiddleCenter,
							Font = "robotocondensed-bold.ttf",
							FontSize = 10,
							Color = _color3
						},
						Button =
						{
							Color = _color6,
							Command = page != 0
								? $"{DashboardConsoleCmd} page {selectedTab} {page - 1} {search}"
								: ""
						}
					}, DashboardMainLayer + ".Header");

					#endregion

					xSwitch = xSwitch - DASHBOARD_HEADER_BTN_WIDTH - 5f;

					#region Search

					container.Add(new CuiPanel()
					{
						RectTransform =
						{
							AnchorMin = "1 1", AnchorMax = "1 1",
							OffsetMin = $"{xSwitch - 140} -37.5",
							OffsetMax = $"{xSwitch} -12.5"
						},
						Image =
						{
							Color = _color7
						}
					}, DashboardMainLayer + ".Header", DashboardMainLayer + ".Header.Search");

					container.Add(new CuiElement()
					{
						Parent = DashboardMainLayer + ".Header.Search",
						Components =
						{
							new CuiInputFieldComponent()
							{
								FontSize = 12,
								Align = TextAnchor.MiddleLeft,
								Command = $"{DashboardConsoleCmd} page {selectedTab} {page}",
								Color = "1 1 1 0.95",
								CharsLimit = 150,
								Text = !string.IsNullOrWhiteSpace(search) ? $"{search}" : "Search...",
								NeedsKeyboard = true
							},
							new CuiRectTransformComponent()
							{
								AnchorMin = "0 0", AnchorMax = "1 1",
								OffsetMin = "5 0", OffsetMax = "-5 0"
							}
						}
					});

					if (HasSearch(search))
						container.Add(new CuiButton()
						{
							RectTransform =
							{
								AnchorMin = "1 0.5", AnchorMax = "1 0.5",
								OffsetMin = "-25 -12.5",
								OffsetMax = "0 12.5"
							},
							Text =
							{
								Text = "✕",
								Align = TextAnchor.MiddleCenter,
								Font = "robotocondensed-regular.ttf",
								FontSize = 10,
								Color = _color8
							},
							Button =
							{
								Color = "0 0 0 0",
								Command = $"{DashboardConsoleCmd} page {selectedTab}"
							}
						}, DashboardMainLayer + ".Header.Search");

					#endregion

					#endregion

					#region Content

					ySwitch = -70f;
					xSwitch = DASHBOARD_TABLE_PLAYERS_CONST_X_INDENT;

					for (var index = 0; index < members.Length; index++)
					{
						var member = members[index];
						var isWarned = _warnedUsers.Contains(member.UserIDString);
						var isBanned = member.IPlayer?.IsBanned == true;

						#region Background

						container.Add(new CuiPanel()
							{
								RectTransform =
								{
									AnchorMin = "0 1", AnchorMax = "0 1",
									OffsetMin = $"{xSwitch} {ySwitch - DASHBOARD_TABLE_PLAYERS_HEIGHT}",
									OffsetMax = $"{xSwitch + DASHBOARD_TABLE_PLAYERS_WIDTH} {ySwitch}"
								},
								Image =
								{
									Color = _color2
								}
							}, DashboardMainLayer, DashboardMainLayer + $".Content.Player.{index}");

						#endregion

						#region Avatar

						container.Add(new CuiElement()
						{
							Parent = DashboardMainLayer + $".Content.Player.{index}",
							Components =
							{
								new CuiRawImageComponent
								{
									Png = ImageLibrary.Call<string>("GetImage", $"avatar_{member.UserIDString}")
								},
								new CuiRectTransformComponent
								{
									AnchorMin = "0 0", AnchorMax = "0 1",
									OffsetMin = "0 0",
									OffsetMax = $"{DASHBOARD_TABLE_PLAYERS_AVATAR_SIZE} 0"
								}
							}
						});

						#endregion

						#region Name&SteamID

						container.Add(new CuiLabel()
							{
								RectTransform =
								{
									AnchorMin = "0 0.5", AnchorMax = "1 1",
									OffsetMin = $"{DASHBOARD_TABLE_PLAYERS_AVATAR_SIZE + 10} 0",
									OffsetMax = "0 0"
								},
								Text =
								{
									Text = $"{member.displayName}",
									Align = TextAnchor.LowerLeft,
									Font = "robotocondensed-bold.ttf",
									FontSize = 12,
									Color = _color3
								}
							}, DashboardMainLayer + $".Content.Player.{index}");

						container.Add(new CuiLabel()
							{
								RectTransform =
								{
									AnchorMin = "0 0", AnchorMax = "1 0.5",
									OffsetMin = $"{DASHBOARD_TABLE_PLAYERS_AVATAR_SIZE + 10} 0",
									OffsetMax = "0 0"
								},
								Text =
								{
									Text = $"{member.UserIDString}",
									Align = TextAnchor.UpperLeft,
									Font = "robotocondensed-regular.ttf",
									FontSize = 10,
									Color = _color3
								}
							}, DashboardMainLayer + $".Content.Player.{index}");

						#endregion

						#region Buttons

						localSwitch = -5;

						container.Add(new CuiButton()
							{
								RectTransform =
								{
									AnchorMin = "1 0.5", AnchorMax = "1 0.5",
									OffsetMin =
										$"{localSwitch - DASHBOARD_TABLE_PLAYERS_BTN_WIDTH} -{DASHBOARD_TABLE_PLAYERS_BTN_HEIGHT / 2}",
									OffsetMax = $"{localSwitch} {DASHBOARD_TABLE_PLAYERS_BTN_HEIGHT / 2}"
								},
								Text =
								{
									Text = isBanned ? "Unban" : "Ban",
									Align = TextAnchor.MiddleCenter,
									Font = "robotocondensed-regular.ttf",
									FontSize = 10,
									Color = _color3
								},
								Button =
								{
									Command = 
										isBanned ?
										$"{DashboardConsoleCmd} {DASHBOARD_ACTION_UNBAN} {member.UserIDString}"
										: $"{DashboardConsoleCmd} {DASHBOARD_ACTION_BAN} {member.UserIDString}",
									Color = _color9
								}
							}, DashboardMainLayer + $".Content.Player.{index}");

						localSwitch = localSwitch - DASHBOARD_TABLE_PLAYERS_BTN_WIDTH -
						              DASHBOARD_TABLE_PLAYERS_BTN_MARGIN;

						container.Add(new CuiButton()
							{
								RectTransform =
								{
									AnchorMin = "1 0.5", AnchorMax = "1 0.5",
									OffsetMin =
										$"{localSwitch - DASHBOARD_TABLE_PLAYERS_BTN_WIDTH} -{DASHBOARD_TABLE_PLAYERS_BTN_HEIGHT / 2}",
									OffsetMax = $"{localSwitch} {DASHBOARD_TABLE_PLAYERS_BTN_HEIGHT / 2}"
								},
								Text =
								{
									Text = isWarned ? "Has warning" : "Warn",
									Align = TextAnchor.MiddleCenter,
									Font = "robotocondensed-regular.ttf",
									FontSize = 10,
									Color = _color3
								},
								Button =
								{
									Command = 
										isWarned 
											? ""
										: $"{DashboardConsoleCmd} {DASHBOARD_ACTION_WARN} {member.UserIDString}",
									Color = isWarned ? _color10 : _color4
								}
							}, DashboardMainLayer + $".Content.Player.{index}");

						#endregion

						#region Table Utils

						if ((index + 1) % DASHBOARD_TABLE_PLAYERS_ON_LINE == 0)
						{
							ySwitch = ySwitch - DASHBOARD_TABLE_PLAYERS_HEIGHT - DASHBOARD_TABLE_PLAYERS_Y_MARGIN;
							xSwitch = DASHBOARD_TABLE_PLAYERS_CONST_X_INDENT;
						}
						else
						{
							xSwitch += DASHBOARD_TABLE_PLAYERS_WIDTH + DASHBOARD_TABLE_PLAYERS_X_MARGIN;
						}

						#endregion
					}

					#endregion

					break;
			}

			#endregion

			CuiHelper.DestroyUi(player, DashboardMainLayer);
			CuiHelper.AddUi(player, container);
		}

		private void ConfirmActionUI(BasePlayer player, string action, string targetName, ulong targetID)
		{
			var container = new CuiElementContainer();

			var confirm = GetOrAddConfirmData(player);

			float ySwitch;
			float height;

			#region Background

			container.Add(new CuiPanel()
			{
				RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
				Image =
				{
					Color = "0 0 0 0.9",
					Material = "assets/content/ui/uibackgroundblur.mat"
				},
				CursorEnabled = true
			}, "Overlay", DashboardConfirmLayer);

			container.Add(new CuiButton
			{
				RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
				Text = {Text = ""},
				Button =
				{
					Color = "0 0 0 0",
					Close = DashboardConfirmLayer,
					Command = $"{DashboardConsoleCmd} confirm cancel"
				}
			}, DashboardConfirmLayer);

			#endregion

			#region Main

			var mainHeight = action == DASHBOARD_ACTION_BAN ? DASHBOARD_CONFIRM_BANS_HEIGHT : DASHBOARD_CONFIRM_HEIGHT;

			container.Add(new CuiPanel()
			{
				RectTransform =
				{
					AnchorMin = "0.5 0.5",
					AnchorMax = "0.5 0.5",
					OffsetMin = $"-125 -{mainHeight / 2f}",
					OffsetMax = $"125 {mainHeight / 2f}"
				},
				Image =
				{
					Color = _color1
				}
			}, DashboardConfirmLayer, DashboardConfirmMainLayer);

			#endregion

			#region Header

			container.Add(new CuiPanel()
			{
				RectTransform =
				{
					AnchorMin = "0 1", AnchorMax = "1 1",
					OffsetMin = "0 -50", OffsetMax = "0 0"
				},
				Image =
				{
					Color = _color2
				}
			}, DashboardConfirmMainLayer, DashboardConfirmMainLayer + ".Header");

			container.Add(new CuiLabel
			{
				RectTransform =
				{
					AnchorMin = "0 0", AnchorMax = "1 1",
					OffsetMin = "20 0",
					OffsetMax = "0 0"
				},
				Text =
				{
					Text = "Confirm Action",
					Align = TextAnchor.MiddleLeft,
					Font = "robotocondensed-bold.ttf",
					FontSize = 14,
					Color = _color3
				}
			}, DashboardConfirmMainLayer + ".Header");

			container.Add(new CuiButton
			{
				RectTransform =
				{
					AnchorMin = "1 1", AnchorMax = "1 1",
					OffsetMin = "-35 -37.5",
					OffsetMax = "-10 -12.5"
				},
				Text =
				{
					Text = "✕",
					Align = TextAnchor.MiddleCenter,
					Font = "robotocondensed-bold.ttf",
					FontSize = 10,
					Color = _color3
				},
				Button =
				{
					Close = DashboardConfirmMainLayer,
					Color = _color4
				}
			}, DashboardConfirmMainLayer + ".Header");

			#endregion

			ySwitch = -60f;
			height = 50f;

			#region Message

			container.Add(new CuiPanel()
			{
				RectTransform =
				{
					AnchorMin = "0 1",
					AnchorMax = "1 1",
					OffsetMin = $"20 {ySwitch - height}",
					OffsetMax = $"-20 {ySwitch}"
				},
				Image =
				{
					Color = _color2
				}
			}, DashboardConfirmMainLayer, DashboardConfirmMainLayer + ".Message");

			container.Add(new CuiLabel()
			{
				RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
				Text =
				{
					Text = $"Do you really want to <b>{action}</b> player\n<b>{targetName}</b> (<b>{targetID}</b>)?",
					Align = TextAnchor.MiddleCenter,
					Font = "robotocondensed-regular.ttf",
					FontSize = 12,
					Color = _color3
				}
			}, DashboardConfirmMainLayer + ".Message");

			#endregion

			if (action != DASHBOARD_ACTION_UNBAN)
			{
				#region Reason

				ySwitch = ySwitch - height - 5f;

				EnterFieldUI(ref container, ref ySwitch, ref height, confirm, "reason");

				#endregion

				#region Duration

				if (action == DASHBOARD_ACTION_BAN)
				{
					ySwitch = ySwitch - height - 5f;

					EnterFieldUI(ref container, ref ySwitch, ref height, confirm, "duration");
				}

				#endregion
			}
			
			#region Buttons

			ySwitch = ySwitch - height - 10f;
			height = 40;

			container.Add(new CuiPanel
			{
				RectTransform =
				{
					AnchorMin = "0 1", AnchorMax = "1 1",
					OffsetMin = $"20 {ySwitch - height}", OffsetMax = $"-20 {ySwitch}"
				},
				Image =
				{
					Color = _color2
				}
			}, DashboardConfirmMainLayer, DashboardConfirmMainLayer + ".Buttons");

			#region Cancel

			container.Add(new CuiButton()
			{
				RectTransform =
				{
					AnchorMin = "0 0.5", AnchorMax = "0 0.5",
					OffsetMin = "15 -12.5",
					OffsetMax = "95 12.5"
				},
				Text =
				{
					Text = "Cancel",
					Align = TextAnchor.MiddleCenter,
					Font = "robotocondensed-regular.ttf",
					Color = _color3
				},
				Button =
				{
					Close = DashboardConfirmLayer,
					Color = _color4,
					Command = $"{DashboardConsoleCmd} confirm cancel"
				}
			}, DashboardConfirmMainLayer + ".Buttons");

			#endregion

			#region Accept

			container.Add(new CuiButton()
			{
				RectTransform =
				{
					AnchorMin = "1 0.5", AnchorMax = "1 0.5",
					OffsetMin = "-95 -12.5",
					OffsetMax = "-15 12.5"
				},
				Text =
				{
					Text = "Accept",
					Align = TextAnchor.MiddleCenter,
					Font = "robotocondensed-regular.ttf",
					Color = _color3
				},
				Button =
				{
					Close = DashboardConfirmLayer,
					Color = _color9,
					Command = $"{DashboardConsoleCmd} confirm {action} {targetID}"
				}
			}, DashboardConfirmMainLayer + ".Buttons");

			#endregion

			#endregion

			CuiHelper.DestroyUi(player, DashboardConfirmLayer);
			CuiHelper.AddUi(player, container);
		}

		private void EnterFieldUI(ref CuiElementContainer container, ref float ySwitch, ref float height,
			ConfirmData confirm, string type)
		{
			container.Add(new CuiPanel()
				{
					RectTransform =
					{
						AnchorMin = "0 1",
						AnchorMax = "1 1",
						OffsetMin = $"20 {ySwitch - height}",
						OffsetMax = $"-20 {ySwitch}"
					},
					Image =
					{
						Color = _color2
					}
				}, DashboardConfirmMainLayer, DashboardConfirmMainLayer + $".{type}");

			string title;
			switch (type)
			{
				case "duration":
					title = $"Enter a {type} (in seconds):";
					break;
				default:
					title = $"Enter a {type}:";
					break;
			}

			container.Add(new CuiLabel()
				{
					RectTransform =
					{
						AnchorMin = "0 1", AnchorMax = "1 1",
						OffsetMin = "0 -20",
						OffsetMax = "0 0"
					},
					Text =
					{
						Text = $"{title}",
						Align = TextAnchor.MiddleCenter,
						Font = "robotocondensed-regular.ttf",
						FontSize = 12,
						Color = _color3
					}
				}, DashboardConfirmMainLayer + $".{type}");

			container.Add(new CuiPanel()
				{
					RectTransform =
					{
						AnchorMin = "0 0", AnchorMax = "1 0",
						OffsetMin = "5 5",
						OffsetMax = "-5 30"
					},
					Image =
					{
						Color = _color1
					}
				}, DashboardConfirmMainLayer + $".{type}", DashboardConfirmMainLayer + $".{type}.Enter");

			var param = type == "reason" ? confirm.Reason : confirm.Duration;

			container.Add(new CuiElement
			{
				Parent = DashboardConfirmMainLayer + $".{type}.Enter",
				Components =
				{
					new CuiInputFieldComponent
					{
						FontSize = 10,
						Align = TextAnchor.MiddleLeft,
						Command = $"{DashboardConsoleCmd} confirm enter {type}",
						Color = "1 1 1 0.95",
						CharsLimit = 150,
						Text = !string.IsNullOrWhiteSpace(param) ? $"{param}" : string.Empty,
						NeedsKeyboard = true
					},
					new CuiRectTransformComponent
					{
						AnchorMin = "0 0", AnchorMax = "1 1",
						OffsetMin = "10 0", OffsetMax = "0 0"
					}
				}
			});
		}

		#endregion

		#region Commands

		private void CmdOpenDashboard(IPlayer cov, string command, string[] args)
		{
			if (!cov.IsAdmin) return;

			var player = cov.Object as BasePlayer;
			if (player == null) return;

			DashboardUI(player, first: true);
		}

		[ConsoleCommand(DashboardConsoleCmd)]
		private void CmdConsoleDashboard(ConsoleSystem.Arg arg)
		{
			var player = arg?.Player();
			if (player == null || !arg.IsAdmin || !arg.HasArgs()) return;

			switch (arg.Args[0])
			{
				case "page":
				{
					DashboardTab targetTab;
					if (!arg.HasArgs(2) || !Enum.TryParse(arg.Args[1], out targetTab)) return;

					var page = 0;
					if (arg.HasArgs(3))
						int.TryParse(arg.Args[2], out page);

					var search = arg.HasArgs(4) ? arg.Args[3] : string.Empty;
					if (search == "delete")
						search = string.Empty;

					DashboardUI(player, targetTab, page, search);
					break;
				}

				case DASHBOARD_ACTION_BAN:
				case DASHBOARD_ACTION_WARN:
				{
					ulong targetID;
					if (!arg.HasArgs(2) || !ulong.TryParse(arg.Args[1], out targetID)) return;

					if (player.userID == targetID)
					{
						player.ChatMessage($"You can't {arg.Args[0]} yourself!");
						return;
					}

					var targetPlayer = covalence.Players.FindPlayerById(targetID.ToString());
					if (targetPlayer == null) return;

					ConfirmActionUI(player, arg.Args[0], targetPlayer.Name, targetID);
					break;
				}

				case DASHBOARD_ACTION_UNBAN:
				{
					ulong targetID;
					if (!arg.HasArgs(2) || !ulong.TryParse(arg.Args[1], out targetID)) return;

					if (player.userID == targetID)
					{
						player.ChatMessage($"You can't {arg.Args[0]} yourself!");
						return;
					}

					var targetPlayer = covalence.Players.FindPlayerById(targetID.ToString());
					if (targetPlayer == null) return;

					ConfirmActionUI(player, arg.Args[0], targetPlayer.Name, targetID);
					break;
				}

				case "confirm":
				{
					if (!arg.HasArgs(2)) return;

					var action = arg.Args[1];
					switch (action)
					{
						case DASHBOARD_ACTION_BAN:
						case DASHBOARD_ACTION_WARN:
						{
							if (!arg.HasArgs(3)) return;

							var targetID = arg.Args[2];
							if (string.IsNullOrWhiteSpace(targetID) || !targetID.IsSteamId())
								return;

							ConfirmData confirmData;
							if (!_confirmPlayers.TryGetValue(player.userID, out confirmData))
								return;

							switch (action)
							{
								case DASHBOARD_ACTION_BAN:
								{
									var duration = default(TimeSpan);

									if (!string.IsNullOrWhiteSpace(confirmData.Duration))
									{
										var seconds = Convert.ToInt32(confirmData.Duration);
										if (seconds > 0) duration = TimeSpan.FromSeconds(seconds);
									}

									NextTick(() => server.Ban(targetID, confirmData.Reason, duration));
									break;
								}
								case DASHBOARD_ACTION_WARN:
								{
									NextTick(() => CreateWarning(player.IPlayer, covalence.Players.FindPlayerById(targetID), confirmData.Reason));
									break;
								}
							}

							_confirmPlayers.Remove(player.userID);

							DashboardUI(player);
							break;
						}

						case DASHBOARD_ACTION_UNBAN:
						{
							if (!arg.HasArgs(3)) return;

							var targetID = arg.Args[2];
							if (string.IsNullOrWhiteSpace(targetID) || !targetID.IsSteamId())
								return;

							ConfirmData confirmData;
							if (!_confirmPlayers.TryGetValue(player.userID, out confirmData))
								return;

							switch (action)
							{
								case DASHBOARD_ACTION_UNBAN:
								{
									NextTick(() => server.Unban(targetID));
									break;
								}
							}

							_confirmPlayers.Remove(player.userID);

							DashboardUI(player);
							break;
						}

						case "enter":
						{
							if (!arg.HasArgs(4)) return;

							var confirmData = GetOrAddConfirmData(player);

							var result = arg.Args[3] == "delete" ? string.Empty : string.Join(" ", arg.Args.Skip(4));

							switch (arg.Args[2])
							{
								case "reason":
								{
									confirmData.Reason = result;
									break;
								}
								case "duration":
								{
									confirmData.Duration = result;
									break;
								}
							}

							break;
						}
					}

					break;
				}
			}
		}

		#endregion

		#region Utils

		private void UnloadDashboardUIs()
		{
			for (var i = 0; i < BasePlayer.activePlayerList.Count; i++)
				UnloadDashboardUI(BasePlayer.activePlayerList[i]);
		}

		private void UnloadDashboardUI(BasePlayer player)
		{
			CuiHelper.DestroyUi(player, DashboardLayer + ".Background");
			CuiHelper.DestroyUi(player, DashboardConfirmLayer);
		}

		private ConfirmData GetOrAddConfirmData(BasePlayer player)
		{
			ConfirmData confirmData;
			if (!_confirmPlayers.TryGetValue(player.userID, out confirmData))
				_confirmPlayers.TryAdd(player.userID, confirmData = new ConfirmData());
			return confirmData;
		}

		private BasePlayer[] GetPlayers(int page, string search)
		{
			return HasSearch(search)
				? BasePlayer.activePlayerList
					.Where(p => p.UserIDString == search || p.displayName == search ||
					            p.displayName.StartsWith(search) || p.displayName.Contains(search) ||
					            (search.IsSteamId() && p.UserIDString.Contains(search)))
					.Skip(page * DASHBOARD_TABLE_PLAYERS_TOTAL_AMOUNT)
					.Take(DASHBOARD_TABLE_PLAYERS_TOTAL_AMOUNT).ToArray()
				: BasePlayer.activePlayerList
					.Skip(page * DASHBOARD_TABLE_PLAYERS_TOTAL_AMOUNT)
					.Take(DASHBOARD_TABLE_PLAYERS_TOTAL_AMOUNT).ToArray();
		}

		private bool HasSearch(string search)
		{
			return !string.IsNullOrWhiteSpace(search) && search.Length > 1;
		}

		#endregion

#endif

		#endregion

		#region API Client

		#region Server Info

		private void GetServerInformation(Action callback = null)
		{
			SendWebRequest(_config.API.ServerEndpoint, null,
				"Cannot connect to VyHub API! Please follow the installation instructions: {0}",
				"Cannot fetch serverbundle id from VyHub API! Please follow the installation instructions.",
				new Action<ServerInfo>(serverInfo =>
				{
					_serverBundleID = serverInfo.ServerBundleID;

					callback?.Invoke();

					Puts("Successfully connected to VyHub API.");
				}));
		}

		private void PatchServer(List<Dictionary<string, object>> userActivities)
		{
			WebPatchRequest(_config.API.ServerEndpoint,
				new Dictionary<string, object>
				{
					["users_max"] = covalence.Server.MaxPlayers,
					["users_current"] = covalence.Server.Players,
					["user_activities"] = userActivities,
					["is_alive"] = true
				},
				"Failed to patch server: {0}",
				"Cannot fetch serverbundle id from VyHub API! Please follow the installation instructions.",
				new Action<ServerInfo>(serverInfo => { _serverBundleID = serverInfo.ServerBundleID; }));
		}

		private class ServerInfo
		{
			[JsonProperty("serverbundle_id")]
			public string ServerBundleID;
		}

		#endregion

		#region Adverts

		private void FetchAdverts(Action<List<Advert>> callback = null)
		{
			SendWebRequest(_config.API.FetchAdvertsEndpoint + $"&serverbundle_id={_serverBundleID}", null,
				"Adverts could not be fetched from VyHub API: {0}",
				"Cannot fetch adverts from VyHub API! Please follow the installation instructions.",
				callback);
		}

		private class Advert
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("title")] public string Title;

			[JsonProperty("content")]
			public string Content;

			[JsonProperty("color")] public string Color;
		}

		#endregion

		#region User

		private void GetOrCreateUser(string playerID, Action<VyHubUser> callback = null)
		{
			var cachedUser = GetVyHubUser(playerID);
			if (cachedUser != null)
			{
				callback?.Invoke(cachedUser);
				return;
			}

			SendWebRequest(_config.API.UserEndpoint + $"{playerID}?type={GameType}", null, (code, response) =>
			{
				if (response == null || code != 200)
				{
					if (code == 404)
					{
						CreateUser(playerID, callback);
						return;
					}

					PrintError("Failed to get user from VyHub API.");
					return;
				}

				var user = GetValueFrom<VyHubUser>(response,
					"Cannot fetch VyHubUser from VyHub API! Please follow the installation instructions.");
				if (user != null) _vyHubUsers.TryAdd(playerID, user);

				callback?.Invoke(user);
			});
		}

		private void CreateUser(string playerID, Action<VyHubUser> callback = null)
		{
			SendWebRequest(_config.API.UserEndpoint, new Dictionary<string, object>
				{
					["type"] = GameType,
					["identifier"] = playerID
				},
				"Failed to create user in VyHub API: {0}",
				"Cannot fetch VyHubUser from VyHub API! Please follow the installation instructions.",
				new Action<VyHubUser>(user =>
				{
					if (user != null)
						_vyHubUsers.TryAdd(playerID, user);

					callback?.Invoke(user);
				}), RequestMethod.POST, true);
		}

		public class VyHubUser
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("type")] public string Type;

			[JsonProperty("identifier")] public string Identifier;

			[JsonProperty("registered_on")] public string RegisteredOn;

			[JsonProperty("username")] public string Username;

			[JsonProperty("avatar")] public string Avatar;

			[JsonProperty("admin")] public bool Admin;

			[JsonProperty("credit_account_id")] public string CreditAccountID;

			[JsonProperty("attributes")] public Dictionary<string, string> Attributes;

			[JsonProperty("email")] public string Email;

			[JsonProperty("email_notification")] public bool EmailNotification;

			[JsonProperty("linked_users")] public List<VyHubUser> LinkedUsers;
		}

		private void FetchWarnedUsers()
		{
			SendWebRequest(_config.API.UserActivityEndpoint, null,
				"Failed to fetch user activity in VyHub API: {0}",
				"Cannot fetch VyHubUser from VyHub API! Please follow the installation instructions.",
				new Action<List<UserActivity>>(users =>
				{
					_warnedUsers.Clear();
					
					users
						.FindAll(user => user.Warnings.Count > 0 && user.Warnings.Exists(warn => warn.Active))
						.ForEach(user => _warnedUsers.Add(user.Identifier));
				}));
		}

		private class UserActivity
		{
			[JsonProperty("id")]
			public string ID;
			
			[JsonProperty("type")]
			public string Type;
			
			[JsonProperty("identifier")]
			public string Identifier;
			
			[JsonProperty("username")]
			public string Username;
			
			[JsonProperty("avatar")]
			public string Avatar;
			
			[JsonProperty("warnings")]
			public List<Warning> Warnings;

			public class Warning
			{
				[JsonProperty("id")]
				public string ID;
				
				[JsonProperty("reason")]
				public string Reason;
				
				[JsonProperty("creator")]
				public Creator Creator;
				
				[JsonProperty("created_on")]
				public DateTime CreatedOn;
				
				[JsonProperty("active")]
				public bool Active;
				
				[JsonProperty("disabled")]
				public bool Disabled;
			}
			
			public class Creator
			{
				[JsonProperty("id")]
				public string ID;
				
				[JsonProperty("username")]
				public string Username;
				
				[JsonProperty("type")]
				public string Type;
				
				[JsonProperty("identifier")]
				public string Identifier;
				
				[JsonProperty("avatar")]
				public string Avatar;
			}
		}
		
		#endregion

		#region Playtime

		private string definitionID;

		private void GetPlaytimeDefinition(Action callback = null)
		{
			if (!string.IsNullOrEmpty(definitionID)) return;

			SendWebRequest(_config.API.PlaytimeDefinitionEndpoint + "/playtime", null, (code, response) =>
			{
				if (response == null || code != 200)
				{
					if (code == 404)
					{
						CreatePlaytimeDefinition();
						return;
					}

					PrintError("Cannot connect to VyHub API! Please follow the installation instructions.");
					return;
				}

				var definition =
					GetValueFrom<PlaytimeDefinition>(response, "Failed to get playtime definition: {0}");
				if (definition != null)
					UpdatePlaytimeDefinition(definition);

				callback?.Invoke();
			});
		}

		private void CreatePlaytimeDefinition()
		{
			SendWebRequest(_config.API.PlaytimeDefinitionEndpoint, new Dictionary<string, object>
				{
					["name"] = "playtime",
					["title"] = "Play Time",
					["unit"] = "HOURS",
					["type"] = "ACCUMULATED",
					["accumulation_interval"] = "day",
					["unspecific"] = true
				},
				"Failed to create playtime definition: {0}",
				"Failed to get playtime definition: {0}",
				new Action<PlaytimeDefinition>(UpdatePlaytimeDefinition), RequestMethod.POST);
		}

		private void UpdatePlaytimeDefinition(PlaytimeDefinition definition)
		{
			if (definition == null || string.IsNullOrEmpty(definition.ID)) return;

			definitionID = definition.ID;
		}

		private void SendPlayerTime(string playerID, double hours, Action callback = null)
		{
			SendPlayerTime(GetVyHubUser(playerID), hours, callback);
		}

		private void SendPlayerTime(VyHubUser user, double hours, Action callback = null)
		{
			if (user == null) return;

			if (hours < 0.1)
				return;

			SendWebRequest(_config.API.PlaytimeEndpoint, new Dictionary<string, object>
			{
				["definition_id"] = definitionID,
				["user_id"] = user.ID,
				["serverbundle_id"] = _serverBundleID,
				["value"] = hours
			}, "Failed to Send playtime statistic to API: {0}", callback, RequestMethod.POST);
		}

		private class PlaytimeDefinition
		{
			[JsonProperty("id")] public string ID;
		}

		#endregion

		#region Bans

		private Dictionary<string, List<BanData>> _vyHubBans = new Dictionary<string, List<BanData>>();

		private void FetchVyHubBans(Action<Dictionary<string, List<BanData>>> callback = null)
		{
			SendWebRequest(
				_config.API.ServerBundleEndpoint + $"{_serverBundleID}/ban?active=true", null,
				"Bans could not be fetched from VyHub API: {0}",
				"Cannot fetch bans data from VyHub API! Please follow the installation instructions.",
				new Action<Dictionary<string, List<BanData>>>(bans =>
				{
					_vyHubBans = bans;

					callback?.Invoke(bans);
				}));
		}

		private void CreateVyHubBanWithoutCreator(long? finalTime, string reason, string playerID, DateTime time,
			Action<BanData> callback = null)
		{
			GetOrCreateUser(playerID, user =>
			{
				SendWebRequest(_config.API.BansEndpoint,
					new Dictionary<string, object>
					{
						["length"] = finalTime,
						["reason"] = reason,
						["serverbundle_id"] = _serverBundleID,
						["user_id"] = user.ID,
						["created_on"] = time.ToString(@"yyyy-MM-dd\THH:mm:ss.fff\Z")
					},
					"Failed to create ban: {0}",
					"Cannot fetch Ban from VyHub API! Please follow the installation instructions.",
					callback, RequestMethod.POST);
			});
		}

		private void UnbanVyHubUser(string playerID, Action<BanData> callback = null)
		{
			GetOrCreateUser(playerID, user =>
			{
				WebPatchRequest(_config.API.UserEndpoint + $"{user.ID}/ban?serverbundle_id={_serverBundleID}",
					new Dictionary<string, object>(),
					"Failed to unban ban: {0}",
					"Cannot fetch ban from VyHub API! Please follow the installation instructions.",
					callback);
			});
		}

		private class BanData
		{
			[JsonProperty("length")] public long? Length;

			[JsonProperty("reason")] public string Reason;

			[JsonProperty("id")] public string ID;

			[JsonProperty("creator")] public BanUser Creator;

			[JsonProperty("user")] public BanUser User;

			[JsonProperty("serverbundle")] public BanServerbundle BanServerbundle;

			[JsonProperty("created_on")] public DateTime CreatedOn;

			[JsonProperty("status")] public string Status;

			[JsonProperty("ends_on")] public DateTime? EndsOn;

			[JsonProperty("active")] public bool Active;
		}

		private class BanUser
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("username")] public string Username;

			[JsonProperty("type")] public string Type;

			[JsonProperty("identifier")] public string Identifier;

			[JsonProperty("avatar")] public string Avatar;
		}

		private class BanServerbundle
		{
			[JsonProperty("id")] public string Id;

			[JsonProperty("name")] public string Name;

			[JsonProperty("color")] public string Color;

			[JsonProperty("icon")] public string Icon;

			[JsonProperty("sort_id")] public long? SortId;
		}

		#endregion

		#region Warnings

		private void CreateWarning(IPlayer initiator,
			IPlayer targetPlayer,
			string reason)
		{
			if (initiator == null || targetPlayer == null)
				return;

			if (!initiator.IsServer && initiator.IsConnected)
				GetOrCreateUser(initiator.Id,
					admin => CreateVyHubWarning(targetPlayer.Id, admin.ID, reason,
						(code, response) => OnWarningResponse(initiator, targetPlayer, reason, code, response)));
			else
				CreateVyHubWarningWithoutCreator(targetPlayer.Id, reason,
					(code, response) => OnWarningResponse(initiator, targetPlayer, reason, code, response));
		}
		
		private void CreateVyHubWarning(string playerID, string adminID, string reason,
			Action<int, string> callback = null)
		{
			GetOrCreateUser(playerID, user =>
			{
				SendWebRequest(_config.API.WarningsEndpoint + $"morph_user_id={adminID}",
					new Dictionary<string, object>
					{
						["reason"] = reason,
						["serverbundle_id"] = _serverBundleID,
						["user_id"] = user.ID
					},
					callback, RequestMethod.POST);
			});
		}

		private void CreateVyHubWarningWithoutCreator(string playerID, string reason,
			Action<int, string> callback = null)
		{
			GetOrCreateUser(playerID, user =>
			{
				SendWebRequest(_config.API.WarningsEndpoint,
					new Dictionary<string, object>
					{
						["reason"] = reason,
						["serverbundle_id"] = _serverBundleID,
						["user_id"] = user.ID
					},
					callback, RequestMethod.POST);
			});
		}

		private void OnWarningResponse(IPlayer initiator, IPlayer targetPlayer, string reason, int code, string response)
		{
			if (code != 200)
			{
				if (code == 403)
				{
					initiator.Reply(
						"Insufficient permissions to perform this action. Please contact your administrator for assistance.");
					return;
				}

				initiator.Reply("Unsuccessful warning. Error message: " + response);
				return;
			}

			targetPlayer.Reply($"You have received a warning: {reason}");
			targetPlayer.Reply("A warning notice has been sent to you.");

			Log(LogType.INFO, $"[WARN] Warned user {targetPlayer.Name}: {reason}");

			initiator.Reply($"Warning successfully issued to \"{targetPlayer.Name}\". Reason: \"{reason}\"");

			FetchVyHubBans(bans => SyncBans());
		}
		
		private class Warn
		{
			[JsonProperty("reason")]
			public string Reason;

			[JsonProperty("id")] public string ID;
		}

		#endregion

		#region Groups

		private void FetchGroups(Action<List<VyHubGroup>> callback = null)
		{
			SendWebRequest(_config.API.GroupEndpoint, null,
				"Failed to fetch groups from VyHub API: {0}",
				"Cannot fetch groups from VyHub API! Please follow the installation instructions.",
				callback);
		}

		private void GetUserGroups(VyHubUser user, Action<List<VyHubGroup>> callback = null)
		{
			if (user == null)
				return;

			SendWebRequest(_config.API.UserEndpoint + $"{user.ID}/group?serverbundle_id={_serverBundleID}", null,
				"Failed to fetch memberships from VyHub API: {0}",
				"Cannot fetch user groups from VyHub API! Please follow the installation instructions.",
				callback);
		}

		private void AddUserToVyHubGroup(VyHubUser user, string groupName, Action<Membership> callback = null)
		{
			VyHubGroup group;
			if (!_vyHubGroups.TryGetValue(groupName, out group))
			{
				Log(LogType.WARNING, $"Could not find group mapping for '{groupName}'.");
				return;
			}

			SendWebRequest(_config.API.UserEndpoint + $"{user.ID}/membership", new Dictionary<string, object>
				{
					["group_id"] = group.ID,
					["serverbundle_id"] = _serverBundleID
				},
				"Could not add user to group: {0}",
				"Could not add user to group at VyHub API! Please follow the installation instructions.",
				callback, RequestMethod.POST);
		}

		private void RemoveUserFromVyHubGroup(VyHubUser user, string groupName, Action<Membership> callback = null)
		{
			VyHubGroup group;
			if (!_vyHubGroups.TryGetValue(groupName, out group))
			{
				Log(LogType.WARNING, $"Could not find group mapping for '{groupName}'.");
				return;
			}

			SendWebRequest(
				_config.API.UserEndpoint +
				$"{user.ID}/membership/by-group?group_id={group.ID}&serverbundle_id={_serverBundleID}",
				null,
				"Could not remove user from groups: {0}",
				"Could not remove user from groups at VyHub API! Please follow the installation instructions.",
				callback, RequestMethod.DELETE);
		}

		private void RemoveUserFromAllVyHubGroups(VyHubUser user, Action<Membership> callback = null)
		{
			SendWebRequest(_config.API.UserEndpoint + $"{user.ID}/membership?serverbundle_id={_serverBundleID}",
				null,
				"Could not remove user from all VyHub groups: {0}",
				"Could not remove user from all VyHub groups from VyHub API! Please follow the installation instructions.",
				callback, RequestMethod.DELETE);
		}

		private enum GroupOperation
		{
			add,
			remove
		}

		private class VyHubGroup
		{
			[JsonProperty("id")] public string ID;
			[JsonProperty("name")] public string Name;

			[JsonProperty("permission_level")]
			public int PermissionLevel;

			[JsonProperty("color")] public string Color;

			[JsonProperty("properties", ObjectCreationHandling = ObjectCreationHandling.Replace)]
			public Dictionary<string, GroupProperty> Properties;

			[JsonProperty("is_team")]
			public bool IsTeam;

			[JsonProperty("mappings", ObjectCreationHandling = ObjectCreationHandling.Replace)]
			public List<GroupMapping> Mappings;
		}

		private class GroupProperty
		{
			[JsonProperty("name")] public string Name;

			[JsonProperty("granted")]
			public bool Granted;

			[JsonProperty("value")] public string Value;
		}

		private class GroupMapping
		{
			[JsonProperty("name")] public string Name;

			[JsonProperty("serverbundle_id")]
			public string ServerBundleID;

			[JsonProperty("id")] public string ID;

			[JsonProperty("group_id")]
			public string GroupID;
		}

		private class Membership
		{
			[JsonProperty("id")] public string ID;
		}

		#endregion

		#region Rewards

		private void FetchRewards(VyHubUser[] onlinePlayers)
		{
#if TESTING
			SayDebug("[FetchRewards] init");
#endif

			var users = string.Join("&", onlinePlayers.Select(player => $"user_id={player.ID}"));
			if (string.IsNullOrEmpty(users)) return;

#if TESTING
			SayDebug($"[FetchRewards] users={users}");
#endif

			SendWebRequest(
				_config.API.UserRewardsEndpoint + $"&serverbundle_id={_serverBundleID}&user_ids={users}",
				null,
				"Failed to get rewards from API: {0}",
				"Cannot fetch rewards from VyHub API! Please follow the installation instructions.",
				new Action<Dictionary<string, List<AppliedReward>>>(rewards =>
				{
					_rewards = rewards;

					RunDirectRewards(rewards);
				}));
		}

		private void GetUserRewards(string userID, Action<Dictionary<string, List<AppliedReward>>> callback = null)
		{
			SendWebRequest(_config.API.UserRewardsEndpoint + $"&serverbundle_id={_serverBundleID}&user_id={userID}",
				null,
				"Failed to get rewards from API: {0}",
				"Cannot fetch rewards from VyHub API! Please follow the installation instructions.",
				callback);
		}

		private void SendExecutedReward(string rewardID, Action<AppliedReward> callback = null)
		{
			WebPatchRequest(_config.API.SendRewardsEndpoint + rewardID,
				new Dictionary<string, object>(),
				"Failed to send executed rewards to API: {0}",
				"Cannot fetch executed reward from VyHub API! Please follow the installation instructions.",
				callback);
		}

		private class AppliedReward
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("active")]
			public string Active;

			[JsonProperty("reward")]
			public Reward Reward;

			[JsonProperty("user")] public User User;

			[JsonProperty("applied_packet_id")]
			public string AppliedPacketID;

			[JsonProperty("applied_packet")]
			public AppliedPacket AppliedPacket;

			[JsonProperty("status")]
			public string Status;

			[JsonProperty("executed_on", ObjectCreationHandling = ObjectCreationHandling.Replace)]
			public List<string> ExecutedOn;
		}

		private class Reward
		{
			[JsonProperty("name")] public string Name;
			[JsonProperty("type")] public string Type;

			[JsonProperty("data", ObjectCreationHandling = ObjectCreationHandling.Replace)]
			public Dictionary<string, string> Data;

			[JsonProperty("order")] public int Order;
			[JsonProperty("once")] public bool Once;

			[JsonProperty("once_from_all")]
			public bool OnceFromAll;

			[JsonProperty("on_event")]
			public string OnEvent;

			[JsonProperty("id")] public string ID;

			[JsonProperty("serverbundle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
			public ServerBundle ServerBundle;
		}

		private class ServerBundle
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("name")] public string Name;

			[JsonProperty("color")] public string Color;

			[JsonProperty("icon")] public object Icon;

			[JsonProperty("sort_id")]
			public int SortID;
		}

		private class AppliedPacket
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("purchase")]
			public Purchase Purchase;

			[JsonProperty("packet")]
			public Packet Packet;
		}

		private class Purchase
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("amount_text")]
			public string AmountText;
		}

		private class Packet
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("title")] public string Title;
		}

		private class User
		{
			[JsonProperty("id")] public string ID;

			[JsonProperty("username")]
			public string Username;

			[JsonProperty("type")] public string Type;

			[JsonProperty("identifier")]
			public string Identifier;

			[JsonProperty("avatar")]
			public string Avatar;
		}

		#endregion

		#region Utils

		private void SendWebRequest(string endpoint, Dictionary<string, object> body, string errOnResponse,
			Action callback = null, RequestMethod method = RequestMethod.GET)
		{
			SendWebRequest(endpoint, body, (code, response) =>
			{
				if (IsBadResponse(response, code, errOnResponse))
					return;

				callback?.Invoke();
			}, method);
		}

		private void SendWebRequest<T>(string endpoint, Dictionary<string, object> body,
			string errOnResponse,
			string errOnValue,
			Action<T> callback = null,
			RequestMethod method = RequestMethod.GET, bool ignoreCheck = false)
		{
			SendWebRequest(endpoint, body, (code, response) =>
			{
				if (IsBadResponse(response, code, errOnResponse))
					return;

				var val = GetValueFrom<T>(response, errOnValue);
				if (val != null || ignoreCheck)
					callback?.Invoke(val);
			}, method);
		}

		private void SendWebRequest(string endpoint, Dictionary<string, object> body, Action<int, string> callback,
			RequestMethod method = RequestMethod.GET)
		{
			webrequest.Enqueue(endpoint, body != null ? JsonConvert.SerializeObject(body) : null, (code,
					result) =>
				{
					callback?.Invoke(code, result);
				}, this,
				method, _config.API.Headers);
		}

		private bool IsBadResponse(string response, int code, string msg)
		{
			if (code != 200 || response == null)
			{
				PrintError(msg, response);

				Log(LogType.ERROR, string.Format(msg, response));
				return true;
			}

			return false;
		}

		private T GetValueFrom<T>(string response, string errorMsg)
		{
			var obj = JsonConvert.DeserializeObject<T>(response);
			if (obj == null)
				PrintError(errorMsg);

			return obj;
		}

		private void WebPatchRequest<T>(string url,
			Dictionary<string, object> body,
			string errOnResponse,
			string errOnValue,
			Action<T> callback = null)
		{
			Rust.Global.Runner.StartCoroutine(WebPatchRequestAsync(url,
				body,
				_config.API.Headers,
				err => PrintError(errOnResponse, err),
				result =>
				{
					var val = GetValueFrom<T>(result, errOnValue);
					if (val != null)
						callback?.Invoke(val);
				}));
		}

		private IEnumerator WebPatchRequestAsync(string url,
			Dictionary<string, object> requestBody,
			Dictionary<string, string> headers,
			Action<string> onDeleteRequestError,
			Action<string> onDeleteRequestSuccess)
		{
			var webRequest = UnityWebRequest.Put(url, JsonConvert.SerializeObject(requestBody));
			webRequest.method = "PATCH";

			_activeRequests.Add(webRequest);

			foreach (var check in headers)
				webRequest.SetRequestHeader(check.Key, check.Value);

			yield return webRequest.SendWebRequest();

			if (webRequest.isNetworkError || webRequest.isHttpError)
			{
				onDeleteRequestError?.Invoke(webRequest.error);
			}
			else
			{
				if (webRequest.isDone) onDeleteRequestSuccess?.Invoke(webRequest.downloadHandler.text);
			}

			_activeRequests.Remove(webRequest);
		}

		private void DisposeActiveRequests()
		{
			foreach (var www in _activeRequests)
				try
				{
					www.Dispose();
				}
				catch
				{
					// ignored
				}
		}

		#endregion

		#endregion

		#region Utils

		#region Players

		private void LoadPlayers()
		{
			foreach (var player in covalence.Players.Connected)
				OnUserConnected(player);
		}

		private IEnumerable<VyHubUser> GetOnlinePlayers
		{
			get
			{
				foreach (var player in covalence.Players.Connected)
				{
					var user = GetVyHubUser(player.Id);
					if (user != null)
						yield return user;
				}
			}
		}

		#endregion

		#region Fetching

		private bool _enabledFetching;

		private Coroutine _coroutineFetching;

		private void InitFetching()
		{
			_enabledFetching = true;

			_coroutineFetching = Rust.Global.Runner.StartCoroutine(HandleFetching());
		}

		private void StopFetching()
		{
			_enabledFetching = false;

			if (_coroutineFetching != null)
				Rust.Global.Runner.StopCoroutine(_coroutineFetching);
		}

		private IEnumerator HandleFetching()
		{
			while (_enabledFetching)
			{
				var onlinePlayers = GetOnlinePlayers.ToArray();

				FetchWarnedUsers();
				
				FetchRewards(onlinePlayers);

				FetchVyHubBans(bans => SyncBans());

				FetchGroups(groups =>
				{
					UpdateGroups(groups);

					SyncGroupsForAll(onlinePlayers);
				});

				FetchAdverts(averts => _adverts = averts);

				PatchServer(onlinePlayers);

				SendPlayerTime();

				yield return CoroutineEx.waitForSeconds(30);
			}
		}

		#region Utils

		private void PatchServer(VyHubUser[] onlinePlayers)
		{
			var userActivities = new List<Dictionary<string, object>>();

			foreach (var user in onlinePlayers)
			{
				var dict = new Dictionary<string, object>();
				var extra = new Dictionary<string, string>();

				dict.Add("user_id", user.ID);
				dict.Add("extra", extra);

				userActivities.Add(dict);
			}

			PatchServer(userActivities);
		}

		#endregion

		#endregion

		#region Adverts

		private List<Advert> _adverts = new List<Advert>();

		private Timer _timerAdverts;

		private int _currentAdvert;

		private void InitAdverts()
		{
			_timerAdverts = timer.Every(_config.AdvertSettings.Interval, NextAdvert);
		}

		private void StopAdverts()
		{
			_timerAdverts?.Destroy();
		}

		private void NextAdvert()
		{
			if (_adverts.Count < 1) return;

			if (_currentAdvert >= _adverts.Count || _adverts[_currentAdvert] == null)
				_currentAdvert = 0;

			var advert = _adverts[_currentAdvert];
			ShowAdvert(advert);

			if (_currentAdvert + 1 == _adverts.Count)
				_currentAdvert = 0;
			else
				_currentAdvert++;
		}

		private void ShowAdvert(Advert advert)
		{
			var prefix = $"<color=blue>{_config.AdvertSettings.Prefix}</color>";

			var color = advert.Color;
			if (string.IsNullOrEmpty(color))
				color = "white";

			var message = $"{prefix}<color={color}>{advert.Content}</color>";

			server.Broadcast(message);
		}

		#endregion

		#region Playtime

		private Dictionary<string, float> _dataPlayTimes = new Dictionary<string, float>();

		private float _lastSendPlayerTime;

		private const float _cooldownPlayerTime = 3600; //hour

		private float GetPlayTimes(string playerID)
		{
			float time;
			return _dataPlayTimes.TryGetValue(playerID, out time) ? time : 0;
		}

		private void ResetTimes(string playerID)
		{
			if (_dataPlayTimes.ContainsKey(playerID)) _dataPlayTimes[playerID] = Time.time;
		}

		private void TryAddToPlayTimes(string playerID)
		{
			_dataPlayTimes.TryAdd(playerID, Time.time);
		}

		private void SendPlayerTime(bool fast = false)
		{
			#region Cooldown

			if (fast == false && _lastSendPlayerTime > 0f &&
			    _lastSendPlayerTime + _cooldownPlayerTime >= Time.time) return;

			_lastSendPlayerTime = Time.time;

			#endregion

			Log(LogType.INFO, "Sending playertime to API");

			if (!string.IsNullOrEmpty(definitionID))
				Array.ForEach(_dataPlayTimes.ToArray(), check =>
				{
					var hours = TimeSpan.FromSeconds(Time.time - check.Value).TotalHours;

					SendPlayerTime(check.Key.ToString(), hours, () => ResetTimes(check.Key));
				});
		}

		private void SendPlayerTime(string playerID, bool delete = false)
		{
			float time;
			if (!_dataPlayTimes.TryGetValue(playerID, out time))
				return;

			var hours = TimeSpan.FromSeconds(Time.time - time).TotalHours;

			SendPlayerTime(GetVyHubUser(playerID), hours, () =>
			{
				if (delete)
					_dataPlayTimes.Remove(playerID);
				else
					ResetTimes(playerID);
			});
		}

		#endregion

		#region Bans

		private HashSet<string> _cachedBans = new HashSet<string>();

		private void SyncBans()
		{
			var serverBans = GetServerBans();
			var fetchedVyHubBans = _vyHubBans;
			var vyHubBans = fetchedVyHubBans.Keys;

			// All game bans, that do not exist on VyHub
			var bannedGamePlayersDiff = new HashSet<string>(serverBans);
			bannedGamePlayersDiff.RemoveWhere(x => vyHubBans.Contains(x));

			// All VyHub bans, that do not exist on game server
			var bannedVyHubPlayersDiff = new HashSet<string>(vyHubBans);
			bannedVyHubPlayersDiff.RemoveWhere(x => serverBans.Contains(x));

			// All bans that game server and VyHub have in common
			var bannedPlayersIntersect = new HashSet<string>(vyHubBans);
			bannedPlayersIntersect.IntersectWith(serverBans);

			// Check for bans missing on VyHub
			foreach (var playerID in bannedGamePlayersDiff)
				if (_cachedBans.Contains(playerID))
				{
					// Unbanned on VyHub
					Puts($"Unbanning game ban for player {playerID}. (Unbanned on VyHub)");

					if (UnbanGameBan(playerID)) _cachedBans.Remove(playerID);
				}
				else
				{
					// Missing on VyHub
					Puts($"Adding VyHub ban for player {playerID} from game. (Banned on game server)");

					AddVyHubBan(playerID, ban => _cachedBans.Add(playerID));
				}

			// Checks for bans missing on game server
			foreach (var playerID in bannedVyHubPlayersDiff)
				if (_cachedBans.Contains(playerID))
				{
					// Unbanned on Game Server
					Puts($"Unbanning VyHub ban for player {playerID}. (Unbanned on game server)");

					UnbanVyHubUser(playerID, result => _cachedBans.Remove(playerID));
				}
				else
				{
					// Missing on Game Server
					Puts($"Adding game ban for player {playerID} from VyHub. (Banned on VyHub)");

					List<BanData> bans;
					if (fetchedVyHubBans.TryGetValue(playerID, out bans) && AddGameBan(playerID, bans[0]))
						_cachedBans.Add(playerID);
				}

			foreach (var playerID in bannedPlayersIntersect)
				_cachedBans.Add(playerID);
		}

		private void SaveCachedBans()
		{
			Interface.Oxide.DataFileSystem.WriteObject($"{Name}/CachedBans", _cachedBans);
		}

		private void LoadCachedBans()
		{
			try
			{
				_cachedBans = Interface.Oxide.DataFileSystem.ReadObject<HashSet<string>>($"{Name}/CachedBans");
			}
			catch (Exception e)
			{
				PrintError(e.ToString());
			}

			if (_cachedBans == null) _cachedBans = new HashSet<string>();
		}

		private List<string> GetServerBans()
		{
			var list = new List<string>();

			foreach (var player in covalence.Players.All)
				if (player.IsBanned)
					list.Add(player.Id);

			return list;
		}

		private bool AddGameBan(string playerID, BanData ban)
		{
			var player = covalence.Players.FindPlayerById(playerID);
			if (player == null) return false;

			if (ban.EndsOn != null)
				player.Ban(ban.Reason, ban.EndsOn?.ToUniversalTime().Subtract(DateTime.UtcNow) ?? default(TimeSpan));
			else
				player.Ban(ban.Reason);

			return true;
		}

		private bool UnbanGameBan(string playerID)
		{
			var player = covalence.Players.FindPlayerById(playerID);
			if (player == null) return false;

			player.Unban();
			return true;
		}

		private void AddVyHubBan(string playerID, Action<BanData> callback = null)
		{
			var player = covalence.Players.FindPlayerById(playerID);
			if (player == null) return;

			GetOrCreateUser(playerID, user =>
			{
				var serverUser = ServerUsers.Get(Convert.ToUInt64(playerID));
				if (serverUser == null) return;

				CreateVyHubBanWithoutCreator((long) Mathf.Max(serverUser.expiry, 0), serverUser.notes, playerID,
					DateTime.UtcNow, callback);
			});
		}

		#endregion

		#region Groups

		private Dictionary<string, VyHubGroup> _vyHubGroups = new Dictionary<string, VyHubGroup>();

		private HashSet<string> _groupsBackLog = new HashSet<string>();

		private void UpdateGroups(List<VyHubGroup> groups)
		{
			var newMappedGroups = new Dictionary<string, VyHubGroup>();

			foreach (var group in groups)
			foreach (var mapping in group.Mappings)
				if (string.IsNullOrEmpty(mapping.ServerBundleID) ||
				    mapping.ServerBundleID == _serverBundleID)
					newMappedGroups.TryAdd(mapping.Name.ToLower(), group);

			_vyHubGroups = newMappedGroups;
		}

		private void SyncGroups(VyHubUser user)
		{
			GetUserGroups(user, userGroups =>
			{
				var allGroups = new HashSet<string>();

				foreach (var group in userGroups)
				foreach (var mapping in group.Mappings)
				{
					allGroups.Add(mapping.Name.ToLower());

					if (!permission.GroupExists(group.Name))
						permission.CreateGroup(mapping.Name.ToLower(), mapping.Name, group.PermissionLevel);
				}

				AddPlayerToGameGroup(allGroups, user.Identifier);

				allGroups.Clear();
			});
		}

		private void GetUserGroups(string playerID, Action<List<VyHubGroup>> callback = null)
		{
			GetUserGroups(GetVyHubUser(playerID), callback);
		}

		private void AddPlayerToGameGroup(HashSet<string> groups, string playerID)
		{
			foreach (var group in groups)
				if (permission.GroupExists(group) && !permission.UserHasGroup(playerID, group))
				{
					permission.AddUserGroup(playerID, group);

					_groupsBackLog.Add(GetGroupBacklogKey(playerID, group, GroupOperation.add));
				}

			foreach (var group in permission.GetUserGroups(playerID))
				if (!groups.Contains(group) && _vyHubGroups.ContainsKey(group))
				{
#if TESTING
					SayDebug($"[AddPlayerToGameGroup] group to remove: {group}");
#endif
					permission.RemoveUserGroup(playerID, group);
				}
		}

		private string GetGroupBacklogKey(string playerID, string groupName, GroupOperation operation)
		{
			return $"{playerID}_{groupName}_{operation}";
		}

		private void SyncGroupsForAll(VyHubUser[] onlinePlayers)
		{
			foreach (var user in onlinePlayers)
				SyncGroups(user);
		}

		#endregion

		#region Rewards

		private Dictionary<string, List<AppliedReward>> _rewards = new Dictionary<string, List<AppliedReward>>();

		private List<string> _executedRewards = new List<string>();

		private List<string> _executedAndSentRewards = new List<string>();

		private List<Coroutine> _coroutinesSendExecutedRewards = new List<Coroutine>();

		private void ExecuteReward(List<string> events, string playerID)
		{
			if (events == null) return;

			Dictionary<string, List<AppliedReward>> rewardsByPlayer;

			if (string.IsNullOrEmpty(playerID))
			{
				foreach (var act in events)
					if (!act.Contains("DIRECT") && !act.Contains("DISABLE"))
						return;

				rewardsByPlayer = new Dictionary<string, List<AppliedReward>>(_rewards);
			}
			else
			{
				rewardsByPlayer = new Dictionary<string, List<AppliedReward>>();

				List<AppliedReward> playerRewards;
				if (_rewards.TryGetValue(playerID, out playerRewards))
					rewardsByPlayer.TryAdd(playerID, playerRewards);
				else
					return;
			}

			foreach (var check in rewardsByPlayer)
			{
				var userID = check.Key;

				var player = covalence.Players.FindPlayerById(userID);
				if (player == null) continue;

				var appliedRewards = check.Value;
				foreach (var appliedReward in appliedRewards)
				{
					if (_executedRewards.Contains(appliedReward.ID) ||
					    _executedAndSentRewards.Contains(appliedReward.ID))
						continue;

					var reward = appliedReward.Reward;

					if (events.Contains(reward.OnEvent))
					{
						var data = reward.Data;
						var success = true;

						if (reward.Type == "COMMAND")
						{
							var command = data["command"]?.Replace("\n", "|");

							command = ReplaceStrings(command, player, appliedReward);

							if (!string.IsNullOrEmpty(command))
								foreach (var cmd in command.Split('|'))
									server.Command(cmd);
						}
						else
						{
							success = false;

							Log(LogType.WARNING, $"No implementation for Reward Type: {reward.Type}");
						}

						if (reward.Once) SetExecutedReward(appliedReward.ID);

						if (success)
							Log(LogType.INFO,
								$"RewardName: {reward.Name}, Type: {reward.Type}, Player: {player.Name} ({player.Id}) executed!");
					}
				}
			}

			SendExecutedRewards();
		}

		private void RunDirectRewards(Dictionary<string, List<AppliedReward>> rewards)
		{
			if (rewards == null || rewards.Count == 0) return;

			ExecuteReward(new List<string>
			{
				"DIRECT",
				"DISABLE"
			}, null);
		}

		private void SetExecutedReward(string rewardID)
		{
			_executedRewards.Add(rewardID);

			SaveExecutedRewards();
		}

		private void SendExecutedRewards()
		{
			_coroutinesSendExecutedRewards.Add(Rust.Global.Runner.StartCoroutine(AsyncExecutedRewards()));
		}

		private IEnumerator AsyncExecutedRewards()
		{
			var rewards = _executedRewards.ToArray();

			foreach (var reward in rewards)
			{
				SendExecutedReward(reward, appliedReward =>
				{
					_executedAndSentRewards.Add(appliedReward.ID);
					_executedRewards.Remove(appliedReward.ID);

					SaveExecutedRewards();
				});

				yield return CoroutineEx.waitForFixedUpdate;
			}
		}

		private static string ReplaceStrings(string command, IPlayer player, AppliedReward appliedReward)
		{
			var newString = command;

			newString = newString.Replace("%nick%", player.Name);
			newString = newString.Replace("%user_id%", player.Id);
			newString = newString.Replace("%applied_packet_id%", appliedReward.AppliedPacketID);
			newString = newString.Replace("%player_id%", player.Id);
			newString = newString.Replace("%steamid64%", player.Id);

			var purchaseAmount = "-";

			if (appliedReward.AppliedPacket.Purchase != null)
				purchaseAmount = appliedReward.AppliedPacket.Purchase.AmountText;

			newString = newString.Replace("%purchase_amount%", purchaseAmount);
			newString = newString.Replace("%packet_title%", appliedReward.AppliedPacket.Packet.Title);

			return newString;
		}

		#endregion

		private void RegisterCommands()
		{
			AddCovalenceCommand(CMD_EDIT_CONFIG, nameof(CmdEditConfig));

			AddCovalenceCommand(CMD_SETUP_CONFIG, nameof(CmdSetupConfig));

			AddCovalenceCommand(CMD_WARN, nameof(CmdWarn));

#if RUST
			AddCovalenceCommand(DashboardOpenCmd, nameof(CmdOpenDashboard));
#endif
		}

		#endregion

		#region Logs

		private enum LogType
		{
			NONE,
			INFO,
			ERROR,
			WARNING
		}

		private void Log(LogType type, string message)
		{
#if TESTING
			Puts($"[Log.{type}] {message}");
#endif

			LogToFile($"{type}", $"[{DateTime.Now:hh:mm:ss}] {message}", this);
		}

		#endregion

		#region Testing Functions

#if TESTING
		private static void SayDebug(string message)
		{
			Debug.Log($"[TESTING] {message}");
		}
#endif

		#endregion
	}
}