// #define DEBUG

using Newtonsoft.Json;
using Oxide.Core.Libraries;
using UnityEngine;

namespace Oxide.Plugins;

[Info("VyHub", "VyHub", "1.0.0")]
[Description("VyHub plugin to manage and monetize your Rust / 7 Days to Die server. You can create your webstore for free with VyHub!")]
public class VyHub : CovalencePlugin
{
	#region Fields

	private string _serverBundleID = string.Empty;
	
	#endregion

	#region Config

	private Configuration _config;

	private class Configuration
	{
		[JsonProperty(PropertyName = "API Settings")]
		public APISettings API = new APISettings
		{
			URL = "https://api.vyhub.app/<name>/v1",
			Key = "Admin -> Settings -> Server -> Setup",
			ServerID = "Admin -> Settings -> Server -> Setup"
		};
	}

	private class APISettings
	{
		[JsonProperty(PropertyName = "URL")]
		public string URL;

		[JsonProperty(PropertyName = "Key")] public string Key;

		[JsonProperty(PropertyName = "Server ID")]
		public string ServerID;

		public Dictionary<string, string> GetHeaders()
		{
			return new Dictionary<string, string>
			{
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

	protected override void SaveConfig() => Config.WriteObject(_config);

	protected override void LoadDefaultConfig() => _config = new Configuration();

	#endregion

	#region Hooks

	private void OnServerInitialized()
	{
		GetServerInformation();
	}

	#endregion

	#region Interface

	

	#endregion

	#region Utils

	

	#endregion

	#region Lang

	

	#endregion

	#region API Client

	private void GetServerInformation()
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
				PrintError("Cannot fetch serverbundle id from VyHub API! Please follow the installation instructions.");
				return;
			}
			
			_serverBundleID = serverInfo.ServerBundleID;
			
			Puts("Successfully connected to VyHub API.");
		}, this, RequestMethod.GET, _config.API.GetHeaders());
	}
	
	private class ServerInfo
	{
		[JsonProperty(PropertyName = "serverbundle_id")]
		public string ServerBundleID;
	}
	
	#endregion
}