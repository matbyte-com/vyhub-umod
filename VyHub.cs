	// #define TESTING

	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Linq;
	using ConVar;
	using JetBrains.Annotations;
	using Newtonsoft.Json;
	using Oxide.Core;
	using Oxide.Core.Libraries;
	using Oxide.Core.Libraries.Covalence;
	using UnityEngine;
	using Time = UnityEngine.Time;

	namespace Oxide.Plugins
	{
		[Info("VyHub", "VyHub", "1.0.0")]
		[Description(
			"VyHub plugin to manage and monetize your Rust / 7 Days to Die server. You can create your webstore for free with VyHub!")]
		public class VyHub : CovalencePlugin
		{
			#region Fields

			private string _serverBundleID = string.Empty;

			private const string GameType = "STEAM";
			
			#endregion

			#region Config

			private Configuration _config;

			private class Configuration
			{
				[JsonProperty(PropertyName = "Advert Settings")]
				public AdvertSettings AdvertSettings = new AdvertSettings
				{
					Prefix = "[★] ",
					Interval = 180f
				};

				[JsonProperty(PropertyName = "API Settings")]
				public APISettings API = new APISettings
				{
					URL = "https://api.vyhub.app/<name>/v1",
					Key = "Admin -> Settings -> Server -> Setup",
					ServerID = "Admin -> Settings -> Server -> Setup"
				};
			}

			private class AdvertSettings
			{			
				[JsonProperty(PropertyName = "Prefix")]
				public string Prefix;
				
				[JsonProperty(PropertyName = "Interval")]
				public float Interval;
			}
			
			private class APISettings
			{
				[JsonProperty(PropertyName = "URL")] public string URL;

				[JsonProperty(PropertyName = "Key")] public string Key;

				[JsonProperty(PropertyName = "Server ID")]
				public string ServerID;

				public Dictionary<string, string> GetHeaders()
				{
					return new Dictionary<string, string>
					{
						["Content-Type"] = "application/json",
						["Authorization"] = $"Bearer {Key}",
						["Accept"] = "application/json"
					};
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
				GetUser(targetID, user =>
				{
					_vyHubUsers.TryAdd(targetID, user);

					callback?.Invoke(user);
				});
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
			}

			private void OnServerInitialized()
			{
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
					if (coroutine != null)
					{
						ServerMgr.Instance.StopCoroutine(coroutine);
					}
				});
				
				StopFetching();

				SendPlayerTime(true);
				
				StopAdverts();
				
				_config = null;
			}

			#region Players

			private void OnUserConnected(IPlayer player)
			{
				if (player == null) return;

				TryAddToPlayTimes(player.Id);
				
				LoadPlayerData(player.Id, user =>
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
				FetchVyHubBans(vyHubBans =>
				{
					if (!vyHubBans.ContainsKey(id))
					{
						Puts("Adding banned player to VyHub");
						CreateVyHubBanWithoutCreator(null, reason, id, DateTime.UtcNow);
					}
				});
			}

			private void OnUserUnbanned(string name, string id, string ipAddress)
			{
				FetchVyHubBans(vyHubBans =>
				{
					if (vyHubBans.ContainsKey(id))
					{
						Puts("Removed banned player from VyHub");
						UnbanVyHubUser(id);
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
				{
					AddUserToVyHubGroup(user, groupName);
				}
			}
			
			private void OnUserGroupRemoved(string id, string groupName)
			{
				var user = GetVyHubUser(id);
				if (user == null) return;

				var remove = _groupsBackLog.Remove(GetGroupBacklogKey(id, groupName, GroupOperation.remove));
				if (remove == false)
				{
					RemoveUserFromVyHubGroup(user, groupName);
				}
			}

			#endregion
			
			#endregion

			#region API Client

			#region Server Info

			private void GetServerInformation(Action callback = null)
			{
				webrequest.Enqueue($"{_config.API.URL}/server/{_config.API.ServerID}", null, (code, response) =>
				{
					if (response == null || code != 200)
					{
						PrintError("Cannot connect to VyHub API! Please follow the installation instructions.");
						return;
					}

					var serverInfo = JsonConvert.DeserializeObject<ServerInfo>(response);
					if (serverInfo == null)
					{
						PrintError(
							"Cannot fetch serverbundle id from VyHub API! Please follow the installation instructions.");
						return;
					}

					_serverBundleID = serverInfo.ServerBundleID;

					Puts("Successfully connected to VyHub API.");
					
					callback?.Invoke();
				}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private void PatchServer(List<Dictionary<string, object>> userActivities)
			{
				var values = new Dictionary<string, object>
				{
					["users_max"] = covalence.Server.MaxPlayers,
					["users_current"] = covalence.Server.Players,
					["user_activities"] = userActivities,
					["is_alive"] = true
				};

				webrequest.Enqueue($"{_config.API.URL}/server/{_config.API.ServerID}", JsonConvert.SerializeObject(values),
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							
							PrintError("Failed to patch server: {0}", response?.ToString()); 
							return;
						}
						
						var serverInfo = JsonConvert.DeserializeObject<ServerInfo>(response);
						if (serverInfo == null)
						{
							PrintError(
								"Cannot fetch serverbundle id from VyHub API! Please follow the installation instructions.");
							return;
						}
						
						_serverBundleID = serverInfo.ServerBundleID;
					}, this, RequestMethod.PATCH, _config.API.GetHeaders());
			}

			private class ServerInfo
			{
				[JsonProperty(PropertyName = "serverbundle_id")]
				public string ServerBundleID;
			}

			#endregion

			#region Adverts
			
			private void FetchAdverts(Action<List<Advert>> callback = null)
			{
				webrequest.Enqueue($"{_config.API.URL}/advert/?active=true&serverbundle_id={_serverBundleID}", null,
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Adverts could not be fetched from VyHub API.");
							return;
						}

						var adverts = JsonConvert.DeserializeObject<List<Advert>>(response);
						if (adverts == null)
						{
							PrintError(
								"Cannot fetch adverts from VyHub API! Please follow the installation instructions.");
							return;
						}

						_adverts = adverts;
						
						callback?.Invoke(adverts);
					}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private class Advert
			{
				[JsonProperty(PropertyName = "id")] public string ID;

				[JsonProperty(PropertyName = "title")] public string Title;

				[JsonProperty(PropertyName = "content")]
				public string Content;

				[JsonProperty(PropertyName = "color")] public string Color;
			}

			#endregion

			#region User

			private void GetUser(string playerID, Action<VyHubUser> callback = null)
			{
				webrequest.Enqueue($"{_config.API.URL}/user/{playerID}?type={GameType}", null, (code, response) =>
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

					var user = JsonConvert.DeserializeObject<VyHubUser>(response);
					if (user == null)
					{
						PrintError(
							"Cannot fetch VyHubUser from VyHub API! Please follow the installation instructions.");
						return;
					}

					callback?.Invoke(user);
				}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private void CreateUser(string playerID, Action<VyHubUser> callback = null)
			{
				var values = new Dictionary<string, string>
				{
					["type"] = GameType,
					["identifier"] = playerID
				};
				
				webrequest.Enqueue($"{_config.API.URL}/user/", JsonConvert.SerializeObject(values), (code, response) =>
				{
					if (response == null || code != 200)
					{
						PrintError("Failed to create user in VyHub API.");
						return;
					}

					var user = JsonConvert.DeserializeObject<VyHubUser>(response);
					if (user == null)
					{PrintError(
							"Cannot fetch VyHubUser from VyHub API! Please follow the installation instructions.");
						return;
					}
					
					callback?.Invoke(user);
				}, this, RequestMethod.POST, _config.API.GetHeaders());
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

			#endregion

			#region Playtime

			private string definitionID;

			private void GetPlaytimeDefinition(Action callback = null)
			{
				if (!string.IsNullOrEmpty(definitionID)) return;

				webrequest.Enqueue($"{_config.API.URL}/user/attribute/definition/playtime", null, (code, response) =>
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

					var definition = JsonConvert.DeserializeObject<PlaytimeDefinition>(response);
					if (definition == null || string.IsNullOrEmpty(definition.ID))
					{
						PrintError("Failed to get playtime definition: {0}", response.ToString());
						return;
					}

					definitionID = definition.ID;
					
					callback?.Invoke();
				}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private void CreatePlaytimeDefinition()
			{
				var values = new Dictionary<string, object>
				{
					["name"] = "playtime",
					["title"] = "Play Time",
					["unit"] = "HOURS",
					["type"] = "ACCUMULATED",
					["accumulation_interval"] = "day",
					["unspecific"] = true
				};

				webrequest.Enqueue($"{_config.API.URL}/user/attribute/definition", JsonConvert.SerializeObject(values),
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Failed to create playtime definition: {0}", response?.ToString());
							return;
						}

						var definition = JsonConvert.DeserializeObject<PlaytimeDefinition>(response);
						if (definition == null || string.IsNullOrEmpty(definition.ID))
						{
							PrintError("Failed to get playtime definition: {0}", response.ToString());
							return;
						}

						definitionID = definition.ID;
					}, this, RequestMethod.POST, _config.API.GetHeaders());
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

				var values = new Dictionary<string, object>
				{
					["definition_id"] = definitionID,
					["user_id"] = user.ID,
					["serverbundle_id"] = _serverBundleID,
					["value"] = hours
				};

				webrequest.Enqueue($"{_config.API.URL}/user/attribute/", JsonConvert.SerializeObject(values),
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							Log(LogType.ERROR, $"[API Error] Failed to Send playtime statistic to API: {response?.ToString()}");
							return;
						}

						callback?.Invoke();
					}, this, RequestMethod.POST, _config.API.GetHeaders());
			}

			private class PlaytimeDefinition
			{
				[JsonProperty(PropertyName = "id")] public string ID;
			}

			#endregion
			
			#region Bans

			private Dictionary<string, List<BanData>> _vyHubBans = new Dictionary<string, List<BanData>>();
			
			private void FetchVyHubBans(Action<Dictionary<string, List<BanData>>> callback = null)
			{
				webrequest.Enqueue($"{_config.API.URL}/server/bundle/{_serverBundleID}/ban?active=true", null,
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Bans could not be fetched from VyHub API.");
							return;
						}

						var bans = JsonConvert.DeserializeObject<Dictionary<string, List<BanData>>>(response);
						if (bans == null)
						{
							PrintError(
								"Cannot fetch bans data from VyHub API! Please follow the installation instructions.");
							return;
						}
                        
						_vyHubBans = bans;
						
						callback?.Invoke(_vyHubBans);
						
					}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private void CreateVyHubBanWithoutCreator(long? finalTime, string reason, string playerID, DateTime time, Action<BanData> callback = null)
			{
				GetUser(playerID, user =>
				{
					var values = new Dictionary<string, object>
					{
						["length"] = finalTime,
						["reason"] = reason,
						["serverbundle_id"] = _serverBundleID,
						["user_id"] = user.ID,
						["created_on"] = time.ToString(@"yyyy-MM-dd\THH:mm:ss.fff\Z")
					};

					webrequest.Enqueue($"{_config.API.URL}/ban/", JsonConvert.SerializeObject(values),
						(code, response) =>
						{
							if (response == null || code != 200)
							{
								PrintError("Failed to create ban: {0}", response?.ToString());
								return;
							}

							var ban = JsonConvert.DeserializeObject<BanData>(response);
							if (ban == null)
							{
								PrintError(
									"Cannot fetch Ban from VyHub API! Please follow the installation instructions.");
								return;
							}

							callback?.Invoke(ban);
						}, this, RequestMethod.POST, _config.API.GetHeaders());
				});
			}

			private void UnbanVyHubUser(string playerID, Action<BanData> callback = null)
			{
				GetUser(playerID, user =>
				{
				webrequest.Enqueue($"{_config.API.URL}/user/{user.ID}/ban?serverbundle_id={_serverBundleID}", null,
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Failed to create ban: {0}", response?.ToString());
							return;
						}

						var ban = JsonConvert.DeserializeObject<BanData>(response);
						if (ban == null)
						{
							PrintError(
								"Cannot fetch Ban from VyHub API! Please follow the installation instructions.");
							return;
						}
						
						callback?.Invoke(ban);
					}, this, RequestMethod.PATCH, _config.API.GetHeaders());
				});
			}

			private class BanData
			{
				[JsonProperty("length")] public long? Length;

				[JsonProperty("reason")] [CanBeNull] public string Reason;

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

				[JsonProperty("name")] [CanBeNull] public string Name;

				[JsonProperty("color")] [CanBeNull] public string Color;

				[JsonProperty("icon")] [CanBeNull] public string Icon;

				[JsonProperty("sort_id")] public long? SortId;
			}

			#endregion

			#region Groups
			
			private void FetchGroups(Action<List<VyHubGroup>> callback = null)
			{
				webrequest.Enqueue($"{_config.API.URL}/group/", null, (code, response) =>
				{
					if (response == null || code != 200)
					{
						PrintError("Failed to fetch groups from VyHub API: {0}", response?.ToString());
						return;
					}

					var groups = JsonConvert.DeserializeObject<List<VyHubGroup>>(response);
					if (groups == null)
					{
						PrintError(
							"Cannot fetch groups from VyHub API! Please follow the installation instructions.");
						return;
					}
                    
					callback?.Invoke(groups);
				}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private void GetUserGroups(VyHubUser user, Action<List<VyHubGroup>> callback = null)
			{
				if (user == null)
					return;

				webrequest.Enqueue($"{_config.API.URL}/user/{user.ID}/group?serverbundle_id={_serverBundleID}", null,
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Failed to fetch memberships from VyHub API: {0}", response?.ToString());
							return;
						}

						var groups = JsonConvert.DeserializeObject<List<VyHubGroup>>(response);
						if (groups == null)
						{
							PrintError(
								"Cannot fetch user groups from VyHub API! Please follow the installation instructions.");
							return;
						}

						callback?.Invoke(groups);
						
					}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private void AddUserToVyHubGroup(VyHubUser user, string groupName, Action<Membership> callback = null)
			{
				VyHubGroup group;
				if (!_vyHubGroups.TryGetValue(groupName, out group))
				{
					Log(LogType.WARNING, $"Could not find group mapping for '{groupName}'.");
					return;
				}
				
				var values = new Dictionary<string, object>
				{
					["group_id"] = group.ID,
					["serverbundle_id"] = _serverBundleID
				};
				
				webrequest.Enqueue($"{_config.API.URL}/user/{user.ID}/membership", JsonConvert.SerializeObject(values),
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Could not add user to group: {0}", response?.ToString());
							return;
						}

						var membership = JsonConvert.DeserializeObject<Membership>(response);
						if (membership == null)
						{
							PrintError(
								"Could not add user to group at VyHub API! Please follow the installation instructions.");
							return;
						}
						
						Log(LogType.INFO, $"Added VyHub group membership in group {group.Name} for player {user.Username}.");
						
						callback?.Invoke(membership);
					}, this, RequestMethod.POST, _config.API.GetHeaders());
			}
			
			private void RemoveUserFromVyHubGroup(VyHubUser user, string groupName, Action<Membership> callback = null)
			{
				VyHubGroup group;
				if (!_vyHubGroups.TryGetValue(groupName, out group))
				{
					Log(LogType.WARNING, $"Could not find group mapping for '{groupName}'.");
					return;
				}

				webrequest.Enqueue(
					$"{_config.API.URL}/user/{user.ID}/membership/by-group?group_id={group.ID}&serverbundle_id={_serverBundleID}",
					null, (code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Could not remove user from groups: {0}", response?.ToString());
							return;
						}

						var membership = JsonConvert.DeserializeObject<Membership>(response);
						if (membership == null)
						{
							PrintError(
								"Could not remove user from groups at VyHub API! Please follow the installation instructions.");
							return;
						}
						
						Log(LogType.INFO, $"Ended VyHub group membership in group {group.Name} for player {user.Username}.");
						
						callback?.Invoke(membership);
					}, this, RequestMethod.DELETE, _config.API.GetHeaders());
			}

			private void RemoveUserFromAllVyHubGroups(VyHubUser user, Action<Membership> callback = null)
			{
				webrequest.Enqueue($"{_config.API.URL}/user/{user.ID}/membership?serverbundle_id={_serverBundleID}", null,
					(code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Could not remove user from all VyHub groups: {0}", response?.ToString());
							return;
						}

						var membership = JsonConvert.DeserializeObject<Membership>(response);
						if (membership == null)
						{
							PrintError(
								"Could not remove user from all VyHub groups from VyHub API! Please follow the installation instructions.");
							return;
						}
						
						Log(LogType.INFO, $"Ended all VyHub group memberships for player {user.Username}.");
						
						callback?.Invoke(membership);
					}, this, RequestMethod.DELETE, _config.API.GetHeaders());
			}
			
			private enum GroupOperation
			{
				add,
				remove
			}
			
			private class VyHubGroup
			{
				[JsonProperty(PropertyName = "id")] public string ID;
				[JsonProperty(PropertyName = "name")] public string Name;

				[JsonProperty(PropertyName = "permission_level")]
				public int PermissionLevel;

				[JsonProperty(PropertyName = "color")] public string Color;

				[JsonProperty(PropertyName = "properties", ObjectCreationHandling = ObjectCreationHandling.Replace)]
				public Dictionary<string, GroupProperty> Properties;

				[JsonProperty(PropertyName = "is_team")]
				public bool IsTeam;

				[JsonProperty(PropertyName = "mappings", ObjectCreationHandling = ObjectCreationHandling.Replace)]
				public List<GroupMapping> Mappings;
			}

			private class GroupProperty
			{
				[JsonProperty(PropertyName = "name")] public string Name;

				[JsonProperty(PropertyName = "granted")]
				public bool Granted;

				[JsonProperty(PropertyName = "value")] public string Value;
			}

			private class GroupMapping
			{
				[JsonProperty(PropertyName = "name")] public string Name;

				[JsonProperty(PropertyName = "serverbundle_id")]
				public string ServerBundleID;

				[JsonProperty(PropertyName = "id")] public string ID;

				[JsonProperty(PropertyName = "group_id")]
				public string GroupID;
			}

			private class Membership
			{
				[JsonProperty(PropertyName = "id")] public string ID;
			}
			
			#endregion

			#region Rewards

			private void FetchRewards(VyHubUser[] onlinePlayers)
			{
				var users = string.Join("&", onlinePlayers.Select(player => $"user_id={player.ID}"));
				if (string.IsNullOrEmpty(users)) return;

				GetRewards(users, RunDirectRewards);
			}
			
			private void GetRewards(string userIDs, Action<Dictionary<string, List<AppliedReward>>> callback = null)
			{
				webrequest.Enqueue(
					$"{_config.API.URL}/packet/reward/applied/user?active=true&foreign_ids=true&status=OPEN&serverbundle_id={_serverBundleID}&for_server_id={_config.API.ServerID}&user_ids={userIDs}",
					null, (code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Failed to get rewards from API: {0}", response?.ToString());
							return;
						}
						
						var rewards = JsonConvert.DeserializeObject<Dictionary<string, List<AppliedReward>>>(response);
						if (rewards == null)
						{
							PrintError(
								"Cannot fetch rewards from VyHub API! Please follow the installation instructions.");
							return;
						}

						_rewards = rewards;
						
						callback?.Invoke(_rewards);
					}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private void GetUserRewards(string userID, Action<Dictionary<string, List<AppliedReward>>> callback = null)
			{
				webrequest.Enqueue(
					$"{_config.API.URL}/packet/reward/applied/user?active=true&foreign_ids=true&status=OPEN&serverbundle_id={_serverBundleID}&for_server_id={_config.API.ServerID}&user_id={userID}",
					null, (code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Failed to get rewards from API: {0}", response?.ToString());
							return;
						}
						
						var rewards = JsonConvert.DeserializeObject<Dictionary<string, List<AppliedReward>>>(response);
						if (rewards == null)
						{
							PrintError(
								"Cannot fetch rewards from VyHub API! Please follow the installation instructions.");
							return;
						}

						callback?.Invoke(rewards);
					}, this, RequestMethod.GET, _config.API.GetHeaders());
			}

			private void SendExecutedReward(string rewardID, Action<AppliedReward> callback = null)
			{
				var values = new Dictionary<string, object>
				{
					["executed_on"] = new List<string>
					{
						_config.API.ServerID
					}
				};

				webrequest.Enqueue($"{_config.API.URL}/packet/reward/applied/{rewardID}",
					JsonConvert.SerializeObject(values), (code, response) =>
					{
						if (response == null || code != 200)
						{
							PrintError("Failed to send executed rewards to API: {0}", response?.ToString());
							return;
						}

						var reward = JsonConvert.DeserializeObject<AppliedReward>(response);
						if (reward == null)
						{
							PrintError(
								"Cannot fetch executed reward from VyHub API! Please follow the installation instructions.");
							return;
						}
						
						callback?.Invoke(reward);
					}, this, RequestMethod.PATCH, _config.API.GetHeaders());
			}

			private class AppliedReward
			{
				[JsonProperty(PropertyName = "id")] public string ID;

				[JsonProperty(PropertyName = "active")]
				public string Active;

				[JsonProperty(PropertyName = "reward")]
				public Reward Reward;

				[JsonProperty(PropertyName = "user")] public User User;

				[JsonProperty(PropertyName = "applied_packet_id")]
				public string AppliedPacketID;

				[JsonProperty(PropertyName = "applied_packet")]
				public AppliedPacket AppliedPacket;

				[JsonProperty(PropertyName = "status")]
				public string Status;

				[JsonProperty(PropertyName = "executed_on", ObjectCreationHandling = ObjectCreationHandling.Replace)]
				public List<string> ExecutedOn;
			}

			private class Reward
			{
				[JsonProperty(PropertyName = "name")] public string Name;
				[JsonProperty(PropertyName = "type")] public string Type;

				[JsonProperty(PropertyName = "data", ObjectCreationHandling = ObjectCreationHandling.Replace)]
				public Dictionary<string, string> Data;

				[JsonProperty(PropertyName = "order")] public int Order;
				[JsonProperty(PropertyName = "once")] public bool Once;

				[JsonProperty(PropertyName = "once_from_all")]
				public bool OnceFromAll;

				[JsonProperty(PropertyName = "on_event")]
				public string OnEvent;

				[JsonProperty(PropertyName = "id")] public string ID;

				[JsonProperty(PropertyName = "serverbundle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
				public ServerBundle ServerBundle;
			}
			
			private class ServerBundle
			{
				[JsonProperty(PropertyName = "id")]
				public string ID;

				[JsonProperty(PropertyName = "name")]
				public string Name;

				[JsonProperty(PropertyName = "color")]
				public string Color;

				[JsonProperty(PropertyName = "icon")]
				public object Icon;

				[JsonProperty(PropertyName = "sort_id")]
				public int SortID;
			}

			private class AppliedPacket
			{
				[JsonProperty(PropertyName = "id")] public string ID;

				[JsonProperty(PropertyName = "purchase")]
				public Purchase Purchase;

				[JsonProperty(PropertyName = "packet")]
				public Packet Packet;
			}

			private class Purchase
			{
				[JsonProperty(PropertyName = "id")] public string ID;

				[JsonProperty(PropertyName = "amount_text")]
				public string AmountText;
			}

			private class Packet
			{
				[JsonProperty(PropertyName = "id")] public string ID;

				[JsonProperty(PropertyName = "title")] public string Title;
			}
			
			private class User
			{
				[JsonProperty(PropertyName = "id")]
				public string ID;

				[JsonProperty(PropertyName = "username")]
				public string Username;

				[JsonProperty(PropertyName = "type")]
				public string Type;

				[JsonProperty(PropertyName = "identifier")]
				public string Identifier;

				[JsonProperty(PropertyName = "avatar")]
				public string Avatar;
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

				_coroutineFetching = ServerMgr.Instance.StartCoroutine(HandleFetching());
			}

			private void StopFetching()
			{
				_enabledFetching = false;
				
				if (_coroutineFetching != null) 
					ServerMgr.Instance.StopCoroutine(_coroutineFetching);
			}

			private IEnumerator HandleFetching()
			{
				while (_enabledFetching)
				{
					var onlinePlayers = GetOnlinePlayers.ToArray();
					
					FetchRewards(onlinePlayers);
					
					FetchVyHubBans(bans =>
					{
						SyncBans();
					});
					
					FetchGroups(groups =>
					{
						UpdateGroups(groups);
						
						SyncGroupsForAll(onlinePlayers);
					});
					
					FetchAdverts();
					
					PatchServer(onlinePlayers);
					
					SendPlayerTime();

					yield return CoroutineEx.waitForSeconds(60);
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
				if (_adverts.Count <  1) return;

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
				if (_dataPlayTimes.ContainsKey(playerID))
				{
					_dataPlayTimes[playerID] = Time.time;
				}
			}

			private void TryAddToPlayTimes(string playerID)
			{
				_dataPlayTimes.TryAdd(playerID, Time.time);
			}
			
			private void SendPlayerTime(bool fast = false)
			{
				#region Cooldown

				if (fast == false && _lastSendPlayerTime > 0f && _lastSendPlayerTime + _cooldownPlayerTime >= Time.time)
				{
					return;
				}
				
				_lastSendPlayerTime = Time.time;

				#endregion
				
				Log(LogType.INFO, "Sending playertime to API");
					
				if (!string.IsNullOrEmpty(definitionID))
				{
					Array.ForEach(_dataPlayTimes.ToArray(), check =>
					{
						var hours = TimeSpan.FromSeconds(Time.time - check.Value).TotalHours;

						SendPlayerTime(check.Key.ToString(), hours, () => ResetTimes(check.Key));
					});
				}
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
				{
					if (_cachedBans.Contains(playerID))
					{
						// Unbanned on VyHub
						Puts($"Unbanning game ban for player {playerID}. (Unbanned on VyHub)");

						if (UnbanGameBan(playerID))
						{
							_cachedBans.Remove(playerID);
						}
					}
					else
					{
						// Missing on VyHub
						Puts($"Adding VyHub ban for player {playerID} from game. (Banned on game server)");

						AddVyHubBan(playerID, ban => _cachedBans.Add(playerID));
					}
				}

				// Checks for bans missing on game server
				foreach (var playerID in bannedVyHubPlayersDiff)
				{
					if (_cachedBans.Contains(playerID))
					{
						// Unbanned on Game Server
						Puts($"Unbanning VyHub ban for player {playerID}. (Unbanned on game server)");

						UnBanVyHub(playerID, ban => _cachedBans.Remove(playerID));
					}
					else
					{
						// Missing on Game Server
						Puts($"Adding game ban for player {playerID} from VyHub. (Banned on VyHub)");

						List<BanData> bans;
						if (fetchedVyHubBans.TryGetValue(playerID, out bans) && AddGameBan(playerID, bans[0]))
							_cachedBans.Add(playerID);
					}
				}

				foreach (var playerID in bannedPlayersIntersect)
					_cachedBans.Add(playerID);
			}

			
			private List<string> GetServerBans()
			{
				var list = new List<string>();

				foreach (var player in covalence.Players.All)
				{
					if (player.IsBanned)
					{
						list.Add(player.Id);
					}
				}

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
				
				GetUser(playerID, user =>
				{
					var serverUser = ServerUsers.Get(Convert.ToUInt64(playerID));
					if (serverUser == null) return;

					CreateVyHubBanWithoutCreator((long) Mathf.Max(serverUser.expiry, 0), serverUser.notes, playerID,
						DateTime.UtcNow, callback);
				});
			}

			private void UnBanVyHub(string playerID, Action<BanData> callback = null)
			{
				GetUser(playerID, user =>
				{
					UnbanVyHubUser(playerID, callback);
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
					{
						foreach (var mapping in group.Mappings)
							if (string.IsNullOrEmpty(mapping.ServerBundleID) ||
							    mapping.ServerBundleID == _serverBundleID)
								newMappedGroups.TryAdd(mapping.Name.ToLower(), group);
					}
					
					_vyHubGroups = newMappedGroups;
				}
			
			private void SyncGroups(VyHubUser user)
			{
				GetUserGroups(user, userGroups =>
				{
					var allGroups = new HashSet<string>();

					foreach (var group in userGroups)
					{
						foreach (var mapping in group.Mappings)
						{
							allGroups.Add(mapping.Name.ToLower());
                            
							if (!permission.GroupExists(group.Name))
								permission.CreateGroup(mapping.Name.ToLower(), mapping.Name, group.PermissionLevel);
						}
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
				{
					if (permission.GroupExists(group) && !permission.UserHasGroup(playerID, group))
					{
						permission.AddUserGroup(playerID, group);

						_groupsBackLog.Add(GetGroupBacklogKey(playerID, group, GroupOperation.add));
					}
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
					if (player == null)
					{
						continue;
					}

					var appliedRewards = check.Value;
					
					foreach (var appliedReward in appliedRewards)
					{
						if (_executedRewards.Contains(appliedReward.ID) ||
						    _executedAndSentRewards.Contains(appliedReward.ID))
						{
							continue;
						}
						
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
								{
									foreach (var cmd in command.Split('|'))
									{
										server.Command(cmd);
									}
								}
							}
							else
							{
								success = false;
							
								Log(LogType.WARNING, $"No implementation for Reward Type: {reward.Type}");
							}

							if (reward.Once)
							{
								SetExecutedReward(appliedReward.ID);
							}

							if (success)
							{
								Log(LogType.INFO, $"RewardName: {reward.Name}, Type: {reward.Type}, Player: {player.Name} ({player.Id}) executed!");
							}
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
					"DISABLE",
				}, null);
			}
			
			private void SetExecutedReward(string rewardID)
			{
				_executedRewards.Add(rewardID);
				
				SaveExecutedRewards();
			}
			
			private void SendExecutedRewards()
			{
				_coroutinesSendExecutedRewards.Add(ServerMgr.Instance.StartCoroutine(AsyncExecutedRewards()));
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
		}
	}