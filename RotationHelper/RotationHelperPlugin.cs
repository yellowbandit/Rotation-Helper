using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Events;
using VoK.Sdk.Plugins;
using VoK.Sdk.Properties;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]
[assembly: AssemblyCompany("RotationHelper")]
[assembly: AssemblyConfiguration("Debug")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyProduct("RotationHelper")]
[assembly: AssemblyTitle("RotationHelper")]
[assembly: TargetPlatform("Windows7.0")]
[assembly: SupportedOSPlatform("Windows7.0")]
[assembly: AssemblyVersion("1.0.0.0")]
namespace RotationHelper;

public class AutoBuffConfig
{
	public string Name { get; set; }

	public uint ActionDid { get; set; }

	public uint? EffectDid { get; set; }

	public int DurationSeconds { get; set; }

	public int RecastThresholdSeconds { get; set; } = 30;

	public uint CommandValue { get; set; }

	public bool Enabled { get; set; } = true;

	public bool DisableInPublic { get; set; }
}
public class AutoBuffStatus
{
	public AutoBuffConfig Config { get; set; }

	public bool IsActive { get; set; }

	public int RemainingSeconds { get; set; }

	public string State { get; set; } = "Unknown";

	public DateTime LastFired { get; set; } = DateTime.MinValue;
}
public class BuffManager : IDisposable
{
	private readonly IDdoGameDataProvider _gameDataProvider;

	private readonly IGameDataProvider _gameData;

	private readonly IPropertyMaster _propertyMaster;

	private readonly Action<string> _log;

	private readonly string _configDir;

	private System.Threading.Timer _maintenanceTimer;

	private bool _tickRunning;

	private bool _disposed;

	private bool _progressActive;

	private int _zoneChangeCount;

	private int _lastZoneChangeCount;

	private readonly Dictionary<uint, DateTime> _lastFired = new Dictionary<uint, DateTime>();

	private bool _stancesInitialized;

	public BuffProfile Profile { get; private set; } = new BuffProfile();

	public bool DisableInPublicAreas { get; set; }

	public List<uint> EnabledZoneDids { get; set; } = new List<uint>();

	public BuffManager(IDdoGameDataProvider gameDataProvider, string configDir, Action<string> log)
	{
		_gameDataProvider = gameDataProvider;
		_gameData = gameDataProvider;
		_propertyMaster = _gameData?.PropertyMaster;
		_configDir = configDir;
		_log = log;
	}

	public void Initialize()
	{
		SubscribeEvents();
		LoadProfile();
		_maintenanceTimer = new System.Threading.Timer(delegate
		{
			MaintenanceTick();
		}, null, 5000, 1500);
		_log("BuffManager initialized");
	}

	public void Shutdown()
	{
		_disposed = true;
		_maintenanceTimer?.Dispose();
		_maintenanceTimer = null;
		SaveProfile();
	}

	public void Dispose()
	{
		Shutdown();
	}

	private void SubscribeEvents()
	{
		IDdoEventProvider eventProvider = _gameDataProvider.EventProvider;
		if (eventProvider != null)
		{
			eventProvider.OnLogin.AddHandler(delegate
			{
				_log("BuffManager: OnLogin — resetting state");
				_lastFired.Clear();
				_stancesInitialized = false;
				_zoneChangeCount = 0;
				_lastZoneChangeCount = 0;
				LoadProfile();
				return Task.CompletedTask;
			});
			eventProvider.OnLogout.AddHandler(delegate
			{
				_log("BuffManager: OnLogout — saving profile");
				SaveProfile();
				_lastFired.Clear();
				_stancesInitialized = false;
				return Task.CompletedTask;
			});
			eventProvider.OnProgressStart.AddHandler(delegate
			{
				_progressActive = true;
				return Task.CompletedTask;
			});
			eventProvider.OnProgressStop.AddHandler(delegate
			{
				_progressActive = false;
				Interlocked.Increment(ref _zoneChangeCount);
				_log("BuffManager: OnProgressStop — zone change detected");
				return Task.CompletedTask;
			});
		}
	}

	public void LoadProfile()
	{
		try
		{
			string profilePath = GetProfilePath();
			if (File.Exists(profilePath))
			{
				string json = File.ReadAllText(profilePath);
				BuffProfile buffProfile = JsonSerializer.Deserialize<BuffProfile>(json);
				if (buffProfile != null)
				{
					Profile = buffProfile;
					_log($"BuffManager: loaded profile ({Profile.AutoBuffs.Count} auto buffs, {Profile.ToggledStances.Count} stances)");
				}
			}
		}
		catch (Exception ex)
		{
			_log("BuffManager: load error " + ex.Message);
		}
	}

	public void SaveProfile()
	{
		try
		{
			string profilePath = GetProfilePath();
			string directoryName = Path.GetDirectoryName(profilePath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			string contents = JsonSerializer.Serialize(Profile, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			File.WriteAllText(profilePath, contents);
		}
		catch (Exception ex)
		{
			_log("BuffManager: save error " + ex.Message);
		}
	}

	private string GetProfilePath()
	{
		return Path.Combine(_configDir, "buffs.json");
	}

	public void MaintenanceTick()
	{
		if (_tickRunning || _disposed)
		{
			return;
		}
		_tickRunning = true;
		try
		{
			DateTime utcNow = DateTime.UtcNow;
			int num = Volatile.Read(in _zoneChangeCount);
			if (num != _lastZoneChangeCount)
			{
				_lastZoneChangeCount = num;
				_stancesInitialized = false;
				_log("BuffManager: zone change detected, resetting stance state");
			}
			if (_progressActive)
			{
				return;
			}
			bool flag = DisableInPublicAreas && IsInPublicArea();
			if (_stancesInitialized || flag)
			{
				return;
			}
			foreach (ToggledStanceConfig toggledStance in Profile.ToggledStances)
			{
				if (toggledStance.Enabled && (toggledStance.ActionDid == 0 || !_lastFired.TryGetValue(toggledStance.PropertyDid, out var value) || !((utcNow - value).TotalSeconds < 5.0)) && !IsStanceActive(toggledStance))
				{
					ActivateStance(toggledStance);
				}
			}
			_stancesInitialized = true;
		}
		catch (Exception ex)
		{
			_log("BuffManager: tick error " + ex.Message);
		}
		finally
		{
			_tickRunning = false;
		}
	}

	private bool IsInPublicArea()
	{
		try
		{
			bool? flag = _gameDataProvider?.InTown();
			if (flag.HasValue)
			{
				return flag.Value;
			}
			IEntity entity = _gameData?.GetCurrentCharacter();
			int result;
			if (entity != null)
			{
				ILocation location = entity.Location;
				result = ((location != null && !location.Indoors) ? 1 : 0);
			}
			else
			{
				result = 0;
			}
			return (byte)result != 0;
		}
		catch
		{
			return false;
		}
	}

	public bool IsBuffActive(AutoBuffConfig buff)
	{
		try
		{
			IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _gameDataProvider?.GetActiveEffects();
			if (readOnlyDictionary == null || readOnlyDictionary.Count == 0)
			{
				return false;
			}
			uint num = buff.EffectDid ?? buff.ActionDid;
			if (num == 0)
			{
				return false;
			}
			foreach (KeyValuePair<int, IActiveEffect> item in readOnlyDictionary)
			{
				IActiveEffect value = item.Value;
				if (value.EffectDid != (int)num)
				{
					continue;
				}
				if (value.Duration.GetValueOrDefault() == -1f)
				{
					return true;
				}
				if (value.Duration.HasValue && !(value.Duration <= 0f))
				{
					DateTime? dateTime = value.CalculateExpiration();
					if (dateTime.HasValue && dateTime.Value > DateTime.UtcNow)
					{
						return true;
					}
				}
			}
			if (!buff.EffectDid.HasValue && buff.ActionDid != 0)
			{
				uint num2 = 0u;
				try
				{
					IEntity entity = _gameData?.GetCurrentCharacter();
					if (entity != null && _propertyMaster != null)
					{
						foreach (KeyValuePair<int, IActiveEffect> item2 in readOnlyDictionary)
						{
							IPropertyCollection propertyCollection = _propertyMaster.GetPropertyCollection((uint)item2.Value.EffectDid);
							if (propertyCollection == null)
							{
								continue;
							}
							foreach (KeyValuePair<uint, IProperty> property in propertyCollection.Properties)
							{
								if (property.Value is IUInt32Property { UInt32Value: var uInt32Value } && uInt32Value == buff.ActionDid)
								{
									num2 = (uint)item2.Value.EffectDid;
									buff.EffectDid = num2;
									return true;
								}
							}
						}
					}
				}
				catch
				{
				}
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	public BuffRemaining GetBuffRemaining(AutoBuffConfig buff)
	{
		try
		{
			IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _gameDataProvider?.GetActiveEffects();
			if (readOnlyDictionary == null || readOnlyDictionary.Count == 0)
			{
				return new BuffRemaining
				{
					IsActive = false
				};
			}
			uint num = buff.EffectDid ?? buff.ActionDid;
			if (num == 0)
			{
				return new BuffRemaining
				{
					IsActive = false
				};
			}
			foreach (KeyValuePair<int, IActiveEffect> item in readOnlyDictionary)
			{
				IActiveEffect value = item.Value;
				if (value.EffectDid != (int)num)
				{
					continue;
				}
				if (value.Duration.GetValueOrDefault() == -1f)
				{
					return new BuffRemaining
					{
						IsActive = true,
						RemainingSeconds = int.MaxValue
					};
				}
				if (value.Duration.HasValue && !(value.Duration <= 0f))
				{
					DateTime? dateTime = value.CalculateExpiration();
					if (dateTime.HasValue && dateTime.Value > DateTime.UtcNow)
					{
						int remainingSeconds = (int)(dateTime.Value - DateTime.UtcNow).TotalSeconds;
						return new BuffRemaining
						{
							IsActive = true,
							RemainingSeconds = remainingSeconds
						};
					}
				}
			}
		}
		catch
		{
		}
		return new BuffRemaining
		{
			IsActive = false
		};
	}

	private bool ShouldRecastBuff(AutoBuffConfig buff)
	{
		BuffRemaining buffRemaining = GetBuffRemaining(buff);
		if (!buffRemaining.IsActive)
		{
			return true;
		}
		if (buffRemaining.RemainingSeconds < buff.RecastThresholdSeconds)
		{
			return true;
		}
		if (buff.DurationSeconds > 0 && (double)buffRemaining.RemainingSeconds < (double)buff.DurationSeconds * 0.2)
		{
			return true;
		}
		return false;
	}

	private void FireBuff(AutoBuffConfig buff)
	{
		if (buff.ActionDid == 0 && buff.CommandValue == 0)
		{
			return;
		}
		try
		{
			if (buff.CommandValue != 0)
			{
				_gameDataProvider?.SendInput(buff.CommandValue);
			}
			else if (buff.ActionDid != 0)
			{
				_log($"BuffManager: fire buff [{buff.Name}] via DID 0x{buff.ActionDid:X8} (no command cached)");
				return;
			}
			_lastFired[buff.ActionDid] = DateTime.UtcNow;
			_log("BuffManager: recast [" + buff.Name + "]");
		}
		catch (Exception ex)
		{
			_log("BuffManager: fire buff error [" + buff.Name + "]: " + ex.Message);
		}
	}

	public bool IsStanceActive(ToggledStanceConfig stance)
	{
		if (stance.PropertyDid == 0)
		{
			return false;
		}
		try
		{
			IEntity entity = _gameData?.GetCurrentCharacter();
			if (entity == null)
			{
				return false;
			}
			IProperty property = entity.GetProperty(stance.PropertyDid);
			if (property == null)
			{
				return false;
			}
			string text = property.Value?.ToString();
			if (string.IsNullOrEmpty(stance.ExpectedValue))
			{
				return !string.IsNullOrEmpty(text);
			}
			return string.Equals(text, stance.ExpectedValue, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private void ActivateStance(ToggledStanceConfig stance)
	{
		if (stance.CommandValue == 0 && stance.ActionDid == 0)
		{
			return;
		}
		try
		{
			if (stance.CommandValue != 0)
			{
				_gameDataProvider?.SendInput(stance.CommandValue);
				_lastFired[stance.PropertyDid] = DateTime.UtcNow;
				_log("BuffManager: activated stance [" + stance.Name + "]");
			}
			else
			{
				_log("BuffManager: cannot activate stance [" + stance.Name + "] — no command value");
			}
		}
		catch (Exception ex)
		{
			_log("BuffManager: activate stance error [" + stance.Name + "]: " + ex.Message);
		}
	}

	public void ForceRefreshStances()
	{
		_stancesInitialized = false;
		_log("BuffManager: forced stance refresh on next tick");
	}

	public void ResetZoneState()
	{
		_stancesInitialized = false;
		Interlocked.Increment(ref _zoneChangeCount);
	}

	public List<AutoBuffStatus> GetAutoBuffStatuses()
	{
		List<AutoBuffStatus> list = new List<AutoBuffStatus>();
		foreach (AutoBuffConfig autoBuff in Profile.AutoBuffs)
		{
			BuffRemaining buffRemaining = GetBuffRemaining(autoBuff);
			string state = (buffRemaining.IsActive ? ((buffRemaining.RemainingSeconds >= autoBuff.RecastThresholdSeconds) ? $"Active ({buffRemaining.RemainingSeconds}s)" : $"Expiring ({buffRemaining.RemainingSeconds}s)") : "Missing");
			_lastFired.TryGetValue(autoBuff.ActionDid, out var value);
			list.Add(new AutoBuffStatus
			{
				Config = autoBuff,
				IsActive = buffRemaining.IsActive,
				RemainingSeconds = buffRemaining.RemainingSeconds,
				State = state,
				LastFired = value
			});
		}
		return list;
	}

	public List<StanceStatus> GetStanceStatuses()
	{
		List<StanceStatus> list = new List<StanceStatus>();
		foreach (ToggledStanceConfig toggledStance in Profile.ToggledStances)
		{
			bool isActive = IsStanceActive(toggledStance);
			string currentValue = "";
			try
			{
				IEntity entity = _gameData?.GetCurrentCharacter();
				if (entity != null && toggledStance.PropertyDid != 0)
				{
					currentValue = entity.GetProperty(toggledStance.PropertyDid)?.Value?.ToString() ?? "(null)";
				}
			}
			catch
			{
				currentValue = "(error)";
			}
			list.Add(new StanceStatus
			{
				Config = toggledStance,
				IsActive = isActive,
				CurrentValue = currentValue,
				FiredThisZone = _stancesInitialized
			});
		}
		return list;
	}
}
public class BuffProfile
{
	public List<AutoBuffConfig> AutoBuffs { get; set; } = new List<AutoBuffConfig>();

	public List<ToggledStanceConfig> ToggledStances { get; set; } = new List<ToggledStanceConfig>();
}
public class BuffRemaining
{
	public bool IsActive { get; set; }

	public int RemainingSeconds { get; set; }
}
public class GameDataCache
{
	private readonly HttpClient _http;

	private readonly ConcurrentDictionary<uint, string> _didToName = new ConcurrentDictionary<uint, string>();

	private readonly ConcurrentDictionary<string, List<uint>> _nameToDids = new ConcurrentDictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<uint, uint> _actionToEffectDid = new ConcurrentDictionary<uint, uint>();

	public GameDataCache(string baseUrl = "http://localhost:5138")
	{
		_http = new HttpClient
		{
			BaseAddress = new Uri(baseUrl),
			Timeout = TimeSpan.FromSeconds(3.0)
		};
	}

	public string GetCachedName(uint did)
	{
		_didToName.TryGetValue(did, out var value);
		return value;
	}

	public uint GetCachedEffectDid(uint actionDid)
	{
		_actionToEffectDid.TryGetValue(actionDid, out var value);
		return value;
	}

	public void CacheEffectDid(uint actionDid, uint effectDid)
	{
		_actionToEffectDid[actionDid] = effectDid;
	}

	public async Task<string> FetchNameAsync(uint did)
	{
		if (_didToName.TryGetValue(did, out var name))
		{
			return name;
		}
		try
		{
			HttpResponseMessage resp = await _http.GetAsync($"/DbProperties/0x{did:X8}");
			if (!resp.IsSuccessStatusCode)
			{
				return null;
			}
			using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
			if (doc.RootElement.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
			{
				name = nameProp.GetString();
				if (!string.IsNullOrEmpty(name))
				{
					_didToName[did] = name;
					return name;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	public async Task<List<uint>> FetchDidsByNameAsync(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return new List<uint>();
		}
		if (_nameToDids.TryGetValue(name, out var cached))
		{
			return cached;
		}
		try
		{
			HttpResponseMessage resp = await _http.GetAsync("/DbProperties/IdsForName?name=" + Uri.EscapeDataString(name));
			if (!resp.IsSuccessStatusCode)
			{
				return new List<uint>();
			}
			List<int> ids = JsonSerializer.Deserialize<List<int>>(await resp.Content.ReadAsStringAsync());
			List<uint> result = new List<uint>();
			if (ids != null)
			{
				foreach (int id in ids)
				{
					if (id > 0)
					{
						result.Add((uint)id);
					}
				}
			}
			_nameToDids[name] = result;
			return result;
		}
		catch
		{
			return new List<uint>();
		}
	}

	public async Task<List<SearchResult>> SearchByNameAsync(string name, int maxResults = 5)
	{
		try
		{
			var req = new SearchRequest { Words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries), MaxResults = maxResults };
			var json = JsonSerializer.Serialize(req);
			var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
			var resp = await _http.PostAsync("/DbProperties/Search", content).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode) return new List<SearchResult>();
			return JsonSerializer.Deserialize<List<SearchResult>>(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)) ?? new List<SearchResult>();
		}
		catch { return new List<SearchResult>(); }
	}

	public async Task<SearchResult> SearchBestMatchAsync(string name)
	{
		var results = await SearchByNameAsync(name, 5).ConfigureAwait(false);
		foreach (var r in results.OrderByDescending(x => x.Score))
			if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
				return r;
		return results.FirstOrDefault();
	}

	public async Task<bool> CheckAliveAsync()
	{
		try
		{
			return (await _http.GetAsync("/Cache/Metadata")).IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}
}
public class SearchRequest
{
	public string[] Words { get; set; }
	public int MaxResults { get; set; } = 5;
}
public class SearchResult
{
	public int Id { get; set; }
	public string Name { get; set; }
	public double Score { get; set; }
}
public class HotbarAbility
{
	public string Name { get; set; }

	public int BarId { get; set; }

	public int SlotId { get; set; }

	public uint CommandValue { get; set; }

	public uint ReferenceDid { get; set; }

	public int Index { get; set; }

	public long CooldownMs { get; set; } = 0L;

	public long BuffDurationMs { get; set; } = 0L;

	public uint CooldownTimerDid1 { get; set; }

	public uint CooldownTimerDid2 { get; set; }

	public uint CooldownTimerDid3 { get; set; }

	public string ApiTargetType { get; set; } = "";

	public bool? ApiIsHarmful { get; set; }

	public string Display
	{
		get
		{
			string value = $" [Bar {BarId + 1}, Slot {SlotId + 1}]";
			string value2 = ((CooldownMs > 0) ? $" CD:{(double)CooldownMs / 1000.0:F0}s" : "");
			string value3 = ((BuffDurationMs <= 0) ? "" : ((BuffDurationMs >= 60000) ? $" Dur:{BuffDurationMs / 60000}min" : $" Dur:{BuffDurationMs / 1000}s"));
			string value4 = ((!string.IsNullOrEmpty(ApiTargetType)) ? (" [" + ApiTargetType + "]") : "");
			return $"{Name}{value2}{value3}{value4}{value}";
		}
	}
}
public class HudOverlayForm : Form
{
	public struct ToggleStateInfo
	{
		public string Name;

		public bool Active;
	}

	private struct CooldownItem
	{
		public string Name;

		public double Remaining;

		public double TotalMs;
	}

	private struct BuffItem
	{
		public string Name;

		public double Remaining;

		public double Duration;
	}

	private struct TargetInfo
	{
		public string Name;

		public double Distance;

		public bool HasTarget;
	}

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct HudSnapshot
	{
		public bool IsPaused;

		public TargetInfo Target;

		public ToggleStateInfo[] ToggleStates;

		public CooldownItem[] Cooldowns;

		public BuffItem[] Buffs;

		public static HudSnapshot Default => new HudSnapshot
		{
			ToggleStates = Array.Empty<ToggleStateInfo>(),
			Cooldowns = Array.Empty<CooldownItem>(),
			Buffs = Array.Empty<BuffItem>()
		};
	}

	private readonly RotationHelperPlugin _plugin;

	private System.Windows.Forms.Timer _updateTimer;

	private HudSnapshot _snapshot;

	private bool _disposed;

	private bool _initialPositionSet;

	private DateTime _lastTopmostSet = DateTime.MinValue;

	private const int HudWidth = 290;

	private const int LineH = 24;

	private const int Pad = 8;

	private const int HeaderH = 26;

	private const int Gap = 4;

	private const int BarH = 12;

	private const int BarYOff = 6;

	private const int LabelH = 20;

	private static readonly Color BgColor = Color.FromArgb(25, 25, 25);

	private static readonly Color BorderColor = Color.FromArgb(80, 80, 80);

	private static readonly Color TextColor = Color.White;

	private static readonly Color DimTextColor = Color.FromArgb(180, 180, 180);

	private static readonly Color AccentGreen = Color.FromArgb(100, 220, 100);

	private static readonly Color AccentRed = Color.FromArgb(220, 80, 80);

	private static readonly Color AccentYellow = Color.FromArgb(220, 220, 80);

	private static readonly Color AccentBlue = Color.FromArgb(80, 160, 255);

	private static readonly Color BarBgColor = Color.FromArgb(50, 50, 50);

	private static readonly Color BarFillColor = Color.FromArgb(60, 140, 255);

	private static readonly System.Drawing.Font MainFont = new System.Drawing.Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

	private static readonly System.Drawing.Font BoldFont = new System.Drawing.Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);

	private static readonly System.Drawing.Font SmallFont = new System.Drawing.Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel);

	private static readonly System.Drawing.Font HeaderFont = new System.Drawing.Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);

	private static readonly StringFormat LeftAlign = new StringFormat
	{
		Alignment = StringAlignment.Near,
		LineAlignment = StringAlignment.Near
	};

	private static readonly StringFormat RightAlign = new StringFormat
	{
		Alignment = StringAlignment.Far,
		LineAlignment = StringAlignment.Near
	};

	private static readonly nint HWND_TOPMOST = new IntPtr(-1);

	private const uint SWP_NOMOVE = 2u;

	private const uint SWP_NOSIZE = 1u;

	private const uint SWP_NOACTIVATE = 16u;

	public HudOverlayForm(RotationHelperPlugin plugin)
	{
		_plugin = plugin;
		_snapshot = HudSnapshot.Default;
		SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.DoubleBuffer, value: true);
		base.FormBorderStyle = FormBorderStyle.None;
		base.ShowInTaskbar = false;
		base.StartPosition = FormStartPosition.Manual;
		base.Size = new Size(290, 200);
		BackColor = BgColor;
		base.TopMost = true;
		base.HandleCreated += OnHandleCreated;
		_updateTimer = new System.Windows.Forms.Timer
		{
			Interval = 50
		};
		_updateTimer.Tick += delegate
		{
			UpdateHud();
		};
		_ = base.Handle;
	}

	protected override void WndProc(ref Message m)
	{
		if (m.Msg == 132)
		{
			base.WndProc(ref m);
			if (m.Result == 1)
			{
				m.Result = 2;
			}
		}
		else
		{
			base.WndProc(ref m);
		}
	}

	private void OnHandleCreated(object sender, EventArgs e)
	{
		try
		{
			SetInitialPosition();
			base.Visible = _plugin._config.HudEnabled;
			_updateTimer.Start();
			base.Move += delegate
			{
				_plugin._config.HudX = base.Location.X;
				_plugin._config.HudY = base.Location.Y;
			};
		}
		catch (Exception ex)
		{
			_plugin.Log("HUD: HandleCreated error: " + ex.Message);
		}
	}

	private void SetInitialPosition()
	{
		if (_initialPositionSet)
		{
			return;
		}
		_initialPositionSet = true;
		if (_plugin._config.HudX >= 0 && _plugin._config.HudY >= 0)
		{
			base.Location = new Point(_plugin._config.HudX, _plugin._config.HudY);
			return;
		}
		nint num = _plugin._gameDataProvider?.GetGameWindowHandle() ?? IntPtr.Zero;
		if (num != IntPtr.Zero && GetWindowRect(num, out var lpRect))
		{
			int num2 = lpRect.Right - lpRect.Left;
			int x = num2 - 290 - 8;
			base.Location = new Point(x, 8);
		}
	}

	public void UpdateHud()
	{
		if (_disposed || _plugin?._config == null)
		{
			if (base.Visible)
			{
				Hide();
			}
			return;
		}
		bool hudEnabled = _plugin._config.HudEnabled;
		if (!hudEnabled && base.Visible)
		{
			Hide();
		}
		else if (hudEnabled && !base.Visible)
		{
			Show();
		}
		else
		{
			if (!hudEnabled)
			{
				return;
			}
			try
			{
				DateTime utcNow = DateTime.UtcNow;
				if ((utcNow - _lastTopmostSet).TotalMilliseconds >= 500.0)
				{
					SetWindowPos(base.Handle, HWND_TOPMOST, 0, 0, 0, 0, 19u);
					_lastTopmostSet = utcNow;
				}
				HudSnapshot snapshot = _snapshot;
				_snapshot = CaptureSnapshot();
				int num = ComputeHeight(_snapshot);
				int num2 = ComputeHeight(snapshot);
				if (num != num2 && base.Height != num)
				{
					base.Size = new Size(290, num);
				}
				Refresh();
			}
			catch
			{
			}
		}
	}

	private HudSnapshot CaptureSnapshot()
	{
		HudSnapshot result = new HudSnapshot
		{
			ToggleStates = Array.Empty<ToggleStateInfo>(),
			Cooldowns = Array.Empty<CooldownItem>(),
			Buffs = Array.Empty<BuffItem>(),
			Target = default(TargetInfo)
		};
		RotationConfig config = _plugin._config;
		result.IsPaused = _plugin.IsPaused;
		if (config.HudShowToggles)
		{
			result.ToggleStates = _plugin.GetToggleHudStates() ?? Array.Empty<ToggleStateInfo>();
		}
		DateTime utcNow = DateTime.UtcNow;
		if (config.HudShowTarget)
		{
			try
			{
				IEntity entity = _plugin._gameDataProvider?.GetCurrentCharacter();
				IEntity entity2 = _plugin._gameDataProvider?.GetSelectedObject();
				if (entity2 != null && entity != null && entity2.InstanceId != 0L && entity2.InstanceId != entity.InstanceId)
				{
					result.Target = new TargetInfo
					{
						Name = (entity2.Name ?? "Unknown"),
						HasTarget = true,
						Distance = (_plugin.GetDistanceToTarget() ?? (-1.0))
					};
				}
			}
			catch
			{
			}
		}
		if (config.HudShowCooldowns)
		{
			List<CooldownItem> list = new List<CooldownItem>();
			foreach (RotationAbility item in _plugin.Rotation)
			{
				if (item.Autocast && item.CooldownMs > 0)
				{
					long num = 0L;
					try
					{
						num = _plugin.GetRemainingCooldownMs(item);
					}
					catch
					{
					}
					if (num > 0)
					{
						list.Add(new CooldownItem
						{
							Name = item.Name,
							Remaining = (double)num / 1000.0,
							TotalMs = item.CooldownMs
						});
					}
				}
			}
			result.Cooldowns = list.OrderBy((CooldownItem c) => c.Remaining).Take(5).ToArray();
		}
		if (config.HudShowBuffs)
		{
			List<BuffItem> list2 = new List<BuffItem>();
			try
			{
				IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _plugin._gameDataProvider?.GetActiveEffects();
				IPropertyMaster propertyMaster = _plugin._gameDataProvider?.PropertyMaster;
				if (readOnlyDictionary != null && propertyMaster != null)
				{
					foreach (KeyValuePair<int, IActiveEffect> item2 in readOnlyDictionary)
					{
						IActiveEffect value = item2.Value;
						if (!value.Duration.HasValue || value.Duration <= 0f || value.Duration.GetValueOrDefault() == -1f)
						{
							continue;
						}
						DateTime? dateTime = value.CalculateExpiration();
						if (!dateTime.HasValue)
						{
							continue;
						}
						double totalSeconds = (dateTime.Value - utcNow).TotalSeconds;
						if (totalSeconds <= 0.0 || totalSeconds > 3600.0)
						{
							continue;
						}
						string text = null;
						try
						{
							IPropertyCollection propertyCollection = propertyMaster.GetPropertyCollection((uint)value.EffectDid);
							if (propertyCollection != null)
							{
								if (!string.IsNullOrEmpty(propertyCollection.Name))
								{
									text = propertyCollection.Name.Trim();
								}
								if (string.IsNullOrEmpty(text))
								{
									IStringInfoProperty stringInfoProperty = propertyCollection.GetStringInfoProperty(1u);
									if (stringInfoProperty != null)
									{
										text = stringInfoProperty.GetText(propertyMaster, (uint)value.EffectDid, propertyCollection);
									}
								}
							}
						}
						catch
						{
						}
						list2.Add(new BuffItem
						{
							Name = (text ?? $"#{value.EffectDid:X}"),
							Remaining = totalSeconds,
							Duration = value.Duration.Value
						});
					}
				}
			}
			catch
			{
			}
			result.Buffs = list2.OrderBy((BuffItem b) => b.Remaining).Take(5).ToArray();
		}
		return result;
	}

	private int ComputeHeight(HudSnapshot snap)
	{
		RotationConfig config = _plugin._config;
		int num = 8;
		if (snap.IsPaused && config.HudShowPaused)
		{
			num += 28;
		}
		if (snap.Target.HasTarget && config.HudShowTarget)
		{
			num += 58;
		}
		if (snap.Cooldowns.Length != 0 && config.HudShowCooldowns)
		{
			num += 34 + snap.Cooldowns.Length * 42;
		}
		if (snap.ToggleStates.Length != 0 && config.HudShowToggles)
		{
			num += 34 + snap.ToggleStates.Length * 24;
		}
		if (snap.Buffs.Length != 0 && config.HudShowBuffs)
		{
			num += 34 + snap.Buffs.Length * 42;
		}
		num += 8;
		return Math.Max(num, 60);
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		if (_disposed)
		{
			return;
		}
		base.OnPaint(e);
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
		int width = base.ClientSize.Width;
		int y = 8;
		RotationConfig config = _plugin._config;
		using (SolidBrush brush = new SolidBrush(BgColor))
		{
			graphics.FillRectangle(brush, 0, 0, width, base.ClientSize.Height);
		}
		if (_snapshot.IsPaused && config.HudShowPaused)
		{
			using (SolidBrush brush2 = new SolidBrush(AccentRed))
			{
				graphics.DrawString("PAUSED", BoldFont, brush2, 8f, y);
			}
			y += 24;
		}
		if (_snapshot.Target.HasTarget && config.HudShowTarget)
		{
			y += 4;
			DrawSection(graphics, "TARGET", ref y);
			y += 4;
			string right = ((_snapshot.Target.Distance >= 0.0) ? $"{_snapshot.Target.Distance:F1}u" : "");
			DrawLine(graphics, ref y, _snapshot.Target.Name, AccentYellow, right);
		}
		if (_snapshot.Cooldowns.Length != 0 && config.HudShowCooldowns)
		{
			y += 4;
			DrawSection(graphics, "COOLDOWNS", ref y);
			y += 4;
			CooldownItem[] cooldowns = _snapshot.Cooldowns;
			for (int i = 0; i < cooldowns.Length; i++)
			{
				CooldownItem cooldownItem = cooldowns[i];
				float num = (float)(1.0 - cooldownItem.Remaining * 1000.0 / cooldownItem.TotalMs);
				if (num < 0f)
				{
					num = 0f;
				}
				if (num > 1f)
				{
					num = 1f;
				}
				DrawBar(graphics, ref y, cooldownItem.Name, num, $"{cooldownItem.Remaining:F1}s");
			}
		}
		if (_snapshot.ToggleStates.Length != 0 && config.HudShowToggles)
		{
			y += 4;
			DrawSection(graphics, "TOGGLES", ref y);
			y += 4;
			ToggleStateInfo[] toggleStates = _snapshot.ToggleStates;
			for (int j = 0; j < toggleStates.Length; j++)
			{
				ToggleStateInfo toggleStateInfo = toggleStates[j];
				DrawLine(graphics, ref y, toggleStateInfo.Name, toggleStateInfo.Active ? AccentGreen : DimTextColor, toggleStateInfo.Active ? "ON" : "OFF");
			}
		}
		if (_snapshot.Buffs.Length != 0 && config.HudShowBuffs)
		{
			y += 4;
			DrawSection(graphics, "BUFFS", ref y);
			y += 4;
			BuffItem[] buffs = _snapshot.Buffs;
			for (int k = 0; k < buffs.Length; k++)
			{
				BuffItem buffItem = buffs[k];
				float num2 = (float)(buffItem.Remaining / buffItem.Duration);
				if (num2 < 0f)
				{
					num2 = 0f;
				}
				if (num2 > 1f)
				{
					num2 = 1f;
				}
				DrawBar(graphics, ref y, buffItem.Name, num2, FormatDuration(buffItem.Remaining));
			}
		}
		using Pen pen = new Pen(BorderColor);
		graphics.DrawRectangle(pen, 0, 0, width - 1, base.ClientSize.Height - 1);
	}

	private static string FormatDuration(double seconds)
	{
		if (!(seconds >= 60.0))
		{
			if (!(seconds >= 10.0))
			{
				return $"{seconds:F1}s";
			}
			return $"{seconds:F0}s";
		}
		return $"{seconds / 60.0:F0}m{seconds % 60.0:F0}s";
	}

	private void DrawBar(Graphics g, ref int y, string label, float progress, string rightText)
	{
		int num = 8;
		int num2 = base.ClientSize.Width - num - 8;
		using (SolidBrush brush = new SolidBrush(TextColor))
		{
			g.DrawString(label, SmallFont, brush, num, y);
		}
		if (!string.IsNullOrEmpty(rightText))
		{
			using SolidBrush brush2 = new SolidBrush(DimTextColor);
			g.DrawString(rightText, SmallFont, brush2, base.ClientSize.Width - 8, y, RightAlign);
		}
		int num3 = y + 20 + 6;
		using (SolidBrush brush3 = new SolidBrush(BarBgColor))
		{
			g.FillRectangle(brush3, num, num3, num2, 12);
		}
		if (progress > 0f)
		{
			int width = (int)((float)num2 * progress);
			using SolidBrush brush4 = new SolidBrush(BarFillColor);
			g.FillRectangle(brush4, num, num3, width, 12);
		}
		using (Pen pen = new Pen(BorderColor))
		{
			g.DrawRectangle(pen, num, num3, num2, 12);
		}
		y = num3 + 12 + 4;
	}

	private void DrawSection(Graphics g, string text, ref int y)
	{
		using (SolidBrush brush = new SolidBrush(DimTextColor))
		{
			g.DrawString(text, HeaderFont, brush, 8f, y);
		}
		y += 26;
	}

	private void DrawLine(Graphics g, ref int y, string left, Color leftColor, string right)
	{
		using (SolidBrush brush = new SolidBrush(leftColor))
		{
			g.DrawString(left, MainFont, brush, 12f, y, LeftAlign);
		}
		if (right != null)
		{
			using SolidBrush brush2 = new SolidBrush(DimTextColor);
			g.DrawString(right, SmallFont, brush2, base.ClientSize.Width - 8 - 4, y, RightAlign);
		}
		y += 24;
	}

	public void Shutdown()
	{
		_disposed = true;
		_updateTimer?.Stop();
		_updateTimer?.Dispose();
		_updateTimer = null;
		if (base.IsHandleCreated)
		{
			BeginInvoke(delegate
			{
				Close();
			});
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && !_disposed)
		{
			Shutdown();
		}
		base.Dispose(disposing);
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
internal static class NativeMethods
{
	private struct INPUT
	{
		public uint type;

		public InputUnion u;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)]
		public MOUSEINPUT mi;

		[FieldOffset(0)]
		public KEYBDINPUT ki;

		[FieldOffset(0)]
		public HARDWAREINPUT hi;
	}

	private struct KEYBDINPUT
	{
		public ushort wVk;

		public ushort wScan;

		public uint dwFlags;

		public uint time;

		public nint dwExtraInfo;
	}

	private struct MOUSEINPUT
	{
		public int dx;

		public int dy;

		public uint mouseData;

		public uint dwFlags;

		public uint time;

		public nint dwExtraInfo;
	}

	private struct HARDWAREINPUT
	{
		public uint uMsg;

		public ushort wParamL;

		public ushort wParamH;
	}

	private const uint INPUT_KEYBOARD = 1u;

	private const uint KEYEVENTF_KEYDOWN = 0u;

	private const uint KEYEVENTF_KEYUP = 2u;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

	public static void SendKeyPress(ushort virtualKey)
	{
		SendInput(2u, new INPUT[2]
		{
			new INPUT
			{
				type = 1u,
				u = new InputUnion
				{
					ki = new KEYBDINPUT
					{
						wVk = virtualKey,
						wScan = 0,
						dwFlags = 0u,
						time = 0u,
						dwExtraInfo = IntPtr.Zero
					}
				}
			},
			new INPUT
			{
				type = 1u,
				u = new InputUnion
				{
					ki = new KEYBDINPUT
					{
						wVk = virtualKey,
						wScan = 0,
						dwFlags = 2u,
						time = 0u,
						dwExtraInfo = IntPtr.Zero
					}
				}
			}
		}, Marshal.SizeOf<INPUT>());
	}

	[DllImport("user32.dll")]
	public static extern short GetAsyncKeyState(int vKey);
}
public class PluginUI : IPluginUI, IDisposable
{
	private readonly RotationHelperPlugin _plugin;

	internal Form _mainForm;

	private Bitmap _toolbarIcon;

	private bool _disposed;

	internal const string Version = "4";

	private const int SetupW = 55;

	private const int VerW = 22;

	private const int PauseW = 60;

	private const int DistW = 55;

	private const int FormW = 260;

	private const int FormH = 28;

	private const int IconSize = 32;

	private Label _setupLabel;

	private Label _distLabel;

	private Label _dmgLabel;

	private Label _pauseLabel;

	private Label _versionLabel;

	private System.Windows.Forms.Timer _uiTimer;

	private int _logCount;

	private static readonly Color BgDark = Color.FromArgb(25, 25, 25);

	public float? FocusedOpacity => 0.9f;

	public bool EnabledInCharacterSelection => false;

	public Image ToolbarImage => _toolbarIcon;

	public object UserInterfaceForm => _mainForm;

	public Tuple<int, int> MinSize => Tuple.Create(260, 28);

	public PluginUI(RotationHelperPlugin plugin)
	{
		_plugin = plugin;
		CreateToolbarIcon();
		BuildForm();
	}

	private void CreateToolbarIcon()
	{
		_toolbarIcon = new Bitmap(32, 32);
		using Graphics graphics = Graphics.FromImage(_toolbarIcon);
		graphics.Clear(Color.Black);
		using SolidBrush brush = new SolidBrush(Color.White);
		using System.Drawing.Font font = new System.Drawing.Font("Segoe UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
		SizeF sizeF = graphics.MeasureString("RH", font);
		graphics.DrawString("RH", font, brush, (32f - sizeF.Width) / 2f, (32f - sizeF.Height) / 2f);
	}

	private void BuildForm()
	{
		_mainForm = new Form
		{
			FormBorderStyle = FormBorderStyle.None,
			ShowInTaskbar = false,
			TopMost = true,
			BackColor = BgDark,
			TransparencyKey = BgDark,
			Width = 194,
			Height = 28,
			Location = new Point(100, 100),
			Text = "RotationHelper"
		};
		_setupLabel = new Label
		{
			Text = "Setup",
			Size = new Size(56, 30),
			Location = new Point(-1, -1),
			BackColor = Color.Transparent,
			ForeColor = Color.White,
			Font = new System.Drawing.Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel),
			TextAlign = ContentAlignment.MiddleCenter,
			Cursor = Cursors.Hand,
			BorderStyle = BorderStyle.None,
			Tag = "Setup"
		};
		_setupLabel.Click += delegate
		{
			_plugin?.OpenSetupSkills();
		};
		_mainForm.Controls.Add(_setupLabel);
		_pauseLabel = new Label
		{
			Text = "Pause",
			Size = new Size(61, 30),
			Location = new Point(77, -1),
			BackColor = Color.Transparent,
			ForeColor = Color.White,
			Font = new System.Drawing.Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel),
			TextAlign = ContentAlignment.MiddleCenter,
			Cursor = Cursors.Hand,
			BorderStyle = BorderStyle.None,
			Tag = "Pause"
		};
		_pauseLabel.Click += delegate
		{
			_plugin?.TogglePause();
		};
		_mainForm.Controls.Add(_pauseLabel);
		_distLabel = new Label
		{
			Text = "",
			Size = new Size(56, 30),
			Location = new Point(137, -1),
			BackColor = Color.Transparent,
			ForeColor = Color.FromArgb(180, 180, 180),
			Font = new System.Drawing.Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel),
			TextAlign = ContentAlignment.MiddleCenter,
			BorderStyle = BorderStyle.None,
			Tag = "Dist"
		};
		_mainForm.Controls.Add(_distLabel);
		_dmgLabel = new Label
		{
			Text = "",
			Size = new Size(66, 30),
			Location = new Point(193, -1),
			BackColor = Color.Transparent,
			ForeColor = Color.FromArgb(180, 180, 180),
			Font = new System.Drawing.Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel),
			TextAlign = ContentAlignment.MiddleCenter,
			BorderStyle = BorderStyle.None,
			Tag = "Dmg"
		};
		_mainForm.Controls.Add(_dmgLabel);
		_versionLabel = new Label
		{
			Text = "v4",
			Size = new Size(23, 30),
			Location = new Point(55, -1),
			BackColor = Color.Transparent,
			ForeColor = Color.FromArgb(160, 160, 160),
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel),
			TextAlign = ContentAlignment.MiddleCenter,
			BorderStyle = BorderStyle.None,
			Tag = "Ver"
		};
		_mainForm.Controls.Add(_versionLabel);
		_uiTimer = new System.Windows.Forms.Timer
		{
			Interval = 200
		};
		_uiTimer.Tick += delegate
		{
			UpdateDistanceDisplay();
		};
		_uiTimer.Start();
		_mainForm.Move += delegate
		{
			if (_plugin?._config != null)
			{
				_plugin._config.FormX = _mainForm.Location.X;
				_plugin._config.FormY = _mainForm.Location.Y;
			}
		};
	}

		private void UpdateDistanceDisplay()
	{
		if (_mainForm == null || _mainForm.IsDisposed || _plugin == null)
		{
			return;
		}
		try
		{
			if (_mainForm.InvokeRequired)
			{
				_mainForm.BeginInvoke(UpdateDistanceDisplay);
				return;
			}
			double? distanceToTarget = _plugin.GetDistanceToTarget();
			if (distanceToTarget.HasValue)
			{
				_distLabel.Text = $"{distanceToTarget.Value:F1}u";
			}
			else
			{
				_distLabel.Text = "";
			}
			if (_plugin._config.DamageMeterEnabled)
			{
				FormatDamageText();
			}
			else
			{
				_dmgLabel.Text = "";
			}
		}
		catch
		{
		}
	}

	private void FormatDamageText()
	{
		long dealt = _plugin._totalDamageDealt;
		long taken = _plugin._totalDamageTaken;
		if (dealt > 0)
		{
			double elapsed = (DateTime.UtcNow - _plugin._combatStartTime).TotalSeconds;
			double dps = elapsed > 1.0 ? dealt / elapsed : 0.0;
			_dmgLabel.Text = $"{FormatNumber(dealt)}d {dps:F0}";
		}
		else
		{
			_dmgLabel.Text = taken > 0 ? $"{FormatNumber(taken)}t" : "";
		}
	}

	private static string FormatNumber(long n)
	{
		if (n >= 1000000)
			return $"{(double)n / 1000000:F1}M";
		if (n >= 1000)
			return $"{(double)n / 1000:F0}K";
		return n.ToString();
	}

	internal void UpdatePauseStatus(bool paused)
	{
		if (_mainForm == null || _mainForm.IsDisposed)
		{
			return;
		}
		try
		{
			if (_mainForm.InvokeRequired)
			{
				_mainForm.BeginInvoke(delegate
				{
					UpdatePauseStatus(paused);
				});
				return;
			}
			_setupLabel.Text = (paused ? "PAUSED" : "Setup");
			_setupLabel.ForeColor = (paused ? Color.Red : Color.White);
			_pauseLabel.Text = (paused ? "Resume" : "Pause");
			_pauseLabel.ForeColor = (paused ? Color.Red : Color.White);
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_uiTimer?.Stop();
			_uiTimer?.Dispose();
			_toolbarIcon?.Dispose();
			_mainForm?.Close();
			_mainForm?.Dispose();
		}
	}
}
public class RotationAbility
{
	[JsonIgnore]
	internal int CachedHotbarIndex { get; set; } = -1;

	public string Name { get; set; }

	public string Category { get; set; }

	public int VirtualKey { get; set; }

	public string KeyLabel { get; set; }

	public uint? CommandValue { get; set; }

	public uint? ReferenceDid { get; set; }

	public bool Autocast { get; set; } = false;

	public bool BuffOnMissing { get; set; } = false;

	public long CooldownMs { get; set; } = 0L;

	public long BuffDurationMs { get; set; } = 0L;

	public int DurationMinutes { get; set; } = 0;

	public int DurationSeconds { get; set; } = 0;

	public DateTime LastFired { get; set; } = DateTime.MinValue;

	public uint CooldownTimerDid1 { get; set; }

	public uint CooldownTimerDid2 { get; set; }

	public uint CooldownTimerDid3 { get; set; }

	public int PartyHpThreshold { get; set; } = 0;

	public int PartyCountThreshold { get; set; } = 0;

	public string HealScope { get; set; } = "Both";

	public uint ToggleStatePropertyDid { get; set; }

	public string ToggleExpectedValue { get; set; } = "1";

	public bool RequiresTarget { get; set; } = false;

	public string TargetType { get; set; } = "Myself";

	public int MinNearbyEnemies { get; set; } = 0;

	public int TargetHpThreshold { get; set; } = 0;

	public int MinSpThreshold { get; set; } = 0;

	public int Priority { get; set; } = 0;

	public uint RequiredBuffDid { get; set; }

	public uint RequiredDebuffOnTargetDid { get; set; }

	public uint RequiredEnhancementDid { get; set; }
}
public class RotationConfig
{
	public int TriggerKey { get; set; } = 2;

	public int TriggerKey2 { get; set; } = 0;

	public int BuffTriggerKey { get; set; } = 0;

	public int StopKey { get; set; } = 27;

	public int PressDelayMs { get; set; } = 200;

	public int BuffPressDelayMs { get; set; } = 200;

	public int PauseToggleKey { get; set; } = 0;

	public int MinFireIntervalMs { get; set; } = 300;

	public bool AutocastRotationOnTarget { get; set; } = false;

	public bool MeleeRangeEnabled { get; set; } = false;

	public bool BuffDisablePublic { get; set; } = false;

	public bool EmergencyPartyHeal { get; set; } = false;

	public double HealRange { get; set; } = 40.0;

	public bool SkipWhileMounted { get; set; } = true;

	public bool BuffDisableWilderness { get; set; } = false;

	public Dictionary<string, bool> ReloadToggleStates { get; set; } = new Dictionary<string, bool>();

	public Dictionary<string, int> ToggleKeyBindings { get; set; } = new Dictionary<string, int>();

	public bool SmartOrder { get; set; } = false;

	public bool AutoAttackEnabled { get; set; } = false;

	public int AutoAttackCheckIntervalMs { get; set; } = 2000;

	public bool HudEnabled { get; set; } = false;

	public int HudX { get; set; } = -1;

	public int HudY { get; set; } = -1;

	public bool HudShowPaused { get; set; } = true;

	public bool HudShowTarget { get; set; } = true;

	public bool HudShowCooldowns { get; set; } = true;

	public bool HudShowToggles { get; set; } = true;

	public bool HudShowBuffs { get; set; } = true;

	public bool AutoTargetFurthestEnemy { get; set; } = false;

	public bool AutoTargetClosestEnemy { get; set; } = false;

	public float AutoTargetMaxRange { get; set; } = 200f;

	public bool DamageMeterEnabled { get; set; } = false;

	public List<uint> EnabledZoneDids { get; set; } = new List<uint>();

	public string CharacterName { get; set; } = null;

	public int FormX { get; set; } = -1;

	public int FormY { get; set; } = -1;

	public int FormWidth { get; set; } = 0;

	public int FormHeight { get; set; } = 0;

	public static RotationConfig Load(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				string json = File.ReadAllText(path);
				return JsonSerializer.Deserialize<RotationConfig>(json) ?? new RotationConfig();
			}
		}
		catch
		{
		}
		return new RotationConfig();
	}

	public void Save(string path)
	{
		try
		{
			string contents = JsonSerializer.Serialize(this, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			File.WriteAllText(path, contents);
		}
		catch
		{
		}
	}
}
public class RotationHelperPlugin : IPlugin, IDdoPlugin
{
	private static readonly Guid PluginGuid = new Guid("B1C2D3E4-F5A6-7890-1234-56789ABCDEF0");

	internal const int VK_BOTH_MOUSE = 1073741823;

	internal static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RotationHelper");

	internal IDdoGameDataProvider _gameDataProvider;

	private volatile bool _progressActive;

	internal PluginUI _pluginUI;

	internal volatile RotationConfig _config;

	internal string _pluginFolder;

	internal bool _isRunning;

	internal bool _buffIsRunning;

	private CancellationTokenSource _rotationCts;

	private bool _triggerWasHeld;

	private CancellationTokenSource _buffCts;

	private bool _buffTriggerWasHeld;

	private bool _paused;

	private bool _pauseWasHeld;

	private object _lock = new object();

	private System.Threading.Timer _pollTimer;

	private System.Threading.Timer _autocastTimer;

	private System.Threading.Timer _refreshTimer;

	private bool _autocastTickRunning;

	internal GameDataCache GameCache = new GameDataCache();

	private DateTime _lastFireTime = DateTime.MinValue;

	private int _rotationIndex;

	private DateTime _lastAutoAttackCheck = DateTime.MinValue;

	private DateTime _lastManualAutoAttackToggle = DateTime.MinValue;

	private DateTime? _lastAutoAttackToggleTime;

	private IHotbarSlot _cachedAttackSlot;

	private int _cachedAttackSlotIndex = -1;

	private bool _autoAttackWasFiredThisZone;

	private DateTime _lastAutoTargetCheck = DateTime.MinValue;

	private ulong? _pendingEmergencyTargetId;

	private RotationAbility _pendingEmergencyAbility;

	private ulong? _pendingTargetRevertId;

	private DateTime _pendingTargetRevertTime = DateTime.MinValue;

	private ulong? _pendingBuffTargetId;

	private RotationAbility _pendingBuffAbility;

	private ulong? _pendingBuffRevertId;

	private SetupSkillsForm _setupForm;

	private static readonly HashSet<uint> _enrichedDid = new HashSet<uint>();

	private static readonly object _enrichLock = new object();

	private int _autoBuffIndex;

	private DateTime _lastBuffFireTime = DateTime.MinValue;

	private int _logCount;

	internal List<RotationAbility> Rotation = new List<RotationAbility>();

	internal List<RotationAbility> BuffOrder = new List<RotationAbility>();

	internal List<RotationAbility> Emergency = new List<RotationAbility>();

	internal List<RotationAbility> ToggleBuffs = new List<RotationAbility>();

	internal readonly HashSet<uint> _sessionToggleFired = new HashSet<uint>();

	internal DateTime _lastHotbarUsageTime = DateTime.MinValue;

	internal DateTime _lastMapChangeTime = DateTime.MinValue;

	internal DateTime? _lastSuccessfulTick = null;

	internal readonly Dictionary<uint, string> _didNameCache = new Dictionary<uint, string>();

	internal readonly Dictionary<string, uint> _nameCmdMap = new Dictionary<string, uint>();

	internal HudOverlayForm _hudForm;

	private System.Threading.Timer _hudInitTimer;

	private readonly Dictionary<string, DateTime> _lastPartyBuffTime = new Dictionary<string, DateTime>();

	private ulong? _preAttackTargetId;
	private int? _preAttackTargetHp;
	internal long _totalDamageDealt;
	internal long _totalDamageTaken;
	private int _damageSampleCount;
	internal DateTime _combatStartTime = DateTime.MinValue;

	internal readonly HashSet<uint> _knownZoneDids = new HashSet<uint>();

	private static readonly Dictionary<string, (uint Did, string ExpectedValue)> ToggleStatePropertyMap = new Dictionary<string, (uint, string)>(StringComparer.OrdinalIgnoreCase)
	{
		{
			"attack",
			(268484148u, "1")
		},
		{
			"power attack",
			(268442956u, "1")
		},
		{
			"combat expertise",
			(268442960u, "1")
		},
		{
			"rune arm use",
			(268450261u, "1")
		},
		{
			"archer's focus",
			(268450900u, "1")
		},
		{
			"aura of courage",
			(268450920u, "1")
		},
		{
			"aura of menace",
			(268450905u, "1")
		},
		{
			"heavy draw",
			(268450261u, "1")
		},
		{
			"displacement",
			(268467632u, "1")
		},
		{
			"meditation of war",
			(268467080u, "1")
		}
	};

	private const string DatApiBase = "http://localhost:5138";

	internal static readonly string[] ReloadToggleNames = new string[80]
	{
		"AutoAttack", "RuneArmUse", "UseFlameGeneration", "ArchersFocus", "CelestialSpirit", "AuraOfCourage", "AuraOfMenace", "ChannelTheAbyss", "ShiningThrough", "ImprovedPreciseShot",
		"ThornBlade", "ThornBladeDualWield", "VistaniFortune", "HitTheStreets", "DiplomaticImmunity", "LesserDeathAura", "DeathAura", "EmbraceTheVoid", "HeavyDraw", "KnightsTransformation",
		"PowerInChaos", "WeaponAttachment", "ShieldConstruct", "WardingConstruct", "RegenerationConstruct", "ChildOfSummer", "ElderOfWinter", "AutumnWinds", "Displacement", "MeditationOfWar",
		"Swashbuckling", "BardicInspiration", "SkaldicRage", "Boast", "DivineEnergyResistance", "ShadowShrouding", "WardOfShadow", "AmaunatorsFlames", "BlessingOfSilvanus", "PositiveEnergyAura",
		"Martyrdom", "ReactiveHealing", "DivineIntervention", "ShieldOfCondemnation", "AscendancyShineBright", "ArchonsFury", "FvSAuraOfMenace", "SummonArchon", "EternalDefender", "Frenzy",
		"DeathFrenzy", "BloodTribute", "StormsEye", "ImbueWithShadow", "SpiderCultMask", "FocusingChant", "EverythingIsNothing", "AskQuestionsFirst", "RazorclawRage", "BeasthideRage",
		"WildhuntRage", "RageOfTheBeast", "BarbariansRage", "EmbraceDeath", "PrimalScream", "MagicFang", "HarrowingPack", "RisingFury", "ElementalToughness", "Roar",
		"AnimalGrowth", "TenaciousPack", "UnyieldingPack", "ShiftingPack", "BodyOfTheSun", "HasteSpells", "HasteClickies", "HastePots", "HasteScrolls", "EnableEquipmentControl"
	};

	private static uint GameTimeDid = 268443087u;

	private static readonly Dictionary<string, long> BuffDurationTable = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
	{
		{ "Personal Augmentation: Insightful Damage", 1140000L },
		{ "Greater Elemental Weapons", 1140000L },
		{ "Armor of Speed", 288000L },
		{ "Ram's Might", 1140000L },
		{ "Inexorable Advance", 30000L },
		{ "Know the Angles", 60000L },
		{ "Memento: Reaper's Strike", 30000L },
		{ "Greater Heroism", 1200000L },
		{ "Mass Freedom of Movement", 1200000L },
		{ "Death Ward", 1200000L },
		{ "Protection from Evil", 60000L },
		{ "Protection from Energy", 60000L },
		{ "Resist Energy", 60000L },
		{ "Haste", 30000L },
		{ "Blur", 300000L },
		{ "Displacement", 30000L },
		{ "Invisibility", 300000L },
		{ "Greater Invisibility", 30000L },
		{ "Shield", 30000L },
		{ "Nightshield", 30000L },
		{ "Jump", 300000L },
		{ "Tumble", 300000L },
		{ "Expeditious Retreat", 30000L },
		{ "Longstrider", 3600000L },
		{ "Merfolk's Blessing", 3600000L },
		{ "Water Breathing", 3600000L },
		{ "Rage", 30000L },
		{ "Haste Boost", 20000L },
		{ "Power Attack", 300000L },
		{ "Combat Expertise", 60000L },
		{ "Stoneskin", 600000L },
		{ "Barkskin", 600000L },
		{ "Mage Armor", 3600000L },
		{ "Ghostly", 300000L },
		{ "True Seeing", 300000L },
		{ "Freedom of Movement", 60000L },
		{ "Spell Resistance", 300000L },
		{ "Remove Curse", 30000L },
		{ "Remove Disease", 30000L },
		{ "Neutralize Poison", 30000L },
		{ "Bardic Inspiration", 600000L },
		{ "Heroism", 600000L },
		{ "Good Hope", 60000L },
		{ "Prayer", 60000L },
		{ "Recitation", 60000L },
		{ "Aid", 60000L },
		{ "Bless", 600000L },
		{ "Favor of the Aurum", 3600000L },
		{ "Alchemical Intellect", 60000L },
		{ "Alchemical Wisdom", 60000L },
		{ "Owl's Wisdom", 60000L },
		{ "Eagle's Splendor", 60000L },
		{ "Bear's Endurance", 60000L },
		{ "Bull's Strength", 60000L },
		{ "Cat's Grace", 60000L },
		{ "Fox's Cunning", 60000L },
		{ "Mass Haste", 30000L },
		{ "Mass Invisibility", 30000L },
		{ "Mass Death Ward", 1200000L },
		{ "Mass Protection from Evil", 60000L },
		{ "Mass Protection from Energy", 60000L },
		{ "Mass Resist Energy", 60000L },
		{ "Negative Energy Barrier", 30000L },
		{ "Lesser Death Aura", 30000L },
		{ "Death Aura", 30000L },
		{ "Fire Shield", 30000L },
		{ "Protection from Acid", 60000L },
		{ "Protection from Cold", 60000L },
		{ "Protection from Electricity", 60000L },
		{ "Protection from Fire", 60000L },
		{ "Protection from Sonic", 60000L },
		{ "Resist Acid", 60000L },
		{ "Resist Cold", 60000L },
		{ "Resist Electricity", 60000L },
		{ "Resist Fire", 60000L },
		{ "Resist Sonic", 60000L },
		{ "Convalescence", 60000L },
		{ "False Life", 3600000L },
		{ "Lesser Death Ward", 1200000L }
	};

	private readonly Dictionary<uint, (double remainingSec, DateTime readTime, double gameTimeAtRead)> _cooldownCache = new Dictionary<uint, (double, DateTime, double)>();

	private readonly object _cacheLock = new object();

	private static readonly object _logLock = new object();

	private int _logCallCount;

	internal BuffManager BuffManager { get; private set; }

	public IDdoGameDataProvider GameDataProvider => _gameDataProvider;

	public bool IsRunning => _isRunning;

	public bool BuffIsRunning => _buffIsRunning;

	internal bool IsPaused => _paused;

	public Guid PluginId => PluginGuid;

	public GameId Game => GameId.DDO;

	public string PluginKey => "RotationHelper";

	public string Name => "Rotation Helper";

	public string Description => "Setup skill rotations and cycle through them while holding a key";

	public string Author => "Rotation Helper";

	public Version Version => new Version(1, 0, 0, 0);

	private static string PartyBuffKey(uint refDid, ulong memberId, string abilityName)
	{
		return $"{refDid}|{memberId}|{abilityName}";
	}

	internal static bool IsKeyDown(int vk)
	{
		bool flag = false;
		if (1 == 0)
		{
		}
		bool flag2 = vk switch
		{
			1073741823 => (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0 && (NativeMethods.GetAsyncKeyState(2) & 0x8000) != 0, 
			0 => false, 
			_ => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0, 
		};
		if (1 == 0)
		{
		}
		bool result = flag2;
		bool flag3 = false;
		return result;
	}

	private bool IsHostileOrBreakable(IGameDataProvider gdp)
	{
		IEntity selectedObject = gdp.GetSelectedObject();
		if (selectedObject == null)
		{
			return false;
		}
		if (selectedObject.InstanceId == gdp.GetCurrentCharacterId().GetValueOrDefault())
		{
			return false;
		}
		IProperty property = selectedObject.GetProperty(268437503u);
		if (property?.Value != null)
		{
			try
			{
				if (Convert.ToBoolean(property.Value))
				{
					return true;
				}
			}
			catch
			{
			}
		}
		IProperty property2 = selectedObject.GetProperty(268435581u);
		if (property2?.Value != null)
		{
			try
			{
				if (Convert.ToInt32(property2.Value) > 0)
				{
					return true;
				}
				return false;
			}
			catch
			{
			}
		}
		uint? num = selectedObject.WeeniePropertyCollection?.GetEnumProperty(287u)?.UInt32Value;
		if (num == 1245313)
		{
			return true;
		}
		if (num.HasValue)
		{
			switch ((WeenieType)num.Value)
			{
			case WeenieType.Trap:
			case WeenieType.Chest:
			case WeenieType.Door:
			case WeenieType.Collectable:
				return false;
			}
		}
		if (!selectedObject.HitPoints_Max.HasValue || selectedObject.HitPoints_Max.Value <= 0)
		{
			return false;
		}
		IReadOnlyList<IPartyMember> readOnlyList = (gdp as IDdoGameDataProvider)?.GetPartyMembers();
		if (readOnlyList != null && !string.IsNullOrEmpty(selectedObject.Name))
		{
			foreach (IPartyMember item in readOnlyList)
			{
				if (item.IsValid() && item.Connected && string.Equals(item.Name, selectedObject.Name, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}
		}
		return true;
	}

	internal bool HasEnhancement(uint did)
	{
		try
		{
			var trees = _gameDataProvider?.Enhancements;
			if (trees != null)
				foreach (var t in trees)
					if (t.SelectedEnhancements.Contains(did))
						return true;
			var dests = _gameDataProvider?.DestinyTrees;
			if (dests != null)
				foreach (var d in dests)
					if (d.SelectedEnhancements.Contains(did))
						return true;
			var reaps = _gameDataProvider?.ReaperTrees;
			if (reaps != null)
				foreach (var r in reaps)
					if (r.SelectedEnhancements.Contains(did))
						return true;
		}
		catch { }
		return false;
	}

	internal void EnrichFromApi(RotationAbility ability)
	{
		if (!ability.ReferenceDid.HasValue || ability.ReferenceDid.Value == 0)
		{
			return;
		}
		uint value = ability.ReferenceDid.Value;
		lock (_enrichLock)
		{
			if (_enrichedDid.Contains(value))
			{
				return;
			}
		}
		try
		{
			IPropertyMaster propertyMaster = _gameDataProvider?.PropertyMaster;
			if (propertyMaster == null)
			{
				return;
			}
			IPropertyCollection propertyCollection = propertyMaster.GetPropertyCollection(value);
			if (propertyCollection == null)
			{
				return;
			}
			double? num = null;
			IProperty property = propertyCollection.GetProperty(268443083u);
			if (property is IFloatProperty { FloatValue: var floatValue } floatProperty && floatValue.GetValueOrDefault() > 0f)
			{
				num = floatProperty.FloatValue;
			}
			IProperty property2 = propertyCollection.GetProperty(268443084u);
			if (property2 is IFloatProperty { FloatValue: var floatValue2 } floatProperty2 && floatValue2.GetValueOrDefault() > 0f && (!num.HasValue || (double?)floatProperty2.FloatValue > num.Value))
			{
				num = floatProperty2.FloatValue;
			}
			IProperty property3 = propertyCollection.GetProperty(268443085u);
			if (property3 is IFloatProperty { FloatValue: var floatValue3 } floatProperty3 && floatValue3.GetValueOrDefault() > 0f && (!num.HasValue || (double?)floatProperty3.FloatValue > num.Value))
			{
				num = floatProperty3.FloatValue;
			}
			if (num.HasValue && num.Value > 0.0 && ability.CooldownMs <= 0)
			{
				ability.CooldownMs = (long)(num.Value * 1000.0);
				Log($"SDK: set cooldown [{ability.Name}] = {num.Value}s");
			}
			lock (_enrichLock)
			{
				_enrichedDid.Add(value);
			}
		}
		catch (Exception ex)
		{
			Log($"SDK enrichment error for [{ability.Name}] (0x{value:X8}): {ex.Message}");
		}
	}

	internal async Task EnrichAbilityFromDatApiAsync(RotationAbility ability)
	{
		if (string.IsNullOrEmpty(ability.Name))
			return;
		try
		{
			SearchResult match = await GameCache.SearchBestMatchAsync(ability.Name).ConfigureAwait(false);
			if (match == null || match.Id <= 0)
			{
				Log($"DatAPI: no match for [{ability.Name}]");
				return;
			}
			uint did = (uint)match.Id;
			if (!ability.ReferenceDid.HasValue || ability.ReferenceDid.Value == 0)
			{
				ability.ReferenceDid = did;
				Log($"DatAPI: set ReferenceDid 0x{did:X8} for [{ability.Name}]");
			}
			HttpResponseMessage resp = await new HttpClient { BaseAddress = new Uri(DatApiBase), Timeout = TimeSpan.FromSeconds(3) }.GetAsync($"/DbProperties/0x{did:X8}").ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
				return;
			using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
			if (!doc.RootElement.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
				return;
			double? cooldown = null;
			List<double> cooldownCandidates = new List<double>();
			foreach (var kv in props.EnumerateObject())
			{
				uint propId;
				if (!uint.TryParse(kv.Name, System.Globalization.NumberStyles.HexNumber, null, out propId))
					continue;
				if (propId == 268443083u || propId == 268443084u || propId == 268443085u)
				{
					if (kv.Value.TryGetProperty("floatValue", out var fv) && fv.ValueKind == JsonValueKind.Number && fv.TryGetDouble(out var cd) && cd > 0)
						cooldownCandidates.Add(cd);
				}
				else if (propId == 268442188u)
				{
					if (kv.Value.TryGetProperty("values", out var targets) && targets.ValueKind == JsonValueKind.Array)
					{
						List<string> types = new List<string>();
						foreach (var t in targets.EnumerateArray())
						{
							if (t.TryGetProperty("value", out var tv) && tv.ValueKind == JsonValueKind.String)
								types.Add(tv.GetString());
						}
						if (types.Count > 0)
							ability.TargetType = string.Join(",", types);
					}
				}
			}
			if (cooldownCandidates.Count > 0)
			{
				cooldown = cooldownCandidates.Max();
				if (cooldown.Value > 0.0 && ability.CooldownMs <= 0)
				{
					ability.CooldownMs = (long)(cooldown.Value * 1000.0);
					Log($"DatAPI: set cooldown [{ability.Name}] = {cooldown.Value}s");
				}
			}
		}
		catch (Exception ex)
		{
			Log($"DatAPI enrichment error for [{ability.Name}]: {ex.Message}");
		}
	}

	internal void DiscoverToggleProperty(RotationAbility ability)
	{
		if (string.IsNullOrEmpty(ability.Name))
		{
			return;
		}
		string key = ability.Name.Trim().ToLowerInvariant();
		if (ToggleStatePropertyMap.TryGetValue(key, out (uint, string) value))
		{
			(ability.ToggleStatePropertyDid, ability.ToggleExpectedValue) = value;
			Log($"Toggle prop: [{ability.Name}] = 0x{value.Item1:X8} (static map)");
		}
		else
		{
			if (ability.ToggleStatePropertyDid != 0)
			{
				return;
			}
			try
			{
				uint valueOrDefault = ability.ReferenceDid.GetValueOrDefault();
				if (valueOrDefault == 0)
				{
					return;
				}
				IPropertyMaster propertyMaster = _gameDataProvider?.PropertyMaster;
				if (propertyMaster == null)
				{
					return;
				}
				IPropertyCollection propertyCollection = propertyMaster.GetPropertyCollection(valueOrDefault);
				if (propertyCollection != null)
				{
					IProperty property = propertyCollection.GetProperty(268442951u);
					if (property is IUInt32Property { UInt32Value: { } uInt32Value } && uInt32Value != 0)
					{
						ability.ToggleStatePropertyDid = uInt32Value;
						ability.ToggleExpectedValue = "1";
						Log($"Toggle prop: [{ability.Name}] = 0x{uInt32Value:X8} (auto-discovered)");
					}
				}
			}
			catch
			{
			}
		}
	}

	internal void ClearToggleCache()
	{
		_sessionToggleFired.Clear();
		_cachedAttackSlot = null;
		_cachedAttackSlotIndex = -1;
		_autoAttackWasFiredThisZone = false;
		foreach (RotationAbility toggleBuff in ToggleBuffs)
		{
			toggleBuff.CachedHotbarIndex = -1;
		}
	}

	internal void ReloadToggleBuffStatesFromConfig()
	{
		if (_config?.ReloadToggleStates == null || _config.ReloadToggleStates.Count == 0)
			return;
		bool changed = false;
		foreach (var kvp in _config.ReloadToggleStates)
		{
			if (!kvp.Value)
				continue;
			string name = kvp.Key;
			if (ToggleBuffs.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
				continue;
			RotationAbility ab = new RotationAbility
			{
				Name = name,
				KeyLabel = name,
				Autocast = true
			};
			DetectToggleReferenceDid(ab);
			if (ab.ReferenceDid.HasValue && ab.ReferenceDid.Value != 0)
			{
				ToggleBuffs.Add(ab);
				Log($"Toggle auto-migration: added [{name}] from ReloadToggleStates — DID=0x{ab.ReferenceDid.Value:X8}");
				changed = true;
			}
			else
			{
				Log($"Toggle auto-migration: [{name}] had no ReferenceDid (not on hotbar), skipping");
			}
		}
		if (changed)
			SaveToggleBuffs();
	}

	internal void DetectToggleReferenceDid(RotationAbility ability)
	{
		if (string.IsNullOrEmpty(ability.Name) || (ability.ReferenceDid.HasValue && ability.ReferenceDid != 0))
		{
			return;
		}
		try
		{
			IHotbarSlot[] array = _gameDataProvider?.GetHotbarMap();
			if (array == null)
			{
				return;
			}
			string text = ability.Name.Trim().ToLowerInvariant();
			IHotbarSlot[] array2 = array;
			foreach (IHotbarSlot hotbarSlot in array2)
			{
				if (hotbarSlot == null || hotbarSlot.ReferenceDid == 0)
				{
					continue;
				}
				IPropertyMaster propertyMaster = _gameDataProvider?.PropertyMaster;
				if (propertyMaster == null)
				{
					continue;
				}
				IPropertyCollection propertyCollection = propertyMaster.GetPropertyCollection(hotbarSlot.ReferenceDid);
				if (propertyCollection == null)
				{
					continue;
				}
				IStringProperty stringProperty = propertyCollection.GetStringProperty(1u);
				if (stringProperty != null && !string.IsNullOrEmpty(stringProperty.StringValue))
				{
					string text2 = stringProperty.StringValue.Trim().ToLowerInvariant();
					if (text2 == text)
					{
						ability.ReferenceDid = hotbarSlot.ReferenceDid;
						Log($"Toggle slot: [{ability.Name}] found at Bar{hotbarSlot.Bar + 1} Slot{hotbarSlot.Slot + 1} — ReferenceDid=0x{hotbarSlot.ReferenceDid:X8}");
						return;
					}
				}
			}
			Log("Toggle slot: [" + ability.Name + "] not found on hotbar");
			try
			{
				SearchResult match = GameCache.SearchBestMatchAsync(ability.Name).GetAwaiter().GetResult();
				if (match != null && match.Id > 0)
				{
					ability.ReferenceDid = (uint)match.Id;
					Log($"Toggle slot: [{ability.Name}] found via DatAPI — DID=0x{match.Id:X8}");
				}
			}
			catch { }
		}
		catch (Exception ex)
		{
			Log("DetectToggleReferenceDid error: " + ex.Message);
		}
	}

	internal void DetectAllToggleReferenceDids()
	{
		foreach (RotationAbility toggleBuff in ToggleBuffs)
		{
			DetectToggleReferenceDid(toggleBuff);
		}
		SaveToggleBuffs();
	}

	internal void DiscoverTogglePropertyForAll()
	{
		foreach (RotationAbility toggleBuff in ToggleBuffs)
		{
			DiscoverToggleProperty(toggleBuff);
		}
		SaveToggleBuffs();
	}

	internal void EnrichHotbarFromApi(HotbarAbility hotbar)
	{
		if (hotbar.ReferenceDid == 0)
		{
			return;
		}
		uint referenceDid = hotbar.ReferenceDid;
		lock (_enrichLock)
		{
			if (_enrichedDid.Contains(referenceDid))
			{
				return;
			}
		}
		try
		{
			IPropertyMaster propertyMaster = _gameDataProvider?.PropertyMaster;
			if (propertyMaster == null)
			{
				return;
			}
			IPropertyCollection propertyCollection = propertyMaster.GetPropertyCollection(referenceDid);
			if (propertyCollection == null)
			{
				return;
			}
			double? num = null;
			IProperty property = propertyCollection.GetProperty(268443083u);
			if (property is IFloatProperty { FloatValue: var floatValue } floatProperty && floatValue.GetValueOrDefault() > 0f)
			{
				num = floatProperty.FloatValue;
			}
			IProperty property2 = propertyCollection.GetProperty(268443084u);
			if (property2 is IFloatProperty { FloatValue: var floatValue2 } floatProperty2 && floatValue2.GetValueOrDefault() > 0f && (!num.HasValue || (double?)floatProperty2.FloatValue > num.Value))
			{
				num = floatProperty2.FloatValue;
			}
			IProperty property3 = propertyCollection.GetProperty(268443085u);
			if (property3 is IFloatProperty { FloatValue: var floatValue3 } floatProperty3 && floatValue3.GetValueOrDefault() > 0f && (!num.HasValue || (double?)floatProperty3.FloatValue > num.Value))
			{
				num = floatProperty3.FloatValue;
			}
			if (num.HasValue && num.Value > 0.0 && hotbar.CooldownMs <= 0)
			{
				hotbar.CooldownMs = (long)(num.Value * 1000.0);
			}
			IProperty property4 = propertyCollection.GetProperty(268442188u);
			if (property4 is IBitFieldProperty bitFieldProperty)
			{
				bool flag = bitFieldProperty.Values?.Contains<string>("Foe", StringComparer.OrdinalIgnoreCase) ?? false;
				bool flag2 = bitFieldProperty.Values?.Contains<string>("Self", StringComparer.OrdinalIgnoreCase) ?? false;
				if (flag && flag2)
				{
					hotbar.ApiTargetType = "Foe,Self";
				}
				else if (flag)
				{
					hotbar.ApiTargetType = "Foe";
				}
				else if (flag2)
				{
					hotbar.ApiTargetType = "Self";
				}
				else
				{
					hotbar.ApiTargetType = "";
				}
			}
			IProperty property5 = propertyCollection.GetProperty(268438089u);
			if (property5 is IByteProperty byteProperty)
			{
				hotbar.ApiIsHarmful = byteProperty.ByteValue != 0;
			}
			lock (_enrichLock)
			{
				_enrichedDid.Add(referenceDid);
			}
		}
		catch (Exception ex)
		{
			Log($"SDK enrichment error for [{hotbar.Name}] (0x{referenceDid:X8}): {ex.Message}");
		}
	}

	internal bool AnyTriggerDown()
	{
		return IsKeyDown(_config.TriggerKey) || IsKeyDown(_config.TriggerKey2);
	}

	public IPluginUI GetPluginUI()
	{
		return _pluginUI;
	}

	public void Terminate()
	{
		Shutdown();
	}

	public void Initialize(IDdoGameDataProvider gameDataProvider, string folder)
	{
		_gameDataProvider = gameDataProvider;
		_pluginFolder = folder;
		if (!Directory.Exists(AppDataDir))
		{
			Directory.CreateDirectory(AppDataDir);
		}
		_config = RotationConfig.Load(Path.Combine(folder, "RotationHelper.json"));
		_pluginUI = new PluginUI(this);
		StartHudInitTimer();
		SwapToCharacterConfig();
		LoadRotation();
		LoadBuffOrder();
		LoadEmergency();
		LoadToggleBuffs();
		ReloadToggleBuffStatesFromConfig();
		BuffManager = new BuffManager(_gameDataProvider, _pluginFolder, Log);
		BuffManager.Initialize();
		BuffManager.DisableInPublicAreas = _config.BuffDisablePublic;
		BuffManager.EnabledZoneDids = _config.EnabledZoneDids;
		_pollTimer = new System.Threading.Timer(delegate
		{
			PollTrigger();
		}, null, 0, 50);
		_autocastTimer = new System.Threading.Timer(delegate
		{
			Task.Run(() => AutocastTick());
		}, null, 1000, 20);
		_refreshTimer = new System.Threading.Timer(delegate
		{
			Task.Run(() => RefreshAbilityCommands());
		}, null, 3000, 5000);
		_gameDataProvider.EventProvider.OnHotbarUsage.AddHandler(delegate(IHotbarSlot slot)
		{
			_lastHotbarUsageTime = DateTime.UtcNow;
			if (slot != null && slot.ReferenceDid != 0)
			{
				Log($"HOTBAR: DID=0x{slot.ReferenceDid:X8} Bar{slot.Bar + 1} Slot{slot.Slot + 1}");
				uint referenceDid = slot.ReferenceDid;
				lock (_lock)
				{
					foreach (RotationAbility item in Rotation)
					{
						if (item.ReferenceDid.GetValueOrDefault() == referenceDid)
						{
							item.LastFired = DateTime.UtcNow;
							break;
						}
					}
					foreach (RotationAbility toggleBuff in ToggleBuffs)
					{
						if (toggleBuff.ReferenceDid.GetValueOrDefault() == referenceDid)
						{
							toggleBuff.LastFired = DateTime.UtcNow;
							break;
						}
					}
					foreach (RotationAbility item2 in BuffOrder)
					{
						if (item2.ReferenceDid.GetValueOrDefault() == referenceDid)
						{
							item2.LastFired = DateTime.UtcNow;
							break;
						}
					}
					foreach (RotationAbility item3 in Emergency)
					{
						if (item3.ReferenceDid.GetValueOrDefault() == referenceDid)
						{
							item3.LastFired = DateTime.UtcNow;
							break;
						}
					}
					if (referenceDid == 1879062547)
					{
						_sessionToggleFired.Remove(referenceDid);
						_lastManualAutoAttackToggle = DateTime.UtcNow;
					}
				}
				return Task.CompletedTask;
			}
			return Task.CompletedTask;
		});
		_gameDataProvider.EventProvider.OnEffectAblativeUse.AddHandler(delegate(IEffectAblativeUse e)
		{
			if (e != null && _config.DamageMeterEnabled)
			{
				lock (_lock)
				{
					_totalDamageTaken += e.DamageAmount;
				}
			}
			return Task.CompletedTask;
		});
		_gameDataProvider.EventProvider.OnMapAreaChange.AddHandler(delegate(uint newAreaDid)
		{
			lock (_lock)
			{
				_lastMapChangeTime = DateTime.UtcNow;
				Log($"ZONE CHANGE detected (area 0x{newAreaDid:X8}) — resetting cooldown timers");
				_knownZoneDids.Add(newAreaDid);
				foreach (RotationAbility item4 in Rotation)
				{
					item4.LastFired = DateTime.MinValue;
				}
				foreach (RotationAbility item5 in Emergency)
				{
					item5.LastFired = DateTime.MinValue;
				}
				ClearToggleCache();
				_pendingEmergencyTargetId = null;
				_pendingEmergencyAbility = null;
				_pendingTargetRevertId = null;
				_pendingBuffTargetId = null;
				_pendingBuffAbility = null;
				_pendingBuffRevertId = null;
				_lastPartyBuffTime.Clear();
				BuffManager?.ResetZoneState();
			}
			return Task.CompletedTask;
		});
		_gameDataProvider.EventProvider.OnProgressStart.AddHandler(delegate
		{
			_progressActive = true;
			Log("Progress start (shrine/recall/etc) — pausing autocast");
			return Task.CompletedTask;
		});
		_gameDataProvider.EventProvider.OnProgressStop.AddHandler(delegate
		{
			_progressActive = false;
			Log("Progress stop — resuming autocast");
			return Task.CompletedTask;
		});
		_gameDataProvider.EventProvider.OnLogin.AddHandler(delegate
		{
			SwapToCharacterConfig();
			return Task.CompletedTask;
		});
		SwapToCharacterConfig();
	}

	internal void TogglePause()
	{
		_paused = !_paused;
		Log("PAUSE TOGGLED: " + (_paused ? "ON" : "OFF"));
		if (_paused)
		{
			StopRotation();
			StopBuffRotation();
		}
		_pluginUI?.UpdatePauseStatus(_paused);
	}

	private void StartHudInitTimer()
	{
		_hudInitTimer = new System.Threading.Timer(delegate
		{
			try
			{
				if (_pluginUI?._mainForm != null && _pluginUI._mainForm.IsHandleCreated)
				{
					_pluginUI._mainForm.BeginInvoke(delegate
					{
						_hudForm = new HudOverlayForm(this);
						Log("HUD: created on UI thread " + Environment.CurrentManagedThreadId);
					});
					_hudInitTimer.Dispose();
					_hudInitTimer = null;
				}
			}
			catch
			{
			}
		}, null, 500, 200);
	}

	private void Shutdown()
	{
		_hudInitTimer?.Dispose();
		StopRotation();
		StopBuffRotation();
		_pollTimer?.Dispose();
		_autocastTimer?.Dispose();
		_refreshTimer?.Dispose();
		BuffManager?.Shutdown();
		_hudForm?.Shutdown();
		if (_setupForm != null && !_setupForm.IsDisposed)
		{
			_setupForm.Close();
		}
		SaveConfig();
		SaveRotation();
		SaveBuffOrder();
		SaveToggleBuffs();
		SaveEmergency();
	}

	private void PollTrigger()
	{
		try
		{
			if (_config == null)
			{
				return;
			}
			if (_config.PauseToggleKey != 0)
			{
				bool flag = (NativeMethods.GetAsyncKeyState(_config.PauseToggleKey) & 0x8000) != 0;
				if (flag && !_pauseWasHeld)
				{
					_pauseWasHeld = true;
					_paused = !_paused;
					Log($"PAUSE {(_paused ? "ON" : "OFF")} (key 0x{_config.PauseToggleKey:X2})");
					if (_paused)
					{
						StopRotation();
						StopBuffRotation();
					}
					_pluginUI?.UpdatePauseStatus(_paused);
				}
				else if (!flag && _pauseWasHeld)
				{
					_pauseWasHeld = false;
				}
			}
			if (_paused)
			{
				return;
			}
			if (_config.TriggerKey != 0 || _config.TriggerKey2 != 0)
			{
				bool flag2 = AnyTriggerDown();
				if (flag2 && !_triggerWasHeld)
				{
					_triggerWasHeld = true;
					Log($"Trigger DOWN (0x{_config.TriggerKey:X2})");
					Task.Run(delegate
					{
						RunRotation();
					});
				}
				else if (!flag2 && _triggerWasHeld)
				{
					_triggerWasHeld = false;
					Log($"Trigger UP (0x{_config.TriggerKey:X2})");
				}
			}
			if (_config.BuffTriggerKey == 0 || _config.BuffTriggerKey == _config.TriggerKey)
			{
				return;
			}
			bool flag3 = IsKeyDown(_config.BuffTriggerKey);
			if (flag3 && !_buffTriggerWasHeld)
			{
				_buffTriggerWasHeld = true;
				Log($"Buff Trigger DOWN (0x{_config.BuffTriggerKey:X2})");
				Task.Run(delegate
				{
					RunBuffRotation();
				});
			}
			else if (!flag3 && _buffTriggerWasHeld)
			{
				_buffTriggerWasHeld = false;
				Log($"Buff Trigger UP (0x{_config.BuffTriggerKey:X2})");
			}
		}
		catch (Exception ex)
		{
			Log("PollTrigger error: " + ex.Message);
		}
	}

	private void RunRotation()
	{
		lock (_lock)
		{
			if (_isRunning)
			{
				return;
			}
			_isRunning = true;
			_rotationCts = new CancellationTokenSource();
		}
		CancellationToken token = _rotationCts.Token;
		try
		{
			Log("=== ROTATION START ===");
			_rotationIndex = 0;
			while (!token.IsCancellationRequested)
			{
				if (!AnyTriggerDown())
				{
					Log("ROTATION: trigger released, exiting");
					break;
				}
				DateTime utcNow = DateTime.UtcNow;
				if ((utcNow - _lastFireTime).TotalMilliseconds < (double)_config.MinFireIntervalMs)
				{
					Task.Delay(50, token).Wait(token);
					continue;
				}
				List<RotationAbility> list;
				lock (_lock)
				{
					list = Rotation.ToList();
				}
				if (list.Count == 0)
				{
					Log("ROTATION: snapshot empty, waiting");
					Task.Delay(100, token).Wait(token);
					continue;
				}
				Log($"ROTATION iteration: {list.Count} abilities");
				bool flag = false;
				long num = long.MaxValue;
				if (_config.SmartOrder)
				{
					List<RotationAbility> list2 = new List<RotationAbility>();
					for (int i = 0; i < list.Count; i++)
					{
						RotationAbility rotationAbility = list[i];
						if (IsOnCooldown(rotationAbility))
						{
							long remainingCooldownMs = GetRemainingCooldownMs(rotationAbility);
							if (remainingCooldownMs > 0 && remainingCooldownMs < num)
							{
								num = remainingCooldownMs;
							}
						}
						else if (!rotationAbility.ReferenceDid.HasValue || rotationAbility.ReferenceDid == 0 || (utcNow - rotationAbility.LastFired).TotalMilliseconds < 150.0)
						{
							continue;
						}
						else if (!MeetsConditions(rotationAbility))
						{
							Log($"ROTATION condition fail [{i}] {rotationAbility.Name}");
						}
						else
						{
							list2.Add(rotationAbility);
						}
					}
					RotationAbility rotationAbility2 = PickBestAbility(list2);
					if (rotationAbility2 != null && FireAbility(rotationAbility2, skipCooldown: true))
					{
						Log("ROTATION fire (smart) " + rotationAbility2.Name);
						flag = true;
						_lastFireTime = DateTime.UtcNow;
						int num2 = Math.Max(50, _config.PressDelayMs);
						for (int j = 0; j < num2; j += 20)
						{
							if (token.IsCancellationRequested)
							{
								break;
							}
							if (!AnyTriggerDown())
							{
								break;
							}
							Task.Delay(20, token).Wait(token);
						}
					}
				}
			else
				{
					int startIdx = _rotationIndex % list.Count;
					for (int kk = 0; kk < list.Count; kk++)
					{
						int k = (startIdx + kk) % list.Count;
						RotationAbility rotationAbility3 = list[k];
						Log($"ROTATION check index={k}/{list.Count} name={rotationAbility3.Name}");
						if (!IsOnCooldown(rotationAbility3))
						{
							if (!rotationAbility3.ReferenceDid.HasValue || rotationAbility3.ReferenceDid == 0 || (utcNow - rotationAbility3.LastFired).TotalMilliseconds < 150.0)
							{
								continue;
							}
							if (!MeetsConditions(rotationAbility3))
							{
								Log($"ROTATION condition fail [{k}] {rotationAbility3.Name}");
							}
							else
							{
								if (!FireAbility(rotationAbility3, skipCooldown: true))
								{
									continue;
								}
								Log($"ROTATION fire index={k} name={rotationAbility3.Name}");
								flag = true;
								_lastFireTime = DateTime.UtcNow;
								_rotationIndex = (k + 1) % list.Count;
								int num3 = Math.Max(50, _config.PressDelayMs);
								for (int l = 0; l < num3; l += 20)
								{
									if (token.IsCancellationRequested)
									{
										break;
									}
								if (!AnyTriggerDown())
								{
									break;
								}
									Task.Delay(20, token).Wait(token);
								}
								break;
							}
						}
						else
						{
							long remainingCooldownMs2 = GetRemainingCooldownMs(rotationAbility3);
							if (remainingCooldownMs2 > 0 && remainingCooldownMs2 < num)
							{
								num = remainingCooldownMs2;
							}
						}
					}
				}
				if (!flag)
				{
					long num4 = ((num < long.MaxValue) ? Math.Clamp(num + 200, 100L, 10000L) : 500);
					Log($"ROTATION: nothing fired, waiting {num4}ms for cooldowns");
					Task.Delay((int)num4, token).Wait(token);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (AggregateException)
		{
		}
		catch (Exception ex3)
		{
			Log("Rotation error: " + ex3.Message);
		}
		finally
		{
			_isRunning = false;
		}
	}

	private void RunBuffRotation()
	{
		lock (_lock)
		{
			if (_buffIsRunning)
			{
				return;
			}
			_buffIsRunning = true;
			_buffCts = new CancellationTokenSource();
		}
		CancellationToken token = _buffCts.Token;
		try
		{
			while (!token.IsCancellationRequested)
			{
				if (!IsKeyDown(_config.BuffTriggerKey))
				{
					Log("BUFF: trigger released, exiting");
					break;
				}
				DateTime utcNow = DateTime.UtcNow;
				if ((utcNow - _lastFireTime).TotalMilliseconds < (double)_config.MinFireIntervalMs)
				{
					Task.Delay(50, token).Wait(token);
					continue;
				}
				List<RotationAbility> list;
				lock (_lock)
				{
					list = BuffOrder.ToList();
				}
				if (list.Count == 0)
				{
					Log("BUFF: snapshot empty, waiting");
					Task.Delay(100, token).Wait(token);
					continue;
				}
				Log($"BUFF iteration: {list.Count} abilities");
				bool flag = false;
				long num = long.MaxValue;
				for (int i = 0; i < list.Count; i++)
				{
					RotationAbility rotationAbility = list[i];
					Log($"BUFF check index={i}/{list.Count} name={rotationAbility.Name} cmd={rotationAbility.CommandValue} cd={rotationAbility.CooldownMs}ms");
					if (!IsOnCooldown(rotationAbility))
					{
						if (!FireAbility(rotationAbility, skipCooldown: true))
						{
							continue;
						}
						Log($"BUFF fire index={i} name={rotationAbility.Name}");
						flag = true;
						_lastFireTime = DateTime.UtcNow;
						int num2 = Math.Max(50, _config.BuffPressDelayMs);
						for (int j = 0; j < num2; j += 20)
						{
							if (token.IsCancellationRequested)
							{
								break;
							}
							if (!IsKeyDown(_config.BuffTriggerKey))
							{
								StopBuffRotation();
								return;
							}
							Task.Delay(20, token).Wait(token);
						}
						break;
					}
					long remainingCooldownMs = GetRemainingCooldownMs(rotationAbility);
					if (remainingCooldownMs > 0 && remainingCooldownMs < num)
					{
						num = remainingCooldownMs;
					}
					Log($"BUFF skip index={i} - on cooldown (remaining={remainingCooldownMs}ms)");
				}
				if (!flag)
				{
					long num3 = ((num < long.MaxValue) ? Math.Clamp(num + 200, 100L, 10000L) : 500);
					Log($"BUFF: nothing fired, waiting {num3}ms for cooldowns");
					Task.Delay((int)num3, token).Wait(token);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (AggregateException)
		{
		}
		catch (Exception ex3)
		{
			Log("Buff rotation error: " + ex3.Message);
		}
		finally
		{
			_buffIsRunning = false;
		}
	}

	internal void StopBuffRotation()
	{
		lock (_lock)
		{
			_buffCts?.Cancel();
			_buffIsRunning = false;
		}
	}

	internal void StopRotation()
	{
		lock (_lock)
		{
			_rotationCts?.Cancel();
			_isRunning = false;
		}
	}

	private bool IsOnCooldown(RotationAbility ability)
	{
		if (ability.ReferenceDid.GetValueOrDefault() == 0)
		{
			return false;
		}
		if (ability.CooldownTimerDid1 == 0 && ability.CooldownTimerDid2 == 0 && ability.CooldownTimerDid3 == 0)
		{
			RefreshAbilityTimerDids(ability);
		}
		if ((ability.CooldownTimerDid1 != 0 || ability.CooldownTimerDid2 != 0 || ability.CooldownTimerDid3 != 0) && GetEntityCooldownMs(ability) > 0)
		{
			return true;
		}
		return false;
	}

	private long GetEntityCooldownMs(RotationAbility ability)
	{
		uint valueOrDefault = ability.ReferenceDid.GetValueOrDefault();
		double num = double.NaN;
		if (valueOrDefault != 0)
		{
			lock (_cacheLock)
			{
				if (_cooldownCache.TryGetValue(valueOrDefault, out (double, DateTime, double) value))
				{
					double totalSeconds = (DateTime.UtcNow - value.Item2).TotalSeconds;
					double num2 = value.Item1 - totalSeconds;
					if (num2 > 0.0)
					{
						return (long)(num2 * 1000.0);
					}
					num = value.Item3;
					_cooldownCache.Remove(valueOrDefault);
				}
			}
		}
		try
		{
			IGameDataProvider gameDataProvider = _gameDataProvider;
			if (gameDataProvider == null)
			{
				return 0L;
			}
			IEntity currentCharacter = gameDataProvider.GetCurrentCharacter();
			if (currentCharacter == null)
			{
				return 0L;
			}
			double num3 = 0.0;
			IProperty property = currentCharacter.GetProperty(GameTimeDid);
			if (property != null)
			{
				try
				{
					num3 = Convert.ToDouble(property.Value);
				}
				catch
				{
				}
			}
			if (num3 <= 0.0)
			{
				return 0L;
			}
			if (!double.IsNaN(num) && Math.Abs(num3 - num) < 0.01)
			{
				Log($"COOLDOWN [{ability.Name}] gameTime stale (still {num3:F1}) — expired, ready");
				return 0L;
			}
			uint[] array = new uint[3] { ability.CooldownTimerDid1, ability.CooldownTimerDid2, ability.CooldownTimerDid3 };
			uint[] array2 = array;
			uint[] array3 = array2;
			foreach (uint num4 in array3)
			{
				if (num4 == 0 || num4 == GameTimeDid)
				{
					continue;
				}
				IProperty property2 = currentCharacter.GetProperty(num4);
				if (property2 == null)
				{
					continue;
				}
				double num5 = 0.0;
				try
				{
					num5 = Convert.ToDouble(property2.Value);
				}
				catch
				{
				}
				if (num5 > num3)
				{
					double num6 = num5 - num3;
					long result = (long)(num6 * 1000.0);
					lock (_cacheLock)
					{
						_cooldownCache[valueOrDefault] = (num6, DateTime.UtcNow, num3);
					}
					return result;
				}
			}
		}
		catch (Exception ex)
		{
			Log("GetEntityCooldownMs error: " + ex.Message);
		}
		return 0L;
	}

	internal long GetRemainingCooldownMs(RotationAbility ability)
	{
		if (ability.CooldownTimerDid1 == 0 && ability.CooldownTimerDid2 == 0 && ability.CooldownTimerDid3 == 0)
		{
			RefreshAbilityTimerDids(ability);
		}
		return GetEntityCooldownMs(ability);
	}

	private void RefreshAbilityTimerDids(RotationAbility ability)
	{
		try
		{
			uint valueOrDefault = ability.ReferenceDid.GetValueOrDefault();
			if (valueOrDefault == 0)
			{
				return;
			}
			IGameDataProvider gameDataProvider = _gameDataProvider;
			if (gameDataProvider == null)
			{
				return;
			}
			IEntity currentCharacter = gameDataProvider.GetCurrentCharacter();
			if (currentCharacter == null || !(currentCharacter.GetProperty(268435457u) is IPropertyMaster propertyMaster))
			{
				return;
			}
			IPropertyCollection propertyCollection = propertyMaster.GetPropertyCollection(valueOrDefault);
			if (propertyCollection != null)
			{
				IProperty property = propertyCollection.GetProperty(268443082u);
				if (property is IUInt32Property iUInt32Property)
				{
					ability.CooldownTimerDid1 = iUInt32Property.UInt32Value.GetValueOrDefault();
				}
				IProperty property2 = propertyCollection.GetProperty(268443078u);
				if (property2 is IUInt32Property iUInt32Property2)
				{
					ability.CooldownTimerDid2 = iUInt32Property2.UInt32Value.GetValueOrDefault();
				}
				IProperty property3 = propertyCollection.GetProperty(268443079u);
				if (property3 is IUInt32Property iUInt32Property3)
				{
					ability.CooldownTimerDid3 = iUInt32Property3.UInt32Value.GetValueOrDefault();
				}
				Log($"RefreshDIDs [{ability.Name}] -> 0x{ability.CooldownTimerDid1:X8}|0x{ability.CooldownTimerDid2:X8}|0x{ability.CooldownTimerDid3:X8}");
			}
		}
		catch (Exception ex)
		{
			Log("RefreshDIDs error [" + ability.Name + "]: " + ex.Message);
		}
	}

	private bool MeetsConditions(RotationAbility ability)
	{
		if (ability.RequiredBuffDid != 0)
		{
			bool flag = false;
			try
			{
				IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _gameDataProvider?.GetActiveEffects();
				if (readOnlyDictionary != null)
				{
					foreach (KeyValuePair<int, IActiveEffect> item in readOnlyDictionary)
					{
						if (item.Value.EffectDid == (int)ability.RequiredBuffDid)
						{
							flag = true;
							break;
						}
					}
				}
			}
			catch
			{
			}
			if (!flag)
			{
				return false;
			}
		}
		if (ability.RequiredDebuffOnTargetDid != 0)
		{
			bool flag2 = false;
			try
			{
				IEntity entity = _gameDataProvider?.GetSelectedObject();
				if (entity != null)
				{
					IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary2 = _gameDataProvider?.GetActiveEffects();
					if (readOnlyDictionary2 != null)
					{
						foreach (KeyValuePair<int, IActiveEffect> item2 in readOnlyDictionary2)
						{
							if (item2.Value.EffectDid == (int)ability.RequiredDebuffOnTargetDid)
							{
								flag2 = true;
								break;
							}
						}
					}
				}
			}
			catch
			{
			}
			if (!flag2)
			{
				return false;
			}
		}
		return true;
	}

	private RotationAbility PickBestAbility(List<RotationAbility> candidates)
	{
		if (candidates.Count == 0)
		{
			return null;
		}
		if (candidates.Count == 1)
		{
			return candidates[0];
		}
		DateTime utcNow = DateTime.UtcNow;
		return (from a in candidates
			orderby a.Priority descending, (utcNow - a.LastFired).TotalMilliseconds descending
			select a).First();
	}

	private bool FireAbility(RotationAbility ability, bool skipCooldown = false)
	{
		if (!skipCooldown && IsOnCooldown(ability))
		{
			return false;
		}
		if (_config.DamageMeterEnabled)
			CachePreAttackState();
		if (ability.CommandValue.HasValue && ability.CommandValue.Value != 0)
		{
			_gameDataProvider?.SendInput(ability.CommandValue.Value);
			ability.LastFired = DateTime.UtcNow;
			Log($"FIRE [{ability.KeyLabel}] {ability.Name} via SendInput(0x{ability.CommandValue.Value:X8})");
			ScheduleDamageCheck();
			return true;
		}
		if (ability.VirtualKey > 0)
		{
			try
			{
				HotKeyEventArgs key = HotKeyEventArgs.FromKeys(ability.VirtualKey, false, false, false);
				_gameDataProvider?.SendKey(key);
				ability.LastFired = DateTime.UtcNow;
				Log($"FIRE [{ability.KeyLabel}] {ability.Name} via SendKey(0x{ability.VirtualKey:X2})");
				ScheduleDamageCheck();
				return true;
			}
			catch (Exception ex)
			{
				Log("SendKey failed: " + ex.Message + ", falling back to OS SendInput");
				NativeMethods.SendKeyPress((ushort)ability.VirtualKey);
				ability.LastFired = DateTime.UtcNow;
				Log($"FIRE [{ability.KeyLabel}] {ability.Name} via OS SendInput");
				ScheduleDamageCheck();
				return true;
			}
		}
		Log($"SKIP [{ability.KeyLabel}] {ability.Name} - no CommandValue or VirtualKey");
		return false;
	}

	private void CachePreAttackState()
	{
		IEntity target = _gameDataProvider?.GetSelectedObject();
		if (target != null)
		{
			_preAttackTargetId = target.InstanceId;
			_preAttackTargetHp = target.HitPoints_Current;
		}
	}

	private void ScheduleDamageCheck()
	{
		ulong? targetId = _preAttackTargetId;
		int? preHp = _preAttackTargetHp;
		_ = Task.Run(async delegate
		{
			await Task.Delay(200);
			try
			{
				IEntity target = _gameDataProvider?.GetSelectedObject();
				if (target == null || target.InstanceId != targetId)
					return;
				int? curHp = target.HitPoints_Current;
				if (preHp.HasValue && curHp.HasValue && curHp.Value < preHp.Value)
				{
					int damage = preHp.Value - curHp.Value;
					lock (_lock)
					{
						if (_totalDamageDealt == 0)
							_combatStartTime = DateTime.UtcNow;
						_totalDamageDealt += damage;
						_damageSampleCount++;
					}
				}
			}
			catch
			{
			}
		});
	}

	internal void TestKeyPress(int vk)
	{
		try
		{
			HotKeyEventArgs key = HotKeyEventArgs.FromKeys(vk, false, false, false);
			_gameDataProvider?.SendKey(key);
			Log($"TestKeyPress(vk=0x{vk:X2})");
		}
		catch (Exception ex)
		{
			Log("TestKeyPress failed: " + ex.Message);
		}
	}

	internal List<HotbarAbility> ScanHotbar()
	{
		List<HotbarAbility> list = new List<HotbarAbility>();
		try
		{
			IHotbarSlot[] array = _gameDataProvider?.GetHotbarMap();
			if (array == null || array.Length == 0)
			{
				Log("Hotbar map empty or unavailable");
				return list;
			}
			IGameDataProvider gameDataProvider = _gameDataProvider;
			IPropertyMaster propertyMaster = gameDataProvider?.PropertyMaster;
			for (int i = 0; i < array.Length; i++)
			{
				IHotbarSlot hotbarSlot = array[i];
				if (hotbarSlot == null)
				{
					continue;
				}
				uint? inputAction = hotbarSlot.GetInputAction();
				uint bar = hotbarSlot.Bar;
				uint slot = hotbarSlot.Slot;
				uint referenceDid = hotbarSlot.ReferenceDid;
				if (!inputAction.HasValue || inputAction.Value == 0)
				{
					Log($"ScanHotbar: slot Bar{bar + 1} Slot{slot + 1} skipped (no command)");
					continue;
				}
				if (referenceDid == 0)
				{
					Log($"ScanHotbar: slot Bar{bar + 1} Slot{slot + 1} skipped (no ReferenceDid)");
					continue;
				}
				string text = LookupNameFromDid(referenceDid, propertyMaster, gameDataProvider);
				if (string.IsNullOrEmpty(text))
				{
					Log($"ScanHotbar: Bar{bar + 1} Slot{slot + 1} — no name found for DID 0x{referenceDid:X8}, marking unknown");
					Log($"  DID 0x{referenceDid:X8} prefix: 0x{(referenceDid & 0xFF000000u) >> 24:X2}");
					try
					{
						IPropertyCollection propertyCollection = propertyMaster?.GetPropertyCollection(referenceDid);
						if (propertyCollection != null)
						{
							try
							{
								Log("  props.Name = \"" + (propertyCollection.Name ?? "(null)") + "\"");
							}
							catch (Exception ex)
							{
								Log("  props.Name threw: " + ex.Message);
							}
							try
							{
								IStringInfoProperty stringInfoProperty = propertyCollection.GetStringInfoProperty(1u);
								Log("  GetStringInfoProperty(1) = " + ((stringInfoProperty != null) ? ("\"" + stringInfoProperty.GetText(propertyMaster, referenceDid, propertyCollection) + "\"") : "null"));
							}
							catch
							{
							}
							try
							{
								IStringProperty stringProperty = propertyCollection.GetStringProperty(1u);
								Log("  GetStringProperty(1) = " + ((stringProperty != null) ? ("\"" + stringProperty.StringValue + "\"") : "null"));
							}
							catch
							{
							}
							Log($"  PropertyMaster props: {propertyCollection.Properties.Count} entries");
							foreach (KeyValuePair<uint, IProperty> property6 in propertyCollection.Properties)
							{
								string text2 = null;
								string text3 = null;
								if (property6.Value is IStringInfoProperty stringInfoProperty2)
								{
									text2 = stringInfoProperty2.GetText(propertyMaster, referenceDid, propertyCollection);
									text3 = "StringInfo";
								}
								else if (property6.Value is IStringProperty stringProperty2)
								{
									text2 = stringProperty2.StringValue;
									text3 = "String";
								}
								else if (property6.Value is IUInt32Property { UInt32Value: var uInt32Value })
								{
									text2 = uInt32Value?.ToString();
									text3 = "U32";
								}
								else if (property6.Value is IInt32Property { Int32Value: var int32Value })
								{
									text2 = int32Value?.ToString();
									text3 = "I32";
								}
								else if (property6.Value is IFloatProperty { FloatValue: var floatValue })
								{
									text2 = floatValue?.ToString();
									text3 = "Float";
								}
								else if (property6.Value is IDoubleProperty { DoubleValue: var doubleValue })
								{
									text2 = doubleValue?.ToString();
									text3 = "Double";
								}
								else if (property6.Value is IByteProperty { ByteValue: var byteValue })
								{
									text2 = byteValue.ToString();
									text3 = "Byte";
								}
								else
								{
									text3 = "OTHER";
								}
								Log($"    prop[{property6.Key}] via {text3} = \"{text2 ?? "(null)"}\"");
							}
						}
						IPropertyCollection propertyCollection2 = gameDataProvider?.GetWeenieProperties(referenceDid);
						if (propertyCollection2 != null)
						{
							try
							{
								Log("  weenie.Name = \"" + (propertyCollection2.Name ?? "(null)") + "\"");
							}
							catch
							{
							}
							try
							{
								IStringInfoProperty stringInfoProperty3 = propertyCollection2.GetStringInfoProperty(1u);
								Log("  weenie.GetStringInfoProperty(1) = " + ((stringInfoProperty3 != null) ? ("\"" + stringInfoProperty3.GetText(propertyMaster, referenceDid, propertyCollection2) + "\"") : "null"));
							}
							catch
							{
							}
							Log($"  Weenie props: {propertyCollection2.Properties.Count} entries");
							foreach (KeyValuePair<uint, IProperty> property7 in propertyCollection2.Properties)
							{
								string text4 = null;
								string text5 = null;
								if (property7.Value is IStringInfoProperty stringInfoProperty4)
								{
									text4 = stringInfoProperty4.GetText(propertyMaster, referenceDid, propertyCollection2);
									text5 = "StringInfo";
								}
								else if (property7.Value is IStringProperty stringProperty3)
								{
									text4 = stringProperty3.StringValue;
									text5 = "String";
								}
								else if (property7.Value is IUInt32Property { UInt32Value: var uInt32Value2 })
								{
									text4 = uInt32Value2?.ToString();
									text5 = "U32";
								}
								else if (property7.Value is IInt32Property { Int32Value: var int32Value2 })
								{
									text4 = int32Value2?.ToString();
									text5 = "I32";
								}
								else if (property7.Value is IFloatProperty { FloatValue: var floatValue2 })
								{
									text4 = floatValue2?.ToString();
									text5 = "Float";
								}
								else if (property7.Value is IDoubleProperty { DoubleValue: var doubleValue2 })
								{
									text4 = doubleValue2?.ToString();
									text5 = "Double";
								}
								else if (property7.Value is IByteProperty { ByteValue: var byteValue2 })
								{
									text4 = byteValue2.ToString();
									text5 = "Byte";
								}
								else
								{
									text5 = "OTHER";
								}
								Log($"    weenie[{property7.Key}] via {text5} = \"{text4 ?? "(null)"}\"");
							}
						}
						uint num = referenceDid & 0xFFFFFF;
						if (propertyMaster != null)
						{
							uint[] array2 = new uint[7] { 620763835u, 620763989u, 620762385u, 620830741u, 620835274u, 620795239u, 620756994u };
							uint[] array3 = array2;
							uint[] array4 = array3;
							uint[] array5 = array4;
							foreach (uint num2 in array5)
							{
								try
								{
									string value = propertyMaster.GetString(num, num2);
									if (!string.IsNullOrEmpty(value))
									{
										Log($"  StringTable[{num2:X8}]({num}) = \"{value}\"");
									}
								}
								catch
								{
								}
							}
						}
						try
						{
							if (propertyCollection != null)
							{
								string effectName = NameGenerator.GetEffectName(propertyMaster, propertyCollection, propertyCollection);
								if (!string.IsNullOrEmpty(effectName))
								{
									Log("  NameGenerator.GetEffectName = \"" + effectName + "\"");
								}
								string name = NameGenerator.GetName(propertyMaster, propertyCollection, propertyCollection);
								if (!string.IsNullOrEmpty(name))
								{
									Log("  NameGenerator.GetName = \"" + name + "\"");
								}
							}
						}
						catch
						{
						}
						try
						{
							if (propertyCollection2 != null)
							{
								string effectName2 = NameGenerator.GetEffectName(propertyMaster, propertyCollection2, propertyCollection2);
								if (!string.IsNullOrEmpty(effectName2))
								{
									Log("  Weenie.NameGenerator.GetEffectName = \"" + effectName2 + "\"");
								}
								string name2 = NameGenerator.GetName(propertyMaster, propertyCollection2, propertyCollection2);
								if (!string.IsNullOrEmpty(name2))
								{
									Log("  Weenie.NameGenerator.GetName = \"" + name2 + "\"");
								}
							}
						}
						catch
						{
						}
						try
						{
							ulong? num3 = _gameDataProvider?.GetCurrentCharacterId();
							if (num3.HasValue)
							{
								IPropertyCollection characterBuildData = _gameDataProvider.GetCharacterBuildData(num3.Value);
								if (characterBuildData != null)
								{
									Log($"  CharacterBuildData: {characterBuildData.Properties.Count} entries");
									try
									{
										Log("  buildData.Name = \"" + (characterBuildData.Name ?? "(null)") + "\"");
									}
									catch
									{
									}
									int num4 = 0;
									foreach (KeyValuePair<uint, IProperty> property8 in characterBuildData.Properties)
									{
										if (property8.Value is IStringInfoProperty stringInfoProperty5)
										{
											string text6 = stringInfoProperty5.GetText(propertyMaster, 0u, characterBuildData);
											if (!string.IsNullOrEmpty(text6) && num4 < 20)
											{
												Log($"  buildData prop[{property8.Key}] (StringInfo) = \"{text6}\"");
												num4++;
											}
										}
										else if (property8.Value is IStringProperty stringProperty4)
										{
											if (!string.IsNullOrEmpty(stringProperty4.StringValue) && num4 < 20)
											{
												Log($"  buildData prop[{property8.Key}] (String) = \"{stringProperty4.StringValue}\"");
												num4++;
											}
										}
										else if (property8.Value is IUInt32Property iUInt32Property3 && (property8.Key >= 1879048192 || property8.Key < 256))
										{
											Log($"  buildData prop[{property8.Key}] (U32) = 0x{iUInt32Property3.UInt32Value:X8}");
										}
									}
									if (num4 == 0)
									{
										Log("  buildData: no string properties found");
									}
								}
								else
								{
									Log("  GetCharacterBuildData returned null");
								}
							}
							else
							{
								Log("  GetCurrentCharacterId returned null");
							}
						}
						catch (Exception ex2)
						{
							Log("  GetCharacterBuildData threw: " + ex2.Message);
						}
					}
					catch (Exception ex3)
					{
						Log("  Debug lookup error: " + ex3.Message);
					}
				}
				string text7 = ((!string.IsNullOrEmpty(text)) ? SimplifyName(text) : "Unknown");
				long num5 = DetectCooldownFromDescription(referenceDid);
				long num6 = LookupBuffDurationFromTable(text7);
				uint num7 = 0u;
				uint num8 = 0u;
				uint num9 = 0u;
				try
				{
					IPropertyCollection propertyCollection3 = propertyMaster?.GetPropertyCollection(referenceDid);
					if (propertyCollection3 != null)
					{
						IProperty property = propertyCollection3.GetProperty(268443082u);
						if (property is IUInt32Property { UInt32Value: var uInt32Value3 })
						{
							num7 = uInt32Value3.GetValueOrDefault();
						}
						IProperty property2 = propertyCollection3.GetProperty(268443078u);
						if (property2 is IUInt32Property { UInt32Value: var uInt32Value4 })
						{
							num8 = uInt32Value4.GetValueOrDefault();
						}
						IProperty property3 = propertyCollection3.GetProperty(268443079u);
						if (property3 is IUInt32Property { UInt32Value: var uInt32Value5 })
						{
							num9 = uInt32Value5.GetValueOrDefault();
						}
					}
				}
				catch
				{
				}
				if (num7 != 0 || num8 != 0 || num9 != 0)
				{
					try
					{
						IEntity entity = gameDataProvider?.GetCurrentCharacter();
						if (entity != null)
						{
							double num10 = 0.0;
							IProperty property4 = entity.GetProperty(GameTimeDid);
							if (property4 != null)
							{
								try
								{
									num10 = Convert.ToDouble(property4.Value);
								}
								catch
								{
								}
							}
							if (num10 > 0.0)
							{
								uint[] array6 = new uint[3] { num7, num8, num9 };
								uint[] array7 = array6;
								uint[] array8 = array7;
								uint[] array9 = array8;
								foreach (uint num11 in array9)
								{
									if (num11 == 0 || num11 == GameTimeDid)
									{
										continue;
									}
									IProperty property5 = entity.GetProperty(num11);
									if (property5 != null)
									{
										double num12 = 0.0;
										try
										{
											num12 = Convert.ToDouble(property5.Value);
										}
										catch
										{
										}
										if (num12 > num10)
										{
											Log($"  Entity cooldown ACTIVE for [{text7}]: {num12 - num10:F1}s remaining");
										}
									}
								}
							}
						}
					}
					catch
					{
					}
				}
				list.Add(new HotbarAbility
				{
					Name = text7,
					BarId = (int)bar,
					SlotId = (int)slot,
					CommandValue = inputAction.GetValueOrDefault(),
					ReferenceDid = referenceDid,
					Index = i,
					CooldownMs = num5,
					BuffDurationMs = num6,
					CooldownTimerDid1 = num7,
					CooldownTimerDid2 = num8,
					CooldownTimerDid3 = num9
				});
				Log($"ScanHotbar: Bar{bar + 1} Slot{slot + 1} -> [{text7}] cmd=0x{inputAction.Value:X8} did=0x{referenceDid:X8} cd={((num5 > 0) ? $"{(double)num5 / 1000.0:F1}s" : "?")} dur={((num6 > 0) ? $"{(double)num6 / 1000.0:F0}s" : "?")}");
			}
			Log($"ScanHotbar: found {list.Count} hotbar slots");
		}
		catch (Exception ex4)
		{
			Log("ScanHotbar error: " + ex4.Message);
		}
		return list;
	}

	internal string TryResolveZoneName(uint did)
	{
		try
		{
			IGameDataProvider gameDataProvider = _gameDataProvider;
			IPropertyMaster propMaster = gameDataProvider?.PropertyMaster;
			return LookupNameFromDid(did, propMaster, gameDataProvider);
		}
		catch
		{
			return null;
		}
	}

	private string LookupNameFromDid(uint did, IPropertyMaster propMaster, IGameDataProvider gameData)
	{
		try
		{
			if (propMaster != null)
			{
				IPropertyCollection propertyCollection = propMaster.GetPropertyCollection(did);
				if (propertyCollection != null)
				{
					string text = ExtractTextFromProps(propertyCollection, did, propMaster);
					if (!string.IsNullOrEmpty(text))
					{
						return text;
					}
				}
			}
			if (gameData != null)
			{
				IPropertyCollection weenieProperties = gameData.GetWeenieProperties(did);
				if (weenieProperties != null)
				{
					string text2 = ExtractTextFromProps(weenieProperties, did, propMaster);
					if (!string.IsNullOrEmpty(text2))
					{
						return text2;
					}
				}
			}
		}
		catch
		{
		}
		return ChaseDidName(did, propMaster, gameData, new HashSet<uint> { did });
	}

	private string ChaseDidName(uint did, IPropertyMaster propMaster, IGameDataProvider gameData, HashSet<uint> visited)
	{
		try
		{
			IPropertyCollection propertyCollection = propMaster?.GetPropertyCollection(did);
			if (propertyCollection == null)
			{
				return null;
			}
			foreach (KeyValuePair<uint, IProperty> property in propertyCollection.Properties)
			{
				uint? num = null;
				if (property.Value is IUInt32Property { UInt32Value: not null } iUInt32Property)
				{
					num = iUInt32Property.UInt32Value.Value;
				}
				else if (property.Value is IInt32Property { Int32Value: var int32Value } int32Property && int32Value.HasValue && int32Value.Value > 0)
				{
					num = (uint)int32Property.Int32Value.Value;
				}
				if (!num.HasValue)
				{
					continue;
				}
				uint value = num.Value;
				if (value <= 16777216 || value > int.MaxValue || !visited.Add(value))
				{
					continue;
				}
				IPropertyCollection propertyCollection2 = propMaster?.GetPropertyCollection(value);
				string text;
				if (propertyCollection2 != null)
				{
					text = ExtractTextFromProps(propertyCollection2, value, propMaster);
					if (!string.IsNullOrEmpty(text))
					{
						Log($"  ChaseName: DID 0x{did:X8} prop[{property.Key}] -> 0x{value:X8} name=\"{text}\"");
						return text;
					}
				}
				if (gameData != null)
				{
					IPropertyCollection weenieProperties = gameData.GetWeenieProperties(value);
					if (weenieProperties != null)
					{
						text = ExtractTextFromProps(weenieProperties, value, propMaster);
						if (!string.IsNullOrEmpty(text))
						{
							Log($"  ChaseName: DID 0x{did:X8} prop[{property.Key}] -> weenie 0x{value:X8} name=\"{text}\"");
							return text;
						}
					}
				}
				text = ChaseDidName(value, propMaster, gameData, visited);
				if (string.IsNullOrEmpty(text))
				{
					continue;
				}
				Log($"  ChaseName: DID 0x{did:X8} -> 0x{value:X8} -> name=\"{text}\"");
				return text;
			}
		}
		catch
		{
		}
		return null;
	}

	private static string ExtractTextFromProps(IPropertyCollection props, uint did, IPropertyMaster propMaster)
	{
		try
		{
			if (!string.IsNullOrEmpty(props.Name))
			{
				string text = props.Name.Trim();
				if (!string.IsNullOrEmpty(text))
				{
					return text;
				}
			}
			IStringInfoProperty stringInfoProperty = props.GetStringInfoProperty(1u);
			if (stringInfoProperty != null)
			{
				string text2 = stringInfoProperty.GetText(propMaster, did, props);
				if (!string.IsNullOrEmpty(text2))
				{
					return text2;
				}
			}
			IStringProperty stringProperty = props.GetStringProperty(1u);
			if (stringProperty != null && !string.IsNullOrEmpty(stringProperty.StringValue))
			{
				return stringProperty.StringValue;
			}
		}
		catch
		{
		}
		return null;
	}

	private static string ExtractTextFromProps(IPropertyCollection props, uint did, IPropertyMaster propMaster, string label)
	{
		return ExtractTextFromProps(props, did, propMaster);
	}

	internal HudOverlayForm.ToggleStateInfo[] GetToggleHudStates()
	{
		lock (_lock)
		{
			int count = ToggleBuffs.Count;
			if (count == 0)
			{
				return Array.Empty<HudOverlayForm.ToggleStateInfo>();
			}
			HudOverlayForm.ToggleStateInfo[] array = new HudOverlayForm.ToggleStateInfo[count];
			for (int i = 0; i < count; i++)
			{
				RotationAbility rotationAbility = ToggleBuffs[i];
				uint? referenceDid = rotationAbility.ReferenceDid;
				bool active = ((referenceDid != 1879062547) ? (referenceDid.HasValue && _sessionToggleFired.Contains(referenceDid.Value)) : ((_gameDataProvider?.GetCurrentCharacter()?.InstanceProperties?.GetBytePropertyValue(268442925u)).GetValueOrDefault() > 0));
				array[i] = new HudOverlayForm.ToggleStateInfo
				{
					Name = (rotationAbility.Name ?? string.Empty),
					Active = active
				};
			}
			return array;
		}
	}

	private void CheckAutoAttack()
	{
		if (_config == null || !_config.AutoAttackEnabled)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if ((utcNow - _lastAutoAttackCheck).TotalMilliseconds < 2000.0)
		{
			return;
		}
		_lastAutoAttackCheck = utcNow;
		if ((utcNow - _lastMapChangeTime).TotalMilliseconds < 5000.0 || (utcNow - _lastManualAutoAttackToggle).TotalMilliseconds < 4000.0 || _autoAttackWasFiredThisZone)
		{
			return;
		}
		try
		{
			IEntity entity = _gameDataProvider?.GetCurrentCharacter();
			if (entity == null || (_lastAutoAttackToggleTime.HasValue && (DateTime.UtcNow - _lastAutoAttackToggleTime.Value).TotalSeconds < 15.0))
			{
				return;
			}
			bool flag = false;
			if ((entity.InstanceProperties?.GetBytePropertyValue(268442925u)).GetValueOrDefault() > 0)
			{
				flag = true;
			}
			if (!flag && _sessionToggleFired.Contains(1879062547u))
			{
				flag = true;
			}
			if (flag)
			{
				return;
			}
			IHotbarSlot hotbarSlot = _cachedAttackSlot;
			if (hotbarSlot == null || _cachedAttackSlotIndex < 0)
			{
				IHotbarSlot[] array = _gameDataProvider?.GetHotbarMap();
				if (array == null)
				{
					Log("CheckAutoAttack: hotbar null");
					return;
				}
				for (int i = 0; i < array.Length; i++)
				{
					if (array[i] != null && array[i].ReferenceDid == 1879062547)
					{
						hotbarSlot = (_cachedAttackSlot = array[i]);
						_cachedAttackSlotIndex = i;
						break;
					}
				}
			}
			if (hotbarSlot == null)
			{
				Log("CheckAutoAttack: Attack slot not found");
				return;
			}
			uint? inputAction = hotbarSlot.GetInputAction();
			if (!inputAction.HasValue || inputAction.Value == 0)
			{
				Log("CheckAutoAttack: slot found but no action");
				return;
			}
			_gameDataProvider?.SendInput(inputAction.Value);
			_autoAttackWasFiredThisZone = true;
			_sessionToggleFired.Add(1879062547u);
			_lastAutoAttackToggleTime = DateTime.UtcNow;
			Log("CheckAutoAttack: toggled ON");
		}
		catch (Exception ex)
		{
			Log("AutoAttack error: " + ex.Message);
		}
	}

	private void AutocastTick()
	{
		if (_lastSuccessfulTick.HasValue && (DateTime.UtcNow - _lastSuccessfulTick.Value).TotalSeconds > 15)
		{
			_autocastTickRunning = false;
		}
		if (_autocastTickRunning || _paused)
		{
			return;
		}
		_autocastTickRunning = true;
		try
		{
			DateTime utcNow = DateTime.UtcNow;
			CheckAutoAttack();
			if (ToggleBuffs.Count > 0)
			{
				IHotbarSlot[] array = null;
				for (int i = 0; i < ToggleBuffs.Count; i++)
				{
					RotationAbility rotationAbility = ToggleBuffs[i];
					if (!rotationAbility.Autocast || !rotationAbility.ReferenceDid.HasValue || rotationAbility.ReferenceDid == 0 || (_config.AutoAttackEnabled && string.Equals(rotationAbility.Name?.Trim(), "Attack", StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}
					uint value = rotationAbility.ReferenceDid.Value;
					if ((utcNow - rotationAbility.LastFired).TotalMilliseconds < 500.0 || (utcNow - _lastHotbarUsageTime).TotalMilliseconds < 1000.0 || (utcNow - _lastMapChangeTime).TotalMilliseconds < 2000.0)
					{
						continue;
					}
					if (rotationAbility.ToggleStatePropertyDid != 0)
					{
						IEntity entity = _gameDataProvider?.GetCurrentCharacter();
						if (entity != null)
						{
							uint num = rotationAbility.ToggleStatePropertyDid;
							if (num == 268484148)
							{
								num = 268442925u;
							}
							IProperty property = entity.GetProperty(num);
							if (property != null && rotationAbility.ToggleExpectedValue != null && string.Equals(Convert.ToString(property.Value), rotationAbility.ToggleExpectedValue, StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}
						}
					}
					if (_sessionToggleFired.Contains(value))
					{
						continue;
					}
					IHotbarSlot hotbarSlot = null;
					if (rotationAbility.CachedHotbarIndex >= 0)
					{
						if (array == null)
						{
							array = _gameDataProvider?.GetHotbarMap();
						}
						if (array != null && rotationAbility.CachedHotbarIndex < array.Length)
						{
							IHotbarSlot hotbarSlot2 = array[rotationAbility.CachedHotbarIndex];
							if (hotbarSlot2 != null && hotbarSlot2.ReferenceDid == value)
							{
								hotbarSlot = hotbarSlot2;
							}
						}
					}
					if (hotbarSlot == null)
					{
						if (array == null)
						{
							array = _gameDataProvider?.GetHotbarMap();
						}
						if (array != null)
						{
							for (int j = 0; j < array.Length; j++)
							{
								IHotbarSlot hotbarSlot3 = array[j];
								if (hotbarSlot3 != null && hotbarSlot3.ReferenceDid == value)
								{
									hotbarSlot = hotbarSlot3;
									rotationAbility.CachedHotbarIndex = j;
									break;
								}
							}
						}
					}
					if (hotbarSlot != null)
					{
						uint? inputAction = hotbarSlot.GetInputAction();
						if (inputAction.HasValue && inputAction.Value != 0)
						{
							_gameDataProvider?.SendInput(inputAction.Value);
							rotationAbility.LastFired = utcNow;
							_lastHotbarUsageTime = utcNow;
							_sessionToggleFired.Add(value);
							Log($"TOGGLE [{i}] [{rotationAbility.KeyLabel}] {rotationAbility.Name} — fired via hotbar");
						}
					}
				}
			}
			if (IsInSafeArea())
			{
				_pendingBuffTargetId = null;
				_pendingBuffAbility = null;
				_pendingBuffRevertId = null;
				return;
			}
			if (_pendingBuffAbility != null)
			{
				if (_pendingBuffTargetId.HasValue)
				{
					ulong? num2 = _gameDataProvider?.GetCurrentCharacterId();
					bool flag = _pendingBuffTargetId == num2;
					bool flag2 = false;
					if (flag)
					{
						IEntity entity2 = _gameDataProvider?.GetCurrentCharacter();
						flag2 = entity2 == null || !entity2.HitPoints_Current.HasValue || entity2.HitPoints_Current.Value <= 0;
					}
					else
					{
						IReadOnlyList<IPartyMember> readOnlyList = _gameDataProvider?.GetPartyMembers();
						if (readOnlyList != null)
						{
							foreach (IPartyMember item in readOnlyList)
							{
								if (item.Id == _pendingBuffTargetId.Value)
								{
									flag2 = !item.IsValid() || !item.Connected || item.HitPoints <= 0;
									break;
								}
							}
						}
					}
					if (!flag2)
					{
						uint valueOrDefault = _pendingBuffAbility.ReferenceDid.GetValueOrDefault();
						bool flag3 = false;
						if (flag)
						{
							if (_pendingBuffAbility.ToggleStatePropertyDid != 0)
							{
								IEntity entity3 = _gameDataProvider?.GetCurrentCharacter();
								if (entity3 != null)
								{
									IProperty property2 = entity3.GetProperty(_pendingBuffAbility.ToggleStatePropertyDid);
									flag3 = property2 != null && string.Equals(Convert.ToString(property2.Value), _pendingBuffAbility.ToggleExpectedValue, StringComparison.OrdinalIgnoreCase);
								}
							}
							if (!flag3 && valueOrDefault != 0)
							{
								IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _gameDataProvider?.GetActiveEffects();
								if (readOnlyDictionary != null)
								{
									uint cachedEffectDid = GameCache.GetCachedEffectDid(valueOrDefault);
									foreach (KeyValuePair<int, IActiveEffect> item2 in readOnlyDictionary)
									{
										if (item2.Value.EffectDid == (int)valueOrDefault || (cachedEffectDid != 0 && item2.Value.EffectDid == (int)cachedEffectDid))
										{
											flag3 = true;
											break;
										}
									}
								}
							}
						}
						else if (_pendingBuffAbility.ToggleStatePropertyDid != 0)
						{
							IEntity entity4 = _gameDataProvider?.GetSelectedObject();
							if (entity4 != null)
							{
								IProperty property3 = entity4.GetProperty(_pendingBuffAbility.ToggleStatePropertyDid);
								flag3 = property3 != null && string.Equals(Convert.ToString(property3.Value), _pendingBuffAbility.ToggleExpectedValue, StringComparison.OrdinalIgnoreCase);
							}
						}
						if (flag3)
						{
							if (!flag)
							{
								string key = PartyBuffKey(valueOrDefault, _pendingBuffTargetId.Value, _pendingBuffAbility.Name);
								_lastPartyBuffTime[key] = utcNow;
							}
						}
						else if (FireAbility(_pendingBuffAbility, skipCooldown: true))
						{
							_pendingBuffAbility.LastFired = utcNow;
							_lastBuffFireTime = utcNow;
							if (!flag)
							{
								string key2 = PartyBuffKey(valueOrDefault, _pendingBuffTargetId.Value, _pendingBuffAbility.Name);
								_lastPartyBuffTime[key2] = utcNow;
							}
							Log("BUFF FIRE [" + _pendingBuffAbility.Name + "] on " + ((_pendingBuffTargetId == num2) ? "self" : "party member"));
						}
					}
				}
				if (_pendingBuffRevertId.HasValue)
				{
					_gameDataProvider?.SelectObject(_pendingBuffRevertId.Value);
				}
				_pendingBuffTargetId = null;
				_pendingBuffAbility = null;
				_pendingBuffRevertId = null;
			}
			if (_pendingEmergencyTargetId.HasValue && _pendingEmergencyAbility != null)
			{
				try
				{
					if (FireAbility(_pendingEmergencyAbility, skipCooldown: true))
					{
						_pendingEmergencyAbility.LastFired = utcNow;
						Log("EMERGENCY HEAL [" + _pendingEmergencyAbility.Name + "]");
						if (_pendingTargetRevertId.HasValue)
							_pendingTargetRevertTime = utcNow;
						_pendingEmergencyTargetId = null;
						_pendingEmergencyAbility = null;
					}
					else
					{
						if (_pendingTargetRevertId.HasValue)
							_gameDataProvider?.SelectObject(_pendingTargetRevertId.Value);
						_pendingEmergencyTargetId = null;
						_pendingEmergencyAbility = null;
						_pendingTargetRevertId = null;
					}
				}
				catch (Exception ex)
				{
					Log("EMERGENCY FIRE ERROR: " + ex.Message);
					_pendingEmergencyTargetId = null;
					_pendingEmergencyAbility = null;
				}
			}
			else if (_pendingTargetRevertId.HasValue && (utcNow - _pendingTargetRevertTime).TotalMilliseconds >= 1500.0)
			{
				_gameDataProvider?.SelectObject(_pendingTargetRevertId.Value);
				Log("EMERGENCY HEAL reverted target");
				_pendingTargetRevertId = null;
			}
			if (Emergency.Count > 0 && !_pendingEmergencyTargetId.HasValue)
			{
				bool _skipMounted = _config.SkipWhileMounted && (_gameDataProvider?.GetCurrentCharacter()?.GetProperty(3872u) != null);
				if (_skipMounted)
				{
					Log("EMERGENCY SKIP: mounted");
					goto IL_SkipEmergency;
				}
				Log($"EMERGENCY CHECK: {Emergency.Count} abilities, not pending");
				try
				{
					IEntity entity5 = _gameDataProvider?.GetCurrentCharacter();
					if (entity5 != null)
					{
						int? hitPoints_Current = entity5.HitPoints_Current;
						int? hitPoints_Max = entity5.HitPoints_Max;
						if (hitPoints_Current.HasValue && hitPoints_Max.HasValue && hitPoints_Max.Value > 0 && hitPoints_Current.Value > 0)
						{
							double num3 = (double)hitPoints_Current.Value / (double)hitPoints_Max.Value * 100.0;
							for (int num4 = 0; num4 < Emergency.Count; num4++)
							{
								RotationAbility rotationAbility2 = Emergency[num4];
								Log($"EMERGENCY SELF CHECK [{num4}] {rotationAbility2.Name}: auto={rotationAbility2.Autocast} ref={rotationAbility2.ReferenceDid} scope={rotationAbility2.HealScope} hp%={num3:F0} threshold={rotationAbility2.CooldownMs}");
								if (rotationAbility2.Autocast && rotationAbility2.ReferenceDid.HasValue && rotationAbility2.ReferenceDid != 0 && (!(rotationAbility2.HealScope != "Self") || !(rotationAbility2.HealScope != "Both")) && !(utcNow - rotationAbility2.LastFired < TimeSpan.FromMilliseconds(500.0)) && rotationAbility2.CooldownMs > 0 && !(num3 > (double)rotationAbility2.CooldownMs))
								{
									ulong? num5 = _gameDataProvider?.GetSelectedObjectId();
									ulong? num6 = _gameDataProvider?.GetCurrentCharacterId();
									if (num6.HasValue && num5 != num6)
									{
										_gameDataProvider?.SelectObject(num6.Value);
									}
									_pendingEmergencyTargetId = num6;
									_pendingEmergencyAbility = rotationAbility2;
									_pendingTargetRevertId = ((num5.HasValue && num5.Value != num6) ? num5 : ((ulong?)null));
									Log($"EMERGENCY SELF [{num4}] HP={hitPoints_Current}/{hitPoints_Max} ({num3:F0}%) targetting self - will fire next tick");
									goto IL_1bed;
								}
							}
						}
					}
				}
				catch (Exception ex)
				{
					Log("Emergency check error: " + ex.Message);
				}
				if (_config.EmergencyPartyHeal && Emergency.Count > 0 && !_pendingEmergencyTargetId.HasValue)
				{
					try
					{
						IReadOnlyList<IPartyMember> readOnlyList2 = _gameDataProvider?.GetPartyMembers();
						if (readOnlyList2 != null && readOnlyList2.Count > 0)
						{
							Log($"PARTY HEAL: {readOnlyList2.Count} party members found");
							int[] array2 = new int[Emergency.Count];
							for (int k = 0; k < Emergency.Count; k++)
							{
								RotationAbility rotationAbility3 = Emergency[k];
								if (rotationAbility3.PartyCountThreshold <= 0 || rotationAbility3.PartyHpThreshold <= 0)
								{
									continue;
								}
								int num7 = 0;
								foreach (IPartyMember item3 in readOnlyList2)
								{
									if (item3.IsValid() && item3.Connected && item3.MaxHitPoints > 0 && item3.HitPoints > 0)
									{
										double val = (double)item3.HitPoints / (double)item3.MaxHitPoints * 100.0;
										double num8 = Math.Min(val, 100.0);
										if (num8 <= (double)rotationAbility3.PartyHpThreshold)
										{
											num7++;
										}
									}
								}
								array2[k] = num7;
								Log($"PARTY COUNT [{k}] {rotationAbility3.Name}: {num7} below {rotationAbility3.PartyHpThreshold}% (need {rotationAbility3.PartyCountThreshold})");
							}
							Vector3? myPos = _gameDataProvider?.GetCurrentCharacter()?.Location?.Position;
							Dictionary<string, ulong> partyEntityIds = new Dictionary<string, ulong>();
							try
							{
								foreach (IEntity e in _gameDataProvider?.GetEntities() ?? new List<IEntity>())
								{
									if (!string.IsNullOrEmpty(e.Name))
										partyEntityIds[e.Name] = e.InstanceId;
								}
							}
							catch { }
						foreach (IPartyMember item4 in readOnlyList2)
							{
								if (!item4.IsValid() || !item4.Connected || item4.MaxHitPoints <= 0 || item4.HitPoints <= 0)
								{
									continue;
								}
								if (_config.HealRange > 0.0 && myPos.HasValue && item4.Location?.Position is Vector3 pPos)
								{
									double dx = myPos.Value.X - pPos.X;
									double dy = myPos.Value.Y - pPos.Y;
									if (Math.Sqrt(dx * dx + dy * dy) > _config.HealRange)
										continue;
								}
								double num9 = (double)item4.HitPoints / (double)item4.MaxHitPoints * 100.0;
								double num10 = Math.Min(num9, 100.0);
								for (int num11 = 0; num11 < Emergency.Count; num11++)
								{
									RotationAbility rotationAbility4 = Emergency[num11];
									if (rotationAbility4.Autocast && rotationAbility4.ReferenceDid.HasValue && rotationAbility4.ReferenceDid != 0 && (!(rotationAbility4.HealScope != "Party") || !(rotationAbility4.HealScope != "Both")) && rotationAbility4.PartyHpThreshold > 0 && !(utcNow - rotationAbility4.LastFired < TimeSpan.FromMilliseconds(500.0)) && !(num10 > (double)rotationAbility4.PartyHpThreshold) && (rotationAbility4.PartyCountThreshold <= 0 || array2[num11] >= rotationAbility4.PartyCountThreshold))
									{
										if (!partyEntityIds.TryGetValue(item4.Name, out var instId))
											continue;
										ulong? curSel = _gameDataProvider?.GetSelectedObjectId();
										if (curSel != instId)
											_gameDataProvider?.SelectObject(instId);
										_pendingEmergencyTargetId = instId;
										_pendingEmergencyAbility = rotationAbility4;
										_pendingTargetRevertId = (curSel.HasValue && curSel.Value != instId) ? curSel : null;
										Log($"EMERGENCY PARTY targetting {item4.Name} HP={item4.HitPoints}/{item4.MaxHitPoints} ({num9:F1}% raw->{num10:F0}%) - will fire next tick");
										goto IL_1bed;
									}
								}
							}
						}
						else
						{
							Log("PARTY HEAL: no party members found");
						}
					}
					catch (Exception ex2)
					{
						Log("Party heal check error: " + ex2.Message);
					}
				}
			}
			IL_SkipEmergency:;
			if ((_config.AutoTargetFurthestEnemy || _config.AutoTargetClosestEnemy) && (utcNow - _lastAutoTargetCheck).TotalMilliseconds >= 500.0)
			{
				_lastAutoTargetCheck = utcNow;
				if (_config.AutoTargetFurthestEnemy)
				{
					AutoTargetFurthestEnemy();
				}
				else if (_config.AutoTargetClosestEnemy)
				{
					AutoTargetClosestEnemy();
				}
			}
			if (Rotation.Count > 0)
			{
				bool flag4 = _config?.AutocastRotationOnTarget ?? false;
				if (_config.MeleeRangeEnabled && _logCount++ % 10 == 0)
				{
					double? distanceToTarget = GetDistanceToTarget();
					if (distanceToTarget.HasValue)
					{
						Log($"MELEE DISTANCE: {distanceToTarget.Value:F1}u to target");
					}
				}
				List<RotationAbility> list = new List<RotationAbility>();
				for (int l = 0; l < Rotation.Count; l++)
				{
					RotationAbility rotationAbility5 = Rotation[l];
					if (!rotationAbility5.Autocast || !rotationAbility5.ReferenceDid.HasValue || rotationAbility5.ReferenceDid == 0 || IsOnCooldown(rotationAbility5) || (utcNow - rotationAbility5.LastFired).TotalMilliseconds < 150.0)
					{
						continue;
					}
					if (flag4)
					{
						IDdoGameDataProvider gameDataProvider = _gameDataProvider;
						if (gameDataProvider != null && !IsHostileOrBreakable(gameDataProvider))
						{
							continue;
						}
					}
					if (rotationAbility5.MinNearbyEnemies > 0)
					{
						int num13 = CountNearbyEnemies();
						if (num13 < rotationAbility5.MinNearbyEnemies)
						{
							Log($"MIN NEARBY [{l}] [{rotationAbility5.KeyLabel}] {rotationAbility5.Name} — nearby={num13} need={rotationAbility5.MinNearbyEnemies}");
							continue;
						}
					}
					if (rotationAbility5.TargetHpThreshold > 0)
					{
						IEntity entity6 = _gameDataProvider?.GetSelectedObject();
						if (entity6 != null && IsHostileEntity(entity6) && entity6.HitPoints_Max.HasValue && entity6.HitPoints_Max > 0 && entity6.HitPoints_Current.HasValue)
						{
							int num14 = (int)((double)entity6.HitPoints_Current.Value / (double)entity6.HitPoints_Max.Value * 100.0);
							if (num14 > rotationAbility5.TargetHpThreshold)
							{
								Log($"TARGET HP [{l}] [{rotationAbility5.KeyLabel}] {rotationAbility5.Name} — target HP={num14}% threshold={rotationAbility5.TargetHpThreshold}%");
								continue;
							}
						}
					}
					if (rotationAbility5.MinSpThreshold > 0)
					{
						IEntity entity7 = _gameDataProvider?.GetCurrentCharacter();
						if (entity7 != null && entity7.SpellPoints_Max.HasValue && entity7.SpellPoints_Max > 0 && entity7.SpellPoints_Current.HasValue)
						{
							int num15 = (int)((double)entity7.SpellPoints_Current.Value / (double)entity7.SpellPoints_Max.Value * 100.0);
							if (num15 < rotationAbility5.MinSpThreshold)
							{
								Log($"LOW SP [{l}] [{rotationAbility5.KeyLabel}] {rotationAbility5.Name} — SP={num15}% need={rotationAbility5.MinSpThreshold}%");
								continue;
							}
						}
					}
					double distance;
					if (!MeetsConditions(rotationAbility5))
					{
						Log($"CONDITION FAIL [{l}] [{rotationAbility5.KeyLabel}] {rotationAbility5.Name} — required buff/debuff not met");
					}
					else if (_config.MeleeRangeEnabled && !IsInMeleeRange(out distance))
					{
						Log($"MELEE OUT OF RANGE [{l}] [{rotationAbility5.KeyLabel}] {rotationAbility5.Name} dist={distance:F1}");
					}
					else
					{
						list.Add(rotationAbility5);
					}
				}
				RotationAbility rotationAbility6 = (_config.SmartOrder ? PickBestAbility(list) : list.OrderBy(a => a.LastFired).FirstOrDefault());
				if (rotationAbility6 != null && FireAbility(rotationAbility6, skipCooldown: true))
				{
					rotationAbility6.LastFired = utcNow;
					Log("AUTOCAST [" + rotationAbility6.KeyLabel + "] " + rotationAbility6.Name + (_config.SmartOrder ? " (smart)" : ""));
				}
			}
			goto IL_1bed;
			IL_1bd5:
			BuffManager?.MaintenanceTick();
			return;
			IL_1bed:
			if (BuffOrder.Count > 0)
			{
				if (_config.BuffDisablePublic)
				{
					bool flag5 = true;
					try
					{
						bool? flag6 = _gameDataProvider?.InTown();
						bool? flag7 = flag6;
						int num16;
						if (!flag7.HasValue)
						{
							IDdoGameDataProvider gameDataProvider2 = _gameDataProvider;
							if (gameDataProvider2 != null)
							{
								IEntity currentCharacter = gameDataProvider2.GetCurrentCharacter();
								if (currentCharacter != null)
								{
									ILocation location = currentCharacter.Location;
									if (location != null && location.Indoors)
									{
										num16 = 0;
										goto IL_1cb6;
									}
								}
							}
							num16 = 1;
							goto IL_1cb6;
						}
						int num17 = ((flag7 == true) ? 1 : 0);
						goto IL_1cab;
						IL_1cab:
						flag5 = (byte)num17 != 0;
						goto end_IL_1c1e;
						IL_1cb6:
						num17 = num16;
						goto IL_1cab;
						end_IL_1c1e:;
					}
					catch
					{
					}
					if (flag5)
					{
						goto IL_1bd5;
					}
				}
				if (_config.BuffDisableWilderness)
				{
					try
					{
						bool? wInTown = _gameDataProvider?.InTown();
						if ((!wInTown.HasValue || !wInTown.Value) && _gameDataProvider?.GetInstanceQuestDid().GetValueOrDefault() == 0)
						{
							IEntity wChar = _gameDataProvider?.GetCurrentCharacter();
							if (wChar != null && wChar.Location != null && !wChar.Location.Indoors)
							{
								Log("BUFF SKIP: wilderness area");
								goto IL_1bd5;
							}
						}
					}
					catch { }
				}
				if ((utcNow - _lastBuffFireTime).TotalMilliseconds >= 1500.0)
				{
					for (int m = 0; m < BuffOrder.Count; m++)
					{
						int num18 = (_autoBuffIndex + m) % BuffOrder.Count;
						RotationAbility rotationAbility7 = BuffOrder[num18];
						if ((!rotationAbility7.Autocast && !rotationAbility7.BuffOnMissing) || !rotationAbility7.ReferenceDid.HasValue || rotationAbility7.ReferenceDid == 0)
						{
							continue;
						}
						bool flag8 = false;
						if (rotationAbility7.BuffOnMissing)
						{
							bool flag9 = false;
							if (rotationAbility7.ToggleStatePropertyDid != 0)
							{
								IEntity entity8 = _gameDataProvider?.GetCurrentCharacter();
								if (entity8 != null)
								{
									IProperty property4 = entity8.GetProperty(rotationAbility7.ToggleStatePropertyDid);
									flag9 = property4 != null && string.Equals(Convert.ToString(property4.Value), rotationAbility7.ToggleExpectedValue, StringComparison.OrdinalIgnoreCase);
								}
							}
							else
							{
								flag9 = IsToggleActive(rotationAbility7.ReferenceDid.Value, rotationAbility7.Name);
							}
							flag8 = !flag9;
							if (flag9 && (!rotationAbility7.RequiresTarget || (!(rotationAbility7.TargetType == "Both") && !(rotationAbility7.TargetType == "A Party Member"))))
							{
								continue;
							}
						}
						else
						{
							long num19 = (long)rotationAbility7.DurationMinutes * 60000L + (long)rotationAbility7.DurationSeconds * 1000L;
							if (num19 <= 0)
							{
								TryLearnBuffDuration(rotationAbility7);
								num19 = (long)rotationAbility7.DurationMinutes * 60000L + (long)rotationAbility7.DurationSeconds * 1000L;
							}
							if (num19 <= 0)
							{
								if (rotationAbility7.LastFired != DateTime.MinValue)
								{
									continue;
								}
							}
							else
							{
								double totalMilliseconds = (utcNow - rotationAbility7.LastFired).TotalMilliseconds;
								if (totalMilliseconds < (double)num19)
								{
									continue;
								}
							}
						}
						if (IsOnCooldown(rotationAbility7))
						{
							continue;
						}
						if (rotationAbility7.RequiresTarget)
						{
							ulong? num20 = _gameDataProvider?.GetSelectedObjectId();
							ulong? num21 = _gameDataProvider?.GetCurrentCharacterId();
							IEntity entity9 = _gameDataProvider?.GetCurrentCharacter();
							if (entity9 == null || !entity9.HitPoints_Current.HasValue || entity9.HitPoints_Current.Value <= 0)
							{
								Log("SKIP BUFF [" + rotationAbility7.Name + "] — self is dead");
								continue;
							}
							ulong? num22 = null;
							if (rotationAbility7.TargetType == "Myself" || (rotationAbility7.TargetType == "Both" && flag8))
							{
								num22 = num21;
							}
							else if (rotationAbility7.TargetType == "A Party Member" || rotationAbility7.TargetType == "Both")
							{
								IReadOnlyList<IPartyMember> readOnlyList3 = _gameDataProvider?.GetPartyMembers();
								uint valueOrDefault2 = rotationAbility7.ReferenceDid.GetValueOrDefault();
								DateTime dateTime = utcNow;
								if (readOnlyList3 != null)
								{
									bool flag10 = false;
									if (rotationAbility7.TargetType == "A Party Member" && valueOrDefault2 != 0)
									{
										IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary2 = _gameDataProvider?.GetActiveEffects();
										if (readOnlyDictionary2 != null)
										{
											uint cachedEffectDid2 = GameCache.GetCachedEffectDid(valueOrDefault2);
											foreach (KeyValuePair<int, IActiveEffect> item5 in readOnlyDictionary2)
											{
												if (item5.Value.EffectDid == (int)valueOrDefault2 || (cachedEffectDid2 != 0 && item5.Value.EffectDid == (int)cachedEffectDid2))
												{
													flag10 = true;
													break;
												}
											}
										}
									}
									if (flag10)
									{
										foreach (IPartyMember item6 in readOnlyList3)
										{
											if (item6.IsValid() && item6.Connected && item6.HitPoints > 0 && item6.MaxHitPoints > 0)
											{
												string key3 = PartyBuffKey(valueOrDefault2, item6.Id, rotationAbility7.Name);
												_lastPartyBuffTime[key3] = dateTime;
											}
										}
									}
									else
									{
										Dictionary<string, ulong> buffEntityIds = new Dictionary<string, ulong>();
										try
										{
											foreach (IEntity e2 in _gameDataProvider?.GetEntities() ?? new List<IEntity>())
											{
												if (!string.IsNullOrEmpty(e2.Name))
													buffEntityIds[e2.Name] = e2.InstanceId;
											}
										}
										catch { }
										Vector3? myPos2 = entity9?.Location?.Position;
										foreach (IPartyMember item7 in readOnlyList3)
										{
											if (!item7.IsValid() || !item7.Connected || item7.HitPoints <= 0 || item7.MaxHitPoints <= 0)
											{
												continue;
											}
											if (_config.HealRange > 0.0 && myPos2.HasValue && item7.Location?.Position is Vector3 pPos2)
											{
												double dx2 = myPos2.Value.X - pPos2.X;
												double dy2 = myPos2.Value.Y - pPos2.Y;
												if (Math.Sqrt(dx2 * dx2 + dy2 * dy2) > _config.HealRange)
													continue;
											}
											string key4 = PartyBuffKey(valueOrDefault2, item7.Id, rotationAbility7.Name);
											if (_lastPartyBuffTime.TryGetValue(key4, out var value2))
											{
												double num23 = ((rotationAbility7.BuffDurationMs > 0) ? rotationAbility7.BuffDurationMs : 300000);
												if ((dateTime - value2).TotalMilliseconds < num23 * 0.8)
												{
													continue;
												}
											}
											num22 = buffEntityIds.TryGetValue(item7.Name, out var buffInstId) ? buffInstId : item7.Id;
											break;
										}
									}
								}
							}
							if (num22.HasValue)
							{
								if (num22 != num20)
								{
									_gameDataProvider?.SelectObject(num22.Value);
								}
								_pendingBuffTargetId = num22;
								_pendingBuffAbility = rotationAbility7;
								_pendingBuffRevertId = ((num20.HasValue && num20 != num22) ? num20 : ((ulong?)null));
								Log($"BUFF TARGET [{rotationAbility7.Name}] -> {((num22 == num21) ? "self" : "party member")} — will fire next tick");
								break;
							}
							Log("SKIP BUFF [" + rotationAbility7.Name + "] — no valid target");
						}
						else if (FireAbility(rotationAbility7, skipCooldown: true))
						{
							_autoBuffIndex = (num18 + 1) % BuffOrder.Count;
							rotationAbility7.LastFired = utcNow;
							_lastBuffFireTime = utcNow;
							long num24 = (long)rotationAbility7.DurationMinutes * 60000L + (long)rotationAbility7.DurationSeconds * 1000L;
							Log($"AUTOCAST [Buff] [{rotationAbility7.KeyLabel}] {rotationAbility7.Name} ({(rotationAbility7.BuffOnMissing ? "on-missing" : ("every " + ((num24 > 0) ? $"{num24 / 1000}s" : "?")))})");
							if (!rotationAbility7.BuffOnMissing && num24 <= 0)
							{
								TryLearnBuffDuration(rotationAbility7);
							}
							break;
						}
					}
				}
			}
			goto IL_1bd5;
		}
		catch (Exception ex3)
		{
			Log("Autocast error: " + ex3.Message);
		}
		finally
		{
			_autocastTickRunning = false;
			_lastSuccessfulTick = DateTime.UtcNow;
		}
	}

	private static string StripAndLower(string s)
	{
		return new string(s.Where((char c) => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();
	}

	private void RefreshAbilityCommands()
	{
		try
		{
			IHotbarSlot[] array = _gameDataProvider?.GetHotbarMap();
			if (array == null || array.Length == 0)
			{
				return;
			}
			lock (_lock)
			{
				IPropertyMaster propertyMaster = _gameDataProvider?.PropertyMaster;
				IGameDataProvider gameDataProvider = _gameDataProvider;
				IHotbarSlot[] array2 = array;
				IHotbarSlot[] array3 = array2;
				IHotbarSlot[] array4 = array3;
				foreach (IHotbarSlot hotbarSlot in array4)
				{
					if (hotbarSlot == null)
					{
						continue;
					}
					uint referenceDid = hotbarSlot.ReferenceDid;
					if (referenceDid == 0)
					{
						continue;
					}
					uint? inputAction = hotbarSlot.GetInputAction();
					if (!inputAction.HasValue || inputAction.Value == 0)
					{
						continue;
					}
					if (!_didNameCache.ContainsKey(referenceDid) && propertyMaster != null)
					{
						string value = LookupNameFromDid(referenceDid, propertyMaster, gameDataProvider);
						if (!string.IsNullOrEmpty(value))
						{
							_didNameCache[referenceDid] = value;
						}
					}
					if (_didNameCache.TryGetValue(referenceDid, out var value2))
					{
						_nameCmdMap[value2] = inputAction.Value;
						string text = StripAndLower(value2);
						if (text != value2)
						{
							_nameCmdMap[text] = inputAction.Value;
						}
					}
					foreach (RotationAbility item in Rotation)
					{
						if (item.ReferenceDid == referenceDid)
						{
							item.CommandValue = inputAction.Value;
						}
					}
					foreach (RotationAbility item2 in BuffOrder)
					{
						if (item2.ReferenceDid == referenceDid)
						{
							item2.CommandValue = inputAction.Value;
						}
					}
					foreach (RotationAbility toggleBuff in ToggleBuffs)
					{
						if (toggleBuff.ReferenceDid == referenceDid)
						{
							toggleBuff.CommandValue = inputAction.Value;
						}
					}
				}
				if (BuffManager == null)
				{
					return;
				}
				IHotbarSlot[] array5 = array;
				IHotbarSlot[] array6 = array5;
				IHotbarSlot[] array7 = array6;
				foreach (IHotbarSlot hotbarSlot2 in array7)
				{
					if (hotbarSlot2 == null)
					{
						continue;
					}
					uint referenceDid2 = hotbarSlot2.ReferenceDid;
					if (referenceDid2 == 0)
					{
						continue;
					}
					uint? inputAction2 = hotbarSlot2.GetInputAction();
					if (!inputAction2.HasValue || inputAction2.Value == 0)
					{
						continue;
					}
					foreach (ToggledStanceConfig toggledStance in BuffManager.Profile.ToggledStances)
					{
						if (toggledStance.ActionDid == referenceDid2)
						{
							toggledStance.CommandValue = inputAction2.Value;
						}
					}
				}
			}
		}
		catch
		{
		}
	}

	private bool IsInSafeArea()
	{
		try
		{
			IDdoGameDataProvider gameDataProvider = _gameDataProvider;
			if (gameDataProvider == null)
			{
				return false;
			}
			if (_progressActive)
			{
				return true;
			}
			bool? flag = gameDataProvider.InTown();
			if (flag.HasValue && flag.Value)
			{
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	internal double? GetDistanceToTarget()
	{
		try
		{
			IEntity entity = _gameDataProvider?.GetCurrentCharacter();
			if (entity == null)
			{
				return null;
			}
			IEntity entity2 = _gameDataProvider?.GetSelectedObject();
			if (entity2 == null)
			{
				return null;
			}
			Vector3? vector = entity.Location?.Position;
			Vector3? vector2 = entity2.Location?.Position;
			if (!vector.HasValue || !vector2.HasValue)
			{
				return null;
			}
			Vector3 value = vector.Value;
			Vector3 value2 = vector2.Value;
			double num = value.X - value2.X;
			double num2 = value.Y - value2.Y;
			return Math.Sqrt(num * num + num2 * num2);
		}
		catch
		{
			return null;
		}
	}

	internal bool IsInMeleeRange(out double distance)
	{
		double? distanceToTarget = GetDistanceToTarget();
		if (!distanceToTarget.HasValue)
		{
			distance = -1.0;
			return false;
		}
		distance = distanceToTarget.Value;
		return distance <= 1.5;
	}

	internal bool IsInMeleeRange()
	{
		double distance;
		return IsInMeleeRange(out distance);
	}

	internal void AutoTargetFurthestEnemy()
	{
		try
		{
			IEntity entity = _gameDataProvider?.GetCurrentCharacter();
			if (entity == null || !(entity.Location?.Position).HasValue)
			{
				return;
			}
			Vector3 value = entity.Location.Position.Value;
			double num = _config.AutoTargetMaxRange;
			double num2 = -1.0;
			IEntity entity2 = null;
			IReadOnlyCollection<IEntity> readOnlyCollection = _gameDataProvider?.GetEntities();
			if (readOnlyCollection != null && readOnlyCollection.Count > 0)
			{
				foreach (IEntity item in readOnlyCollection)
				{
					if (item == null || item.InstanceId == 0L || item.InstanceId == entity.InstanceId || !IsHostileEntity(item) || item.HitPoints_Current.GetValueOrDefault() <= 0)
					{
						continue;
					}
					Vector3? vector = item.Location?.Position;
					if (vector.HasValue)
					{
						double num3 = Distance(value, vector.Value);
						if ((!(num > 0.0) || !(num3 > num)) && num3 > num2)
						{
							num2 = num3;
							entity2 = item;
						}
					}
				}
			}
			if (entity2 == null)
			{
				IEntity entity3 = _gameDataProvider?.GetSelectedObject();
				if (entity3 != null && entity3.InstanceId != entity.InstanceId && entity3.InstanceId != 0 && IsHostileEntity(entity3) && entity3.HitPoints_Current.GetValueOrDefault() > 0)
				{
					Vector3? vector2 = entity3.Location?.Position;
					if (vector2.HasValue)
					{
						double num4 = Distance(value, vector2.Value);
						if (num <= 0.0 || num4 <= num)
						{
							entity2 = entity3;
						}
					}
				}
			}
			if (entity2 != null)
			{
				ulong? curSelId = _gameDataProvider?.GetSelectedObjectId();
				if (curSelId != entity2.InstanceId)
					_gameDataProvider?.SelectObject(entity2.InstanceId);
			}
		}
		catch
		{
		}
	}

	internal void AutoTargetClosestEnemy()
	{
		try
		{
			IEntity entity = _gameDataProvider?.GetCurrentCharacter();
			if (entity == null || !(entity.Location?.Position).HasValue)
			{
				return;
			}
			Vector3 value = entity.Location.Position.Value;
			double num = _config.AutoTargetMaxRange;
			double num2 = double.MaxValue;
			IEntity entity2 = null;
			IReadOnlyCollection<IEntity> readOnlyCollection = _gameDataProvider?.GetEntities();
			if (readOnlyCollection != null && readOnlyCollection.Count > 0)
			{
				foreach (IEntity item in readOnlyCollection)
				{
					if (item == null || item.InstanceId == 0L || item.InstanceId == entity.InstanceId || !IsHostileEntity(item) || item.HitPoints_Current.GetValueOrDefault() <= 0)
					{
						continue;
					}
					Vector3? vector = item.Location?.Position;
					if (vector.HasValue)
					{
						double num3 = Distance(value, vector.Value);
						if ((!(num > 0.0) || !(num3 > num)) && num3 < num2)
						{
							num2 = num3;
							entity2 = item;
						}
					}
				}
			}
			if (entity2 == null)
			{
				IEntity entity3 = _gameDataProvider?.GetSelectedObject();
				if (entity3 != null && entity3.InstanceId != entity.InstanceId && entity3.InstanceId != 0 && IsHostileEntity(entity3) && entity3.HitPoints_Current.GetValueOrDefault() > 0)
				{
					Vector3? vector2 = entity3.Location?.Position;
					if (vector2.HasValue)
					{
						double num4 = Distance(value, vector2.Value);
						if (num <= 0.0 || num4 <= num)
						{
							entity2 = entity3;
						}
					}
				}
			}
			if (entity2 != null)
			{
				ulong? curSelId = _gameDataProvider?.GetSelectedObjectId();
				if (curSelId != entity2.InstanceId)
					_gameDataProvider?.SelectObject(entity2.InstanceId);
			}
		}
		catch
		{
		}
	}

	private bool IsHostileEntity(IEntity entity)
	{
		try
		{
			if (entity.InstanceId == _gameDataProvider?.GetCurrentCharacterId().GetValueOrDefault())
			{
				return false;
			}
			if (!string.IsNullOrEmpty(entity.Name))
			{
				var party = _gameDataProvider?.GetPartyMembers();
				if (party != null && party.Any(p => string.Equals(p.Name, entity.Name, StringComparison.OrdinalIgnoreCase)))
				{
					return false;
				}
			}
			IProperty property = entity.GetProperty(268437503u);
			if (property != null && property.Value != null && Convert.ToBoolean(property.Value))
			{
				return true;
			}
			IProperty property2 = entity.GetProperty(268435581u);
			if (property2 != null && property2.Value != null && Convert.ToInt32(property2.Value) > 0)
			{
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private double Distance(Vector3 a, Vector3 b)
	{
		double num = a.X - b.X;
		double num2 = a.Y - b.Y;
		return Math.Sqrt(num * num + num2 * num2);
	}

	internal double? GetTargetDistance()
	{
		try
		{
			IEntity entity = _gameDataProvider?.GetCurrentCharacter();
			if (entity == null || !(entity.Location?.Position).HasValue)
			{
				return null;
			}
			Vector3 value = entity.Location.Position.Value;
			IEntity entity2 = _gameDataProvider?.GetSelectedObject();
			if (entity2 == null || !(entity2.Location?.Position).HasValue)
			{
				return null;
			}
			Vector3 value2 = entity2.Location.Position.Value;
			return Distance(value, value2);
		}
		catch
		{
			return null;
		}
	}

	internal int CountNearbyEnemies()
	{
		try
		{
			IEntity entity = _gameDataProvider?.GetCurrentCharacter();
			if (entity == null || !(entity.Location?.Position).HasValue)
			{
				return 0;
			}
			Vector3 value = entity.Location.Position.Value;
			double num = (((double)_config.AutoTargetMaxRange > 0.0) ? ((double)_config.AutoTargetMaxRange) : 40.0);
			IReadOnlyCollection<IEntity> readOnlyCollection = _gameDataProvider?.GetEntities();
			if (readOnlyCollection == null || readOnlyCollection.Count == 0)
			{
				return 0;
			}
			int num2 = 0;
			foreach (IEntity item in readOnlyCollection)
			{
				if (item == null || item.InstanceId == 0L || item.InstanceId == entity.InstanceId || !IsHostileEntity(item) || item.HitPoints_Current.GetValueOrDefault() <= 0)
				{
					continue;
				}
				Vector3? vector = item.Location?.Position;
				if (vector.HasValue)
				{
					double num3 = Distance(value, vector.Value);
					if (num3 <= num)
					{
						num2++;
					}
				}
			}
			return num2;
		}
		catch
		{
			return 0;
		}
	}

	internal bool IsToggleActive(uint referenceDid, string abilityName)
	{
		try
		{
			IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _gameDataProvider?.GetActiveEffects();
			if (readOnlyDictionary == null || readOnlyDictionary.Count == 0)
			{
				return false;
			}
			foreach (KeyValuePair<int, IActiveEffect> item in readOnlyDictionary)
			{
				if (item.Value.EffectDid == (int)referenceDid)
				{
					return true;
				}
			}
			uint cachedEffectDid = GameCache.GetCachedEffectDid(referenceDid);
			if (cachedEffectDid != 0)
			{
				foreach (KeyValuePair<int, IActiveEffect> item2 in readOnlyDictionary)
				{
					if (item2.Value.EffectDid == (int)cachedEffectDid)
					{
						return true;
					}
				}
			}
			if (!string.IsNullOrEmpty(abilityName))
			{
				IPropertyMaster propertyMaster = _gameDataProvider?.PropertyMaster;
				if (propertyMaster != null)
				{
					foreach (KeyValuePair<int, IActiveEffect> item3 in readOnlyDictionary)
					{
						IPropertyCollection propertyCollection = propertyMaster.GetPropertyCollection((uint)item3.Value.EffectDid);
						if (propertyCollection == null)
						{
							continue;
						}
						string text = ExtractTextFromProps(propertyCollection, (uint)item3.Value.EffectDid, propertyMaster);
						if (!string.IsNullOrEmpty(text))
						{
							if (text.StartsWith("Spell: ", StringComparison.OrdinalIgnoreCase))
							{
								text = text.Substring(7);
							}
							if (string.Equals(text.Trim(), abilityName.Trim(), StringComparison.OrdinalIgnoreCase))
							{
								return true;
							}
						}
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	internal void TryLearnBuffDuration(RotationAbility ability)
	{
		if (ability.DurationMinutes > 0 || ability.DurationSeconds > 0)
		{
			return;
		}
		try
		{
			if (!ability.ReferenceDid.HasValue || ability.ReferenceDid.Value == 0)
			{
				return;
			}
			uint value = ability.ReferenceDid.Value;
			IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _gameDataProvider?.GetActiveEffects();
			if (readOnlyDictionary == null || readOnlyDictionary.Count == 0)
			{
				return;
			}
			DateTime utcNow = DateTime.UtcNow;
			foreach (KeyValuePair<int, IActiveEffect> item in readOnlyDictionary)
			{
				IActiveEffect value2 = item.Value;
				if (!value2.Duration.HasValue || value2.Duration <= 0f || value2.Duration.GetValueOrDefault() == -1f)
				{
					continue;
				}
				float value3 = value2.Duration.Value;
				if (value3 < 10f)
				{
					continue;
				}
				DateTime? dateTime = value2.CalculateExpiration();
				if (!dateTime.HasValue)
				{
					continue;
				}
				DateTime dateTime2 = dateTime.Value.AddSeconds(0f - value3);
				double totalSeconds = (utcNow - dateTime2).TotalSeconds;
				if (!(totalSeconds >= 0.0) || !(totalSeconds <= 3.0))
				{
					continue;
				}
				SetBuffDuration(ability, value3);
				if (ability.LastFired == DateTime.MinValue)
				{
					ability.LastFired = dateTime2;
				}
				return;
			}
			foreach (KeyValuePair<int, IActiveEffect> item2 in readOnlyDictionary)
			{
				IActiveEffect value4 = item2.Value;
				if (value4.EffectDid != (int)value || !value4.Duration.HasValue || value4.Duration <= 0f || value4.Duration.GetValueOrDefault() == -1f)
				{
					continue;
				}
				float value5 = value4.Duration.Value;
				if (value5 < 10f)
				{
					continue;
				}
				SetBuffDuration(ability, value5);
				if (ability.LastFired == DateTime.MinValue)
				{
					DateTime? dateTime3 = value4.CalculateExpiration();
					if (dateTime3.HasValue)
					{
						ability.LastFired = dateTime3.Value.AddSeconds(0f - value5);
					}
				}
				break;
			}
		}
		catch
		{
		}
	}

	private void SetBuffDuration(RotationAbility ability, float durSec)
	{
		int num = (int)durSec;
		int num2 = num / 60;
		int num3 = num % 60;
		if (ability.DurationMinutes != num2 || ability.DurationSeconds != num3)
		{
			ability.DurationMinutes = num2;
			ability.DurationSeconds = num3;
			SaveBuffOrder();
			Log($"LEARNED buff duration for {ability.Name}: {num2}m {num3}s ({durSec:F1}s)");
		}
	}

	internal static string SimplifyName(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return name;
		}
		int num = name.IndexOf(" (");
		if (num > 0)
		{
			return name.Substring(0, num);
		}
		return name;
	}

	internal long LookupBuffDurationFromTable(string abilityName)
	{
		if (string.IsNullOrEmpty(abilityName))
		{
			return 0L;
		}
		if (BuffDurationTable.TryGetValue(abilityName, out var value))
		{
			Log($"TABLE buff duration {(double)value / 1000.0:F0}s for [{abilityName}]");
			return value;
		}
		string text = SimplifyName(abilityName);
		if (text != abilityName && BuffDurationTable.TryGetValue(text, out value))
		{
			Log($"TABLE buff duration {(double)value / 1000.0:F0}s for [{abilityName}] (via simplified name)");
			return value;
		}
		return 0L;
	}

	internal long DetectCooldownFromDescription(uint did)
	{
		try
		{
			IGameDataProvider gameDataProvider = _gameDataProvider;
			IPropertyMaster propertyMaster = gameDataProvider?.PropertyMaster;
			if (propertyMaster == null)
			{
				return 0L;
			}
			HashSet<uint> visited = new HashSet<uint>();
			long num = ScanTextForDuration(did, propertyMaster, gameDataProvider, visited);
			if (num > 0)
			{
				return num;
			}
			num = ChaseDurationFromDid(did, propertyMaster, gameDataProvider, visited);
			if (num > 0)
			{
				return num;
			}
		}
		catch
		{
		}
		return 0L;
	}

	private long ScanTextForDuration(uint did, IPropertyMaster propMaster, IGameDataProvider gameData, HashSet<uint> visited)
	{
		if (!visited.Add(did))
		{
			return 0L;
		}
		IPropertyCollection propertyCollection = propMaster?.GetPropertyCollection(did);
		if (propertyCollection == null)
		{
			IPropertyCollection propertyCollection2 = gameData?.GetWeenieProperties(did);
			if (propertyCollection2 != null)
			{
				propertyCollection = propertyCollection2;
			}
		}
		if (propertyCollection == null)
		{
			return 0L;
		}
		foreach (KeyValuePair<uint, IProperty> property4 in propertyCollection.Properties)
		{
			string text = null;
			if (property4.Value is IStringInfoProperty stringInfoProperty)
			{
				text = stringInfoProperty.GetText(propMaster, did, propertyCollection);
			}
			else if (property4.Value is IStringProperty stringProperty)
			{
				text = stringProperty.StringValue;
			}
			if (!string.IsNullOrEmpty(text) && text.Length >= 20)
			{
				Match match = Regex.Match(text, "(?:Cooldown|Duration|Recast|Delay|Reuse)\\s*:?\\s*(\\d+(?:\\.\\d+)?)\\s*(?:seconds?|sec|s|minutes?|min|m)\\b", RegexOptions.IgnoreCase);
				if (match.Success && double.TryParse(match.Groups[1].Value, out var result) && result > 0.0 && result <= 3600.0)
				{
					long result2 = (long)(result * 1000.0);
					Log($"DETECTED duration {result}s for DID 0x{did:X8} from description (pattern 1)");
					return result2;
				}
				match = Regex.Match(text, "(?:Cooldown|Duration|Recast|Delay|Reuse)\\s*:?\\s*(\\d+(?:\\.\\d+)?)\\s*(?:minutes?|min|m)\\b", RegexOptions.IgnoreCase);
				if (match.Success && double.TryParse(match.Groups[1].Value, out var result3) && result3 > 0.0 && result3 <= 60.0)
				{
					long num = (long)(result3 * 60000.0);
					Log($"DETECTED duration {result3}min ({num / 1000}s) for DID 0x{did:X8} from description");
					return num;
				}
				match = Regex.Match(text, "(\\d+(?:\\.\\d+)?)\\s*(?:seconds?|sec|s)\\s+(?:cooldown|reuse|recast|duration|timer)", RegexOptions.IgnoreCase);
				if (match.Success && double.TryParse(match.Groups[1].Value, out result) && result > 0.0 && result <= 3600.0)
				{
					long result4 = (long)(result * 1000.0);
					Log($"DETECTED duration {result}s for DID 0x{did:X8} from description (pattern 2)");
					return result4;
				}
				match = Regex.Match(text, "[^\\d](\\d+(?:\\.\\d+)?)\\s*(?:seconds?|sec|s)\\b", RegexOptions.IgnoreCase);
				if (match.Success && double.TryParse(match.Groups[1].Value, out result) && result >= 3.0 && result <= 3600.0)
				{
					long result5 = (long)(result * 1000.0);
					Log($"DETECTED duration {result}s for DID 0x{did:X8} from description (pattern 3)");
					return result5;
				}
			}
		}
		try
		{
			IProperty property = propertyCollection.GetProperty(268443083u);
			if (property is IFloatProperty { FloatValue: var floatValue } floatProperty && floatValue > 0f)
			{
				long result6 = (long)(floatProperty.FloatValue * 1000f).Value;
				Log($"  CooldownDuration from action: {floatProperty.FloatValue:F1}s for DID 0x{did:X8}");
				return result6;
			}
			IProperty property2 = propertyCollection.GetProperty(268443084u);
			if (property2 is IFloatProperty { FloatValue: var floatValue2 } floatProperty2 && floatValue2 > 0f)
			{
				long result7 = (long)(floatProperty2.FloatValue * 1000f).Value;
				Log($"  CooldownDuration from action (timer2): {floatProperty2.FloatValue:F1}s for DID 0x{did:X8}");
				return result7;
			}
			IProperty property3 = propertyCollection.GetProperty(268443085u);
			if (property3 is IFloatProperty { FloatValue: var floatValue3 } floatProperty3 && floatValue3 > 0f)
			{
				long result8 = (long)(floatProperty3.FloatValue * 1000f).Value;
				Log($"  CooldownDuration from action (timer3): {floatProperty3.FloatValue:F1}s for DID 0x{did:X8}");
				return result8;
			}
		}
		catch
		{
		}
		return 0L;
	}

	private long ChaseDurationFromDid(uint did, IPropertyMaster propMaster, IGameDataProvider gameData, HashSet<uint> visited)
	{
		try
		{
			IPropertyCollection propertyCollection = propMaster?.GetPropertyCollection(did);
			if (propertyCollection == null)
			{
				return 0L;
			}
			foreach (KeyValuePair<uint, IProperty> property in propertyCollection.Properties)
			{
				uint? num = null;
				if (property.Value is IUInt32Property { UInt32Value: not null } iUInt32Property)
				{
					num = iUInt32Property.UInt32Value.Value;
				}
				else if (property.Value is IInt32Property { Int32Value: var int32Value } int32Property && int32Value.HasValue && int32Value.Value > 0)
				{
					num = (uint)int32Property.Int32Value.Value;
				}
				if (!num.HasValue)
				{
					continue;
				}
				uint value = num.Value;
				if (value > 16777216 && value <= int.MaxValue && !visited.Contains(value))
				{
					long num2 = ScanTextForDuration(value, propMaster, gameData, visited);
					if (num2 > 0)
					{
						return num2;
					}
					num2 = ChaseDurationFromDid(value, propMaster, gameData, visited);
					if (num2 > 0)
					{
						return num2;
					}
				}
			}
		}
		catch
		{
		}
		return 0L;
	}

	internal void OpenSetupSkills()
	{
		if (_setupForm != null && !_setupForm.IsDisposed)
		{
			_setupForm.BringToFront();
			_setupForm.Focus();
			return;
		}
		SwapToCharacterConfig();
		_setupForm = new SetupSkillsForm(this);
		_setupForm.FormClosed += delegate
		{
			_setupForm = null;
		};
		_setupForm.Show();
	}

	private void SwapToCharacterConfig()
	{
		try
		{
			string text = _gameDataProvider?.GetCurrentCharacterName();
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			if (_config.CharacterName != text)
			{
				if (!string.IsNullOrEmpty(_config.CharacterName))
				{
					SaveRotation();
					SaveBuffOrder();
					SaveConfig();
				}
				string characterName = _config.CharacterName;
				_config.CharacterName = text;
				LoadConfig();
				LoadRotation();
				LoadBuffOrder();
				LoadEmergency();
				LoadToggleBuffs();
				ClearToggleCache();
				BuffManager?.LoadProfile();
				BuffManager?.ResetZoneState();
				Log($"Switched to character '{text}' (was '{characterName ?? "none"}')");
			}
			else if (!string.IsNullOrEmpty(text) && string.IsNullOrEmpty(_config.CharacterName))
			{
				_config.CharacterName = text;
				SaveConfig();
				LoadConfig();
				MigrateFromLegacyIfNeeded();
				LoadRotation();
				LoadBuffOrder();
				LoadEmergency();
				LoadToggleBuffs();
				ClearToggleCache();
				BuffManager?.LoadProfile();
				BuffManager?.ResetZoneState();
			}
		}
		catch
		{
		}
	}

	internal string GetProfileDir()
	{
		string text = _config?.CharacterName;
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}
		string path = string.Join("_", text.Split(Path.GetInvalidFileNameChars()));
		return Path.Combine(_pluginFolder, "Character profiles", path);
	}

	private string GetConfigPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, "settings.json");
		}
		return Path.Combine(_pluginFolder, "RotationHelper.json");
	}

	internal void SaveConfig()
	{
		try
		{
			string configPath = GetConfigPath();
			string directoryName = Path.GetDirectoryName(configPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			_config.Save(configPath);
		}
		catch
		{
		}
	}

	internal void LoadConfig()
	{
		try
		{
			string configPath = GetConfigPath();
			if (File.Exists(configPath))
			{
				RotationConfig rotationConfig = RotationConfig.Load(configPath);
				if (rotationConfig != null)
				{
					rotationConfig.CharacterName = _config.CharacterName;
					_config = rotationConfig;
					Log("Loaded per-character config for '" + _config.CharacterName + "'");
				}
			}
			else
			{
				string charName = _config.CharacterName;
				_config = new RotationConfig();
				_config.CharacterName = charName;
				Log("No profile found for '" + (charName ?? "none") + "', using defaults");
			}
		}
		catch
		{
		}
	}

	internal string GetCharacterName()
	{
		return _gameDataProvider?.GetCurrentCharacterName() ?? "Unknown";
	}

	private string GetRotationPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, "rotation.json");
		}
		return Path.Combine(_pluginFolder, "rotation.json");
	}

	private string GetBuffPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, "bufforder.json");
		}
		return Path.Combine(_pluginFolder, "bufforder.json");
	}

	private string GetEmergencyPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, "emergency.json");
		}
		return Path.Combine(_pluginFolder, "emergency.json");
	}

	private void MigrateFromLegacyIfNeeded()
	{
		string text = _config?.CharacterName;
		if (!string.IsNullOrEmpty(text))
		{
			string text2 = string.Join("_", text.Split(Path.GetInvalidFileNameChars()));
			string text3 = Path.Combine(_pluginFolder, "Character profiles", text2);
			if (!Directory.Exists(text3))
			{
				Directory.CreateDirectory(text3);
			}
			string text4 = Path.Combine(_pluginFolder, "rotation_" + text2 + ".json");
			string text5 = Path.Combine(text3, "rotation.json");
			if (File.Exists(text4) && !File.Exists(text5))
			{
				File.Move(text4, text5);
			}
			string text6 = Path.Combine(_pluginFolder, "bufforder_" + text2 + ".json");
			string text7 = Path.Combine(text3, "bufforder.json");
			if (File.Exists(text6) && !File.Exists(text7))
			{
				File.Move(text6, text7);
			}
			string text8 = Path.Combine(_pluginFolder, "emergency_" + text2 + ".json");
			string text9 = Path.Combine(text3, "emergency.json");
			if (File.Exists(text8) && !File.Exists(text9))
			{
				File.Move(text8, text9);
			}
			string text10 = Path.Combine(_pluginFolder, "RotationHelper_" + text2 + ".json");
			string text11 = Path.Combine(text3, "settings.json");
			if (File.Exists(text10) && !File.Exists(text11))
			{
				File.Move(text10, text11);
			}
			LoadRotation();
			LoadBuffOrder();
			LoadEmergency();
			LoadConfig();
		}
	}

	internal void DumpCharacterProperties(string outputPath)
	{
		try
		{
			IEntity entity = _gameDataProvider?.GetCurrentCharacter();
			if (entity == null)
			{
				Log("Dump: No character loaded");
				return;
			}
			using StreamWriter streamWriter = new StreamWriter(outputPath, append: false);
			streamWriter.WriteLine($"# Character Property Dump - {entity.Name} - {DateTime.Now}");
			streamWriter.WriteLine($"# InstanceId: 0x{entity.InstanceId:X16}");
			streamWriter.WriteLine($"# WeenieId: 0x{entity.WeenieId.GetValueOrDefault():X8}");
			streamWriter.WriteLine("# Format: 0xPROPERTYID | Name | Value | Type");
			streamWriter.WriteLine();
			DumpPropertyCollection(streamWriter, "PropertyCollection (Instance)", entity.PropertyCollection);
			DumpPropertyCollection(streamWriter, "WeeniePropertyCollection (Base)", entity.WeeniePropertyCollection);
			DumpPropertyCollection(streamWriter, "InstanceProperties", entity.InstanceProperties);
			DumpPropertyCollection(streamWriter, "DetailedProperties", entity.DetailedProperties);
			Log("Dump: wrote " + outputPath);
		}
		catch (Exception ex)
		{
			Log("Dump error: " + ex.Message);
		}
	}

	private static void DumpPropertyCollection(StreamWriter sw, string label, IPropertyCollection collection)
	{
		sw.WriteLine("=== " + label + " ===");
		if (collection?.Properties == null || collection.Properties.Count == 0)
		{
			sw.WriteLine("(empty or null)");
			sw.WriteLine();
			return;
		}
		foreach (KeyValuePair<uint, IProperty> property in collection.Properties)
		{
			IProperty value = property.Value;
			if (value != null)
			{
				try
				{
					sw.WriteLine($"0x{value.PropertyId:X8} | {value.PropertyName ?? "?"} | {value.Value ?? "(null)"} | {value.Type ?? "?"}");
				}
				catch
				{
					sw.WriteLine($"0x{property.Key:X8} | ? | (error reading) | ?");
				}
			}
		}
		sw.WriteLine();
	}

	internal void Log(string msg)
	{
		lock (_logLock)
		{
			try
			{
				string text = Path.Combine(AppDataDir, "rotation.log");
				File.AppendAllText(text, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
				_logCallCount++;
				if (_logCallCount % 100 != 0)
				{
					return;
				}
				long length = new FileInfo(text).Length;
				if (length > 1048576)
				{
					string[] array = File.ReadAllLines(text);
					if (array.Length > 10000)
					{
						File.WriteAllLines(text, array.Skip(array.Length - 5000));
					}
				}
			}
			catch
			{
			}
		}
	}

	internal void LoadRotation()
	{
		try
		{
			string rotationPath = GetRotationPath();
			if (File.Exists(rotationPath))
			{
				string json = File.ReadAllText(rotationPath);
				Rotation = JsonSerializer.Deserialize<List<RotationAbility>>(json) ?? new List<RotationAbility>();
			}
			else
			{
				Rotation = new List<RotationAbility>();
			}
		}
		catch
		{
			if (Rotation == null)
			{
				Rotation = new List<RotationAbility>();
			}
		}
		if (Rotation == null)
		{
			Rotation = new List<RotationAbility>();
		}
	}

	internal void SaveRotation()
	{
		try
		{
			string rotationPath = GetRotationPath();
			string directoryName = Path.GetDirectoryName(rotationPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			string contents = JsonSerializer.Serialize(Rotation, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			File.WriteAllText(rotationPath, contents);
		}
		catch
		{
		}
	}

	internal void LoadBuffOrder()
	{
		try
		{
			string buffPath = GetBuffPath();
			if (File.Exists(buffPath))
			{
				string json = File.ReadAllText(buffPath);
				BuffOrder = JsonSerializer.Deserialize<List<RotationAbility>>(json) ?? new List<RotationAbility>();
			}
			else
			{
				BuffOrder = new List<RotationAbility>();
			}
		}
		catch
		{
			if (BuffOrder == null)
			{
				BuffOrder = new List<RotationAbility>();
			}
		}
		if (BuffOrder == null)
		{
			BuffOrder = new List<RotationAbility>();
		}
	}

	internal void SaveBuffOrder()
	{
		try
		{
			string buffPath = GetBuffPath();
			string directoryName = Path.GetDirectoryName(buffPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			string contents = JsonSerializer.Serialize(BuffOrder, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			File.WriteAllText(buffPath, contents);
		}
		catch
		{
		}
	}

	internal void LoadEmergency()
	{
		try
		{
			string emergencyPath = GetEmergencyPath();
			if (File.Exists(emergencyPath))
			{
				string json = File.ReadAllText(emergencyPath);
				Emergency = JsonSerializer.Deserialize<List<RotationAbility>>(json) ?? new List<RotationAbility>();
			}
			else
			{
				Emergency = new List<RotationAbility>();
			}
		}
		catch
		{
			if (Emergency == null)
			{
				Emergency = new List<RotationAbility>();
			}
		}
		if (Emergency == null)
		{
			Emergency = new List<RotationAbility>();
		}
		foreach (RotationAbility item in Emergency)
		{
			if (string.IsNullOrEmpty(item.HealScope))
			{
				item.HealScope = "Both";
			}
		}
	}

	internal void SaveEmergency()
	{
		try
		{
			string emergencyPath = GetEmergencyPath();
			string directoryName = Path.GetDirectoryName(emergencyPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			string contents = JsonSerializer.Serialize(Emergency, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			File.WriteAllText(emergencyPath, contents);
		}
		catch
		{
		}
	}

	private string GetToggleBuffsPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, "toggles.json");
		}
		return Path.Combine(_pluginFolder, "toggles.json");
	}

	internal void SaveToggleBuffs()
	{
		try
		{
			string contents = JsonSerializer.Serialize(ToggleBuffs, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			string toggleBuffsPath = GetToggleBuffsPath();
			string directoryName = Path.GetDirectoryName(toggleBuffsPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllText(toggleBuffsPath, contents);
		}
		catch
		{
		}
	}

	internal void LoadToggleBuffs()
	{
		try
		{
			string path = GetToggleBuffsPath();
			if (File.Exists(path))
			{
				string json = File.ReadAllText(path);
				ToggleBuffs = JsonSerializer.Deserialize<List<RotationAbility>>(json) ?? new List<RotationAbility>();
			}
			else
			{
				ToggleBuffs = new List<RotationAbility>();
			}
		}
		catch
		{
			if (ToggleBuffs == null)
			{
				ToggleBuffs = new List<RotationAbility>();
			}
		}
		if (ToggleBuffs == null)
		{
			ToggleBuffs = new List<RotationAbility>();
		}
		foreach (RotationAbility toggleBuff in ToggleBuffs)
		{
			DiscoverToggleProperty(toggleBuff);
		}
		PreWarmToggleCacheAsync();
	}

	private async Task PreWarmToggleCacheAsync()
	{
		try
		{
			if (!(await GameCache.CheckAliveAsync()))
			{
				return;
			}
			foreach (RotationAbility a in ToggleBuffs)
			{
				if (!a.ReferenceDid.HasValue || a.ReferenceDid == 0)
				{
					continue;
				}
				uint did = a.ReferenceDid.Value;
				string name = await GameCache.FetchNameAsync(did);
				if (!string.IsNullOrEmpty(name))
				{
					if (name.StartsWith("Spell: ", StringComparison.OrdinalIgnoreCase))
					{
						name = name.Substring(7);
					}
					a.Name = name.Trim();
				}
				await ResolveToggleEffectDidAsync(did);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			Exception ex4 = ex3;
			Log("Toggle cache pre-warm error: " + ex4.Message);
		}
	}

	private async Task<uint> ResolveToggleEffectDidAsync(uint actionDid)
	{
		try
		{
			uint cached = GameCache.GetCachedEffectDid(actionDid);
			if (cached != 0)
			{
				return cached;
			}
			IPropertyMaster propMaster = _gameDataProvider?.PropertyMaster;
			if (propMaster == null)
			{
				return 0u;
			}
			IPropertyCollection props = propMaster.GetPropertyCollection(actionDid);
			if (props == null)
			{
				return 0u;
			}
			foreach (KeyValuePair<uint, IProperty> property5 in props.Properties)
			{
				IProperty value = property5.Value;
				if (!(value is IArrayProperty arr))
				{
					continue;
				}
				uint? spellDid = null;
				foreach (IProperty entry in arr.Properties)
				{
					if (!(entry is IPropertyCollection entryProps))
					{
						continue;
					}
					foreach (KeyValuePair<uint, IProperty> ep in entryProps.Properties)
					{
						value = ep.Value;
						IUInt32Property u32 = value as IUInt32Property;
						if (u32 != null && u32.UInt32Value > 1879048192 && u32.UInt32Value < 1895825407)
						{
							spellDid = u32.UInt32Value;
							break;
						}
						value = ep.Value;
						IInt32Property i32 = value as IInt32Property;
						if (i32 != null && i32.Int32Value > 1879048192 && i32.Int32Value < 1895825407)
						{
							spellDid = (uint)i32.Int32Value.Value;
							break;
						}
					}
					if (!spellDid.HasValue)
					{
						continue;
					}
					break;
				}
				if (!spellDid.HasValue)
				{
					continue;
				}
				IReadOnlyDictionary<int, IActiveEffect> effects = _gameDataProvider?.GetActiveEffects();
				if (effects == null)
				{
					continue;
				}
				foreach (KeyValuePair<int, IActiveEffect> item3 in effects)
				{
					uint effectDid = (uint)item3.Value.EffectDid;
					IPropertyCollection effectProps = propMaster.GetPropertyCollection(effectDid);
					if (effectProps == null)
					{
						continue;
					}
					foreach (KeyValuePair<uint, IProperty> property6 in effectProps.Properties)
					{
						value = property6.Value;
						if (!(value is IArrayProperty effectArr))
						{
							continue;
						}
						foreach (IProperty entry2 in effectArr.Properties)
						{
							if (!(entry2 is IPropertyCollection entryProps2))
							{
								continue;
							}
							foreach (KeyValuePair<uint, IProperty> eep in entryProps2.Properties)
							{
								uint? modSpellDid = null;
								value = eep.Value;
								IUInt32Property u33 = value as IUInt32Property;
								if (u33 != null && u33.UInt32Value == spellDid)
								{
									modSpellDid = u33.UInt32Value;
								}
								else
								{
									value = eep.Value;
									IInt32Property i33 = value as IInt32Property;
									if (i33 != null && i33.Int32Value == (int?)spellDid.Value)
									{
										modSpellDid = (uint)i33.Int32Value.Value;
									}
								}
								if (modSpellDid.HasValue)
								{
									GameCache.CacheEffectDid(actionDid, effectDid);
									Log($"Resolved effect DID: action 0x{actionDid:X8} → spell 0x{spellDid:X8} → effect 0x{effectDid:X8}");
									return effectDid;
								}
							}
						}
					}
				}
			}
		}
		catch
		{
		}
		return 0u;
	}
}
public class SetupSkillsForm : Form
{
	private readonly RotationHelperPlugin _plugin;

	private TextBox _searchBox;

	private ListBox _abilityList;

	private ComboBox _keyCombo;

	private Button _addSelectedBtn;

	private Button _scanHotbarBtn;

	private DataGridView _rotationList;

	private Button _removeBtn;

	private Button _upBtn;

	private Button _downBtn;

	private Label _triggerKeyLabel;

	private ComboBox _triggerKeyCombo;

	private ComboBox _triggerKey2Combo;

	private NumericUpDown _delayNumeric;

	private ComboBox _buffTriggerKeyCombo;

	private NumericUpDown _buffDelayNumeric;

	private ComboBox _pauseKeyCombo;

	private Panel _buffPanel;

	private Button _buffRemoveBtn;

	private Button _addBuffSelectedBtn;

	private Button _addEmergencyBtn;

	private Button _addToggleBtn;

	private Action _rebuildTogglePanel;

	private DataGridView _emergencyList;

	private Button _emergencyRemoveBtn;

	private Button _emergencyUpBtn;

	private Button _emergencyDownBtn;

	private CheckBox _autoOnTargetChk;

	private CheckBox _meleeChk;

	private CheckBox _autoTargetFurthestChk;

	private CheckBox _autoTargetClosestChk;

	private NumericUpDown _maxRangeNumeric;

	private Label _targetDistanceLbl;

	private System.Windows.Forms.Timer _distanceTimer;

	private TabControl _tabControl;

	private SplitContainer _splitter;

	private bool _manualCdMode;

	private int _buffSelectedIndex = -1;

	private List<HotbarAbility> _hotbarAbilities = new List<HotbarAbility>();

	private static readonly string[] KeyOptions = new string[56]
	{
		"1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
		"F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10",
		"F11", "F12", "-", "=", "Q", "E", "R", "T", "Y", "U",
		"I", "O", "P", "[", "]", "A", "S", "D", "F", "G",
		"H", "J", "K", "L", ";", "'", "Z", "X", "C", "V",
		"B", "N", "M", ",", ".", "/"
	};

	private static readonly Dictionary<string, int> KeyVkMap = new Dictionary<string, int>
	{
		{ "1", 49 },
		{ "2", 50 },
		{ "3", 51 },
		{ "4", 52 },
		{ "5", 53 },
		{ "6", 54 },
		{ "7", 55 },
		{ "8", 56 },
		{ "9", 57 },
		{ "0", 48 },
		{ "F1", 112 },
		{ "F2", 113 },
		{ "F3", 114 },
		{ "F4", 115 },
		{ "F5", 116 },
		{ "F6", 117 },
		{ "F7", 118 },
		{ "F8", 119 },
		{ "F9", 120 },
		{ "F10", 121 },
		{ "F11", 122 },
		{ "F12", 123 },
		{ "-", 189 },
		{ "=", 187 },
		{ "Q", 81 },
		{ "E", 69 },
		{ "R", 82 },
		{ "T", 84 },
		{ "Y", 89 },
		{ "U", 85 },
		{ "I", 73 },
		{ "O", 79 },
		{ "P", 80 },
		{ "A", 65 },
		{ "S", 83 },
		{ "D", 68 },
		{ "F", 70 },
		{ "G", 71 },
		{ "H", 72 },
		{ "J", 74 },
		{ "K", 75 },
		{ "L", 76 },
		{ "Z", 90 },
		{ "X", 88 },
		{ "C", 67 },
		{ "V", 86 },
		{ "B", 66 },
		{ "N", 78 },
		{ "M", 77 },
		{ ",", 188 },
		{ ".", 190 },
		{ "/", 191 },
		{ "[", 219 },
		{ "]", 221 },
		{ ";", 186 },
		{ "'", 222 }
	};

	private static readonly string[] TriggerKeyOptions = BuildTriggerKeys();

	private static readonly Dictionary<string, int> TriggerVkMap = BuildTriggerVkMap();

	private static readonly string[] PauseKeyOptions = BuildPauseKeys();

	private static string[] BuildTriggerKeys()
	{
		return new string[114]
		{
			"R.Mouse+L.Mouse", "L.Mouse", "R.Mouse", "M.Mouse", "Mouse4", "Mouse5", "F1", "F2", "F3", "F4",
			"F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "F13", "F14",
			"F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
			"`", "1", "2", "3", "4", "5", "6", "7", "8", "9",
			"0", "-", "=", "Q", "W", "E", "R", "T", "Y", "U",
			"I", "O", "P", "[", "]", "\\", "A", "S", "D", "F",
			"G", "H", "J", "K", "L", ";", "'", "Z", "X", "C",
			"V", "B", "N", "M", ",", ".", "/", "Tab", "CapsLock", "L.Shift",
			"R.Shift", "L.Ctrl", "R.Ctrl", "L.Alt", "R.Alt", "Space", "Enter", "Backspace", "Escape", "Up",
			"Down", "Left", "Right", "Insert", "Home", "PageUp", "Delete", "End", "PageDown", "Num0",
			"Num1", "Num2", "Num3", "Num4", "Num5", "Num6", "Num7", "Num8", "Num9", "Num.",
			"Num/", "Num*", "Num-", "Num+"
		};
	}

	private static Dictionary<string, int> BuildTriggerVkMap()
	{
		return new Dictionary<string, int>
		{
			{ "R.Mouse+L.Mouse", 1073741823 },
			{ "L.Mouse", 1 },
			{ "R.Mouse", 2 },
			{ "M.Mouse", 4 },
			{ "Mouse4", 5 },
			{ "Mouse5", 6 },
			{ "F1", 112 },
			{ "F2", 113 },
			{ "F3", 114 },
			{ "F4", 115 },
			{ "F5", 116 },
			{ "F6", 117 },
			{ "F7", 118 },
			{ "F8", 119 },
			{ "F9", 120 },
			{ "F10", 121 },
			{ "F11", 122 },
			{ "F12", 123 },
			{ "F13", 124 },
			{ "F14", 125 },
			{ "F15", 126 },
			{ "F16", 127 },
			{ "F17", 128 },
			{ "F18", 129 },
			{ "F19", 130 },
			{ "F20", 131 },
			{ "F21", 132 },
			{ "F22", 133 },
			{ "F23", 134 },
			{ "F24", 135 },
			{ "`", 192 },
			{ "1", 49 },
			{ "2", 50 },
			{ "3", 51 },
			{ "4", 52 },
			{ "5", 53 },
			{ "6", 54 },
			{ "7", 55 },
			{ "8", 56 },
			{ "9", 57 },
			{ "0", 48 },
			{ "-", 189 },
			{ "=", 187 },
			{ "Q", 81 },
			{ "W", 87 },
			{ "E", 69 },
			{ "R", 82 },
			{ "T", 84 },
			{ "Y", 89 },
			{ "U", 85 },
			{ "I", 73 },
			{ "O", 79 },
			{ "P", 80 },
			{ "[", 219 },
			{ "]", 221 },
			{ "\\", 220 },
			{ "A", 65 },
			{ "S", 83 },
			{ "D", 68 },
			{ "F", 70 },
			{ "G", 71 },
			{ "H", 72 },
			{ "J", 74 },
			{ "K", 75 },
			{ "L", 76 },
			{ ";", 186 },
			{ "'", 222 },
			{ "Z", 90 },
			{ "X", 88 },
			{ "C", 67 },
			{ "V", 86 },
			{ "B", 66 },
			{ "N", 78 },
			{ "M", 77 },
			{ ",", 188 },
			{ ".", 190 },
			{ "/", 191 },
			{ "Tab", 9 },
			{ "CapsLock", 20 },
			{ "L.Shift", 160 },
			{ "R.Shift", 161 },
			{ "L.Ctrl", 162 },
			{ "R.Ctrl", 163 },
			{ "L.Alt", 164 },
			{ "R.Alt", 165 },
			{ "Space", 32 },
			{ "Enter", 13 },
			{ "Backspace", 8 },
			{ "Escape", 27 },
			{ "Up", 38 },
			{ "Down", 40 },
			{ "Left", 37 },
			{ "Right", 39 },
			{ "Insert", 45 },
			{ "Home", 36 },
			{ "PageUp", 33 },
			{ "Delete", 46 },
			{ "End", 35 },
			{ "PageDown", 34 },
			{ "Num0", 96 },
			{ "Num1", 97 },
			{ "Num2", 98 },
			{ "Num3", 99 },
			{ "Num4", 100 },
			{ "Num5", 101 },
			{ "Num6", 102 },
			{ "Num7", 103 },
			{ "Num8", 104 },
			{ "Num9", 105 },
			{ "Num.", 110 },
			{ "Num/", 111 },
			{ "Num*", 106 },
			{ "Num-", 109 },
			{ "Num+", 107 }
		};
	}

	private static string[] BuildPauseKeys()
	{
		List<string> list = new List<string> { "None" };
		list.AddRange(BuildTriggerKeys());
		return list.ToArray();
	}

	public SetupSkillsForm(RotationHelperPlugin plugin)
	{
		_plugin = plugin;
		Text = $"Rotation Helper v{"4"} - Setup [{_plugin.GetCharacterName()}]";
		base.Size = new Size(1035, 820);
		MinimumSize = new Size(1035, 820);
		base.StartPosition = FormStartPosition.Manual;
		base.Location = new Point(plugin._config?.FormX ?? 330, plugin._config?.FormY ?? 130);
		BackColor = Color.FromArgb(30, 30, 35);
		ForeColor = Color.White;
		base.FormBorderStyle = FormBorderStyle.SizableToolWindow;
		base.MinimizeBox = true;
		base.MaximizeBox = false;
		BuildUI();
		base.Load += delegate
		{
			SetSplitterPosition();
			RefreshBuffList();
			_rebuildTogglePanel?.Invoke();
		};
		base.FormClosing += delegate
		{
			_plugin.SaveToggleBuffs();
		};
		RefreshRotationList();
		LoadSettings();
		ScanHotbarFromGame();
	}

	private void BuildUI()
	{
		_splitter = new SplitContainer
		{
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(30, 30, 35),
			BorderStyle = BorderStyle.FixedSingle
		};
		base.Controls.Add(_splitter);
		BuildLeftPanel(_splitter.Panel1);
		BuildRightPanel(_splitter.Panel2);
		BuildBottomBar(_splitter);
		UpdateAbilityList();
	}

	private void SetSplitterPosition()
	{
		if (_splitter != null)
		{
			_splitter.SplitterDistance = 450;
		}
	}

	private void BuildLeftPanel(Panel panel)
	{
		panel.BackColor = Color.FromArgb(30, 30, 35);
		int num = 10;
		int width = panel.Width - 20;
		_searchBox = new TextBox
		{
			Location = new Point(num, 10),
			Size = new Size(width, 22),
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.FromArgb(180, 180, 180),
			BorderStyle = BorderStyle.FixedSingle,
			Text = "Search abilities..."
		};
		_searchBox.GotFocus += delegate
		{
			if (_searchBox.Text == "Search abilities...")
			{
				_searchBox.Text = "";
				_searchBox.ForeColor = Color.White;
			}
		};
		_searchBox.LostFocus += delegate
		{
			if (_searchBox.Text.Trim() == "")
			{
				_searchBox.Text = "Search abilities...";
				_searchBox.ForeColor = Color.FromArgb(180, 180, 180);
			}
		};
		_searchBox.TextChanged += delegate
		{
			UpdateAbilityList();
		};
		panel.Controls.Add(_searchBox);
		Label value = new Label
		{
			Text = "Abilities (Ctrl+click multi):",
			Location = new Point(num, 38),
			Size = new Size(width, 18),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent
		};
		panel.Controls.Add(value);
		Panel panel2 = new Panel
		{
			Dock = DockStyle.Bottom,
			Height = 100,
			BackColor = Color.Transparent
		};
		_addSelectedBtn = new Button
		{
			Text = "Add → Rotation",
			Location = new Point(num, 4),
			Size = new Size(146, 28),
			BackColor = Color.FromArgb(60, 120, 60),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_addSelectedBtn.Click += delegate
		{
			AddSelectedAbilities();
		};
		panel2.Controls.Add(_addSelectedBtn);
		_addBuffSelectedBtn = new Button
		{
			Text = "Add → Buffs",
			Location = new Point(num, 36),
			Size = new Size(146, 28),
			BackColor = Color.FromArgb(50, 90, 130),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_addBuffSelectedBtn.Click += delegate
		{
			AddBuffSelection();
		};
		panel2.Controls.Add(_addBuffSelectedBtn);
		_addEmergencyBtn = new Button
		{
			Text = "Add → Emergency",
			Location = new Point(num + 150, 36),
			Size = new Size(146, 28),
			BackColor = Color.FromArgb(140, 50, 40),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_addEmergencyBtn.Click += delegate
		{
			AddEmergencySelection();
		};
		panel2.Controls.Add(_addEmergencyBtn);
		_addToggleBtn = new Button
		{
			Text = "Add → Toggle",
			Location = new Point(num, 68),
			Size = new Size(146, 28),
			BackColor = Color.FromArgb(100, 80, 130),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_addToggleBtn.Click += delegate
		{
			AddToggleSelection();
		};
		panel2.Controls.Add(_addToggleBtn);
		_scanHotbarBtn = new Button
		{
			Text = "Scan Hotbar",
			Location = new Point(num + 150, 68),
			Size = new Size(146, 28),
			BackColor = Color.FromArgb(100, 70, 50),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_scanHotbarBtn.Click += delegate
		{
			ScanHotbarFromGame();
		};
		panel2.Controls.Add(_scanHotbarBtn);
		panel.Controls.Add(panel2);
		_abilityList = new ListBox
		{
			Location = new Point(num, 58),
			Size = new Size(width, panel.Height - 58 - panel2.Height - 4),
			Anchor = (AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right),
			SelectionMode = SelectionMode.MultiExtended,
			BackColor = Color.FromArgb(40, 40, 45),
			ForeColor = Color.White,
			BorderStyle = BorderStyle.FixedSingle,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		contextMenuStrip.Items.Add("Add to Rotation", null, delegate
		{
			AddSelectedAbilities();
		});
		contextMenuStrip.Items.Add("Add to Buffs", null, delegate
		{
			AddBuffSelection();
		});
		contextMenuStrip.Items.Add("Add to Emergency", null, delegate
		{
			AddEmergencySelection();
		});
		contextMenuStrip.Items.Add("Add to Toggle", null, delegate
		{
			AddToggleSelection();
		});
		_abilityList.ContextMenuStrip = contextMenuStrip;
		_abilityList.DoubleClick += delegate
		{
			if (_abilityList.SelectedItems.Count != 0 && _tabControl?.SelectedTab != null)
			{
				if (_tabControl.SelectedTab.Text == "Rotation")
				{
					AddSelectedAbilities();
				}
				else if (_tabControl.SelectedTab.Text == "Buffs")
				{
					AddBuffSelection();
				}
				else if (_tabControl.SelectedTab.Text == "Emergency")
				{
					AddEmergencySelection();
				}
				else if (_tabControl.SelectedTab.Text == "Toggle Buffs")
				{
					AddToggleSelection();
				}
			}
		};
		panel.Controls.Add(_abilityList);
	}

	private void BuildRightPanel(Panel panel)
	{
		panel.BackColor = Color.FromArgb(30, 30, 35);
		_tabControl = new TabControl
		{
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(30, 30, 35),
			ForeColor = Color.White,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		BuildRotationTab(_tabControl);
		BuildToggleBuffsTab(_tabControl);
		BuildBuffsTab(_tabControl);
		BuildEmergencyTab(_tabControl);
		BuildHudTab(_tabControl);
		BuildSettingsTab(_tabControl);
		_tabControl.SelectedIndexChanged += delegate
		{
			if (_tabControl.SelectedTab?.Text == "Buffs")
			{
				RefreshBuffList();
			}
			else if (_tabControl.SelectedTab?.Text == "Toggle Buffs")
			{
				_rebuildTogglePanel?.Invoke();
			}
		};
		panel.Controls.Add(_tabControl);
	}

	private void BuildRotationTab(TabControl tabs)
	{
		TabPage tabPage = new TabPage("Rotation")
		{
			BackColor = Color.FromArgb(30, 30, 35),
			ForeColor = Color.White,
			UseVisualStyleBackColor = false
		};
		int x = 10;
		GroupBox groupBox = new GroupBox
		{
			Text = "Controls",
			Location = new Point(x, 4),
			Size = new Size(tabPage.ClientSize.Width - 20, 100),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Anchor = (AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right)
		};
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Location = new Point(6, 18),
			Size = new Size(200, 52),
			ColumnCount = 2,
			RowCount = 2,
			BackColor = Color.Transparent
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
		Button button = new Button
		{
			Text = "Auto All",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(50, 90, 130),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 1, 1)
		};
		button.Click += delegate
		{
			SetAllRotationAutocast(enabled: true);
		};
		tableLayoutPanel.Controls.Add(button, 0, 0);
		Button button2 = new Button
		{
			Text = "Disable All",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 0, 1)
		};
		button2.Click += delegate
		{
			SetAllRotationAutocast(enabled: false);
		};
		tableLayoutPanel.Controls.Add(button2, 1, 0);
		Button button3 = new Button
		{
			Text = "Auto CD",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(60, 100, 60),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 1, 0)
		};
		button3.Click += delegate
		{
			_manualCdMode = false;
			_rotationList.Columns["CD(s)"].DefaultCellStyle.BackColor = Color.FromArgb(40, 40, 45);
			_rotationList.Columns["CD(s)"].DefaultCellStyle.ForeColor = Color.White;
			_rotationList.CellBorderStyle = DataGridViewCellBorderStyle.None;
			SetAllRotationAutoCd();
		};
		tableLayoutPanel.Controls.Add(button3, 0, 1);
		Button button4 = new Button
		{
			Text = "Manual CD",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(100, 80, 50),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 0, 0)
		};
		button4.Click += delegate
		{
			_manualCdMode = true;
			_rotationList.Columns["CD(s)"].ReadOnly = false;
			_rotationList.Columns["CD(s)"].DefaultCellStyle.BackColor = Color.White;
			_rotationList.Columns["CD(s)"].DefaultCellStyle.ForeColor = Color.Black;
			_rotationList.CellBorderStyle = DataGridViewCellBorderStyle.Single;
			RefreshRotationList();
		};
		tableLayoutPanel.Controls.Add(button4, 1, 1);
		groupBox.Controls.Add(tableLayoutPanel);
		_autoOnTargetChk = new CheckBox
		{
			Text = "Enemy target only",
			Location = new Point(215, 22),
			Size = new Size(140, 20),
			ForeColor = Color.FromArgb(200, 200, 200),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f)
		};
		groupBox.Controls.Add(_autoOnTargetChk);
		_meleeChk = new CheckBox
		{
			Text = "Melee range check",
			Location = new Point(215, 48),
			Size = new Size(140, 20),
			ForeColor = Color.FromArgb(200, 200, 200),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f)
		};
		groupBox.Controls.Add(_meleeChk);
		Label value = new Label
		{
			Text = "Next ability key:",
			Location = new Point(360, 22),
			Size = new Size(95, 20),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			TextAlign = ContentAlignment.MiddleRight,
			Visible = false
		};
		groupBox.Controls.Add(value);
		_keyCombo = new ComboBox
		{
			Location = new Point(458, 20),
			Size = new Size(55, 24),
			DropDownStyle = ComboBoxStyle.DropDownList,
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.White,
			Visible = false
		};
		ComboBox.ObjectCollection items = _keyCombo.Items;
		object[] keyOptions = KeyOptions;
		object[] items2 = keyOptions;
		items.AddRange(items2);
		_keyCombo.SelectedIndex = 0;
		groupBox.Controls.Add(_keyCombo);
		Label value2 = new Label
		{
			Text = "(auto-increments when adding abilities)",
			Location = new Point(360, 46),
			Size = new Size(210, 18),
			ForeColor = Color.FromArgb(140, 140, 140),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 7f),
			Visible = false
		};
		groupBox.Controls.Add(value2);
		_autoTargetFurthestChk = new CheckBox
		{
			Text = "Auto target furthest",
			Location = new Point(360, 22),
			Size = new Size(140, 20),
			ForeColor = Color.FromArgb(200, 200, 200),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			Checked = _plugin._config.AutoTargetFurthestEnemy
		};
		_autoTargetFurthestChk.CheckedChanged += delegate
		{
			_plugin._config.AutoTargetFurthestEnemy = _autoTargetFurthestChk.Checked;
			_plugin.SaveConfig();
		};
		groupBox.Controls.Add(_autoTargetFurthestChk);
		_autoTargetClosestChk = new CheckBox
		{
			Text = "Auto target closest enemy",
			Location = new Point(360, 48),
			Size = new Size(160, 20),
			ForeColor = Color.FromArgb(200, 200, 200),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			Checked = _plugin._config.AutoTargetClosestEnemy
		};
		_autoTargetClosestChk.CheckedChanged += delegate
		{
			_plugin._config.AutoTargetClosestEnemy = _autoTargetClosestChk.Checked;
			_plugin.SaveConfig();
		};
		groupBox.Controls.Add(_autoTargetClosestChk);
		Label value3 = new Label
		{
			Text = "Range:",
			Location = new Point(505, 23),
			Size = new Size(42, 20),
			ForeColor = Color.FromArgb(200, 200, 200),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			TextAlign = ContentAlignment.MiddleRight
		};
		groupBox.Controls.Add(value3);
		_maxRangeNumeric = new NumericUpDown
		{
			Location = new Point(548, 22),
			Size = new Size(65, 22),
			Minimum = 0m,
			Maximum = 50000m,
			DecimalPlaces = 0,
			Value = (decimal)_plugin._config.AutoTargetMaxRange,
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.White
		};
		_maxRangeNumeric.ValueChanged += delegate
		{
			_plugin._config.AutoTargetMaxRange = (float)_maxRangeNumeric.Value;
			_plugin.SaveConfig();
		};
		groupBox.Controls.Add(_maxRangeNumeric);
		_targetDistanceLbl = new Label
		{
			Text = "Target dist: --",
			Location = new Point(505, 48),
			Size = new Size(120, 20),
			ForeColor = Color.FromArgb(180, 220, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			TextAlign = ContentAlignment.MiddleLeft
		};
		groupBox.Controls.Add(_targetDistanceLbl);
		CheckBox chkSmartOrder = new CheckBox
		{
			Text = "Smart Order",
			Location = new Point(360, 72),
			Size = new Size(120, 20),
			ForeColor = Color.FromArgb(200, 200, 200),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			Checked = _plugin._config.SmartOrder
		};
		chkSmartOrder.CheckedChanged += delegate
		{
			_plugin._config.SmartOrder = chkSmartOrder.Checked;
			_plugin.SaveConfig();
			if (_rotationList.Columns.Contains("Priority"))
			{
				_rotationList.Columns["Priority"].Visible = chkSmartOrder.Checked;
			}
		};
		groupBox.Controls.Add(chkSmartOrder);
		Label value4 = new Label
		{
			Text = "Picks best ready ability (by priority + time since last fire)",
			Location = new Point(360, 88),
			Size = new Size(320, 14),
			ForeColor = Color.FromArgb(140, 140, 140),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 7f)
		};
		groupBox.Controls.Add(value4);
		_distanceTimer = new System.Windows.Forms.Timer
		{
			Interval = 500
		};
		_distanceTimer.Tick += delegate
		{
			double? targetDistance = _plugin.GetTargetDistance();
			if (targetDistance.HasValue)
			{
				_targetDistanceLbl.Text = $"Target dist: {targetDistance.Value:F1}";
			}
			else
			{
				_targetDistanceLbl.Text = "Target dist: --";
			}
		};
		_distanceTimer.Start();
		tabPage.Controls.Add(groupBox);
		Label value5 = new Label
		{
			Text = "Rotation Abilities",
			Location = new Point(x, 88),
			Size = new Size(200, 20),
			ForeColor = Color.FromArgb(220, 220, 220),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
		};
		tabPage.Controls.Add(value5);
		Panel panel = new Panel
		{
			Dock = DockStyle.Bottom,
			Height = 28,
			BackColor = Color.Transparent
		};
		_removeBtn = new Button
		{
			Text = "Remove",
			Location = new Point(10, 1),
			Size = new Size(60, 24),
			BackColor = Color.FromArgb(120, 50, 50),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_removeBtn.Click += delegate
		{
			RemoveSelected();
		};
		panel.Controls.Add(_removeBtn);
		_upBtn = new Button
		{
			Text = "▲",
			Location = new Point(75, 1),
			Size = new Size(35, 24),
			BackColor = Color.FromArgb(60, 60, 65),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_upBtn.Click += delegate
		{
			MoveUp();
		};
		panel.Controls.Add(_upBtn);
		_downBtn = new Button
		{
			Text = "▼",
			Location = new Point(115, 1),
			Size = new Size(35, 24),
			BackColor = Color.FromArgb(60, 60, 65),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_downBtn.Click += delegate
		{
			MoveDown();
		};
		panel.Controls.Add(_downBtn);
		Button button5 = new Button
		{
			Text = "Remove All",
			Location = new Point(155, 1),
			Size = new Size(85, 24),
			BackColor = Color.FromArgb(120, 50, 50),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button5.Click += delegate
		{
			_plugin.Rotation.Clear();
			_plugin.SaveRotation();
			RefreshRotationList();
		};
		panel.Controls.Add(button5);
		tabPage.Controls.Add(panel);
		_rotationList = CreateRotationGrid();
		_rotationList.Columns["Priority"].Visible = _plugin._config.SmartOrder;
		_rotationList.Location = new Point(x, 110);
		_rotationList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		_rotationList.Width = tabPage.ClientSize.Width - 20;
		_rotationList.Height = Math.Max(80, tabPage.ClientSize.Height - 110 - panel.Height - 4);
		_rotationList.CellValueChanged += RotationGridCellValueChanged;
		_rotationList.CurrentCellDirtyStateChanged += delegate
		{
			if (_rotationList.CurrentCell is DataGridViewCheckBoxCell)
			{
				_rotationList.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		};
		_rotationList.CellFormatting += delegate(object? s, DataGridViewCellFormattingEventArgs e)
		{
			if (!(_rotationList.Columns[e.ColumnIndex].Name != "CD(s)"))
			{
				if (_manualCdMode)
				{
					e.CellStyle.BackColor = Color.White;
					e.CellStyle.ForeColor = Color.Black;
					if (e.Value is double num && num == 0.0)
					{
						e.Value = "";
					}
				}
				else if (e.Value is double num2 && num2 == 0.0)
				{
					e.Value = "Auto";
					e.CellStyle.ForeColor = Color.FromArgb(140, 140, 140);
					e.CellStyle.Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Italic);
				}
			}
		};
		tabPage.Controls.Add(_rotationList);
		tabs.TabPages.Add(tabPage);
	}

	private void BuildBuffsTab(TabControl tabs)
	{
		TabPage tabPage = new TabPage("Buffs")
		{
			BackColor = Color.FromArgb(30, 30, 35),
			ForeColor = Color.White,
			UseVisualStyleBackColor = false
		};
		int x = 10;
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Location = new Point(x, 2),
			Size = new Size(tabPage.ClientSize.Width - 20, 30),
			ColumnCount = 5,
			RowCount = 1,
			BackColor = Color.Transparent,
			Anchor = (AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right)
		};
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
		tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
		Button button = new Button
		{
			Text = "+ Add Buff",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(60, 120, 60),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 1, 0),
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
		};
		button.Click += delegate
		{
			_plugin.BuffOrder.Add(new RotationAbility
			{
				Name = "New Buff " + (_plugin.BuffOrder.Count + 1),
				BuffOnMissing = true,
				Autocast = true
			});
			_plugin.SaveBuffOrder();
			RefreshBuffList();
			if (_buffPanel.Controls.Count > 0)
			{
				_buffPanel.ScrollControlIntoView(_buffPanel.Controls[_buffPanel.Controls.Count - 1]);
			}
		};
		tableLayoutPanel.Controls.Add(button, 0, 0);
		Button button2 = new Button
		{
			Text = "Manual All",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(50, 90, 130),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 1, 0)
		};
		button2.Click += delegate
		{
			SetAllBuffAutocast(enabled: true);
		};
		tableLayoutPanel.Controls.Add(button2, 1, 0);
		Button button3 = new Button
		{
			Text = "Missing All",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 1, 0)
		};
		button3.Click += delegate
		{
			foreach (RotationAbility item in _plugin.BuffOrder)
			{
				item.BuffOnMissing = true;
				item.Autocast = false;
			}
			_plugin.SaveBuffOrder();
			RefreshBuffList();
		};
		tableLayoutPanel.Controls.Add(button3, 2, 0);
		Button button4 = new Button
		{
			Text = "Clear All",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(120, 50, 50),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 1, 0)
		};
		button4.Click += delegate
		{
			foreach (RotationAbility item2 in _plugin.BuffOrder)
			{
				item2.Autocast = false;
				item2.BuffOnMissing = false;
				item2.DurationMinutes = 0;
				item2.DurationSeconds = 0;
			}
			_plugin.SaveBuffOrder();
			RefreshBuffList();
		};
		tableLayoutPanel.Controls.Add(button4, 3, 0);
		Button button5 = new Button
		{
			Text = "Remove All",
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(120, 30, 30),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Margin = new Padding(0, 0, 0, 0)
		};
		button5.Click += delegate
		{
			_plugin.BuffOrder.Clear();
			_plugin.SaveBuffOrder();
			RefreshBuffList();
		};
		tableLayoutPanel.Controls.Add(button5, 4, 0);
		tabPage.Controls.Add(tableLayoutPanel);
		CheckBox chkDisableWild = new CheckBox
		{
			Text = "Disable in wilderness",
			Location = new Point(x, 32),
			Size = new Size(160, 20),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.BuffDisableWilderness
		};
		chkDisableWild.CheckedChanged += delegate
		{
			_plugin._config.BuffDisableWilderness = chkDisableWild.Checked;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(chkDisableWild);
		_buffPanel = new Panel
		{
			Location = new Point(x, 56),
			Width = 600,
			Height = 400,
			Anchor = (AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right),
			BackColor = Color.FromArgb(30, 30, 35),
			AutoScroll = true,
			BorderStyle = BorderStyle.FixedSingle
		};
		int _lastBuffPanelW = 0;
		_buffPanel.Resize += delegate
		{
			if (Math.Abs(_buffPanel.ClientSize.Width - _lastBuffPanelW) > 20)
			{
				_lastBuffPanelW = _buffPanel.ClientSize.Width;
				RefreshBuffList();
			}
		};
		tabPage.Controls.Add(_buffPanel);
		Panel panel = new Panel
		{
			Dock = DockStyle.Bottom,
			Height = 32,
			BackColor = Color.Transparent
		};
		_buffRemoveBtn = new Button
		{
			Text = "Remove Selected",
			Location = new Point(10, 3),
			Size = new Size(110, 24),
			BackColor = Color.FromArgb(120, 50, 50),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		_buffRemoveBtn.Click += delegate
		{
			RemoveBuffSelected();
		};
		panel.Controls.Add(_buffRemoveBtn);
		CheckBox chkDisablePublic = new CheckBox
		{
			Text = "Disable in public areas",
			Location = new Point(130, 4),
			Size = new Size(160, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.BuffDisablePublic
		};
		chkDisablePublic.CheckedChanged += delegate
		{
			_plugin._config.BuffDisablePublic = chkDisablePublic.Checked;
			_plugin.SaveConfig();
		};
		panel.Controls.Add(chkDisablePublic);
		tabPage.Controls.Add(panel);
		tabs.TabPages.Add(tabPage);
	}

	private void BuildToggleBuffsTab(TabControl tabs)
	{
		TabPage tabPage = new TabPage("Toggle Buffs")
		{
			BackColor = Color.FromArgb(30, 30, 35),
			ForeColor = Color.White,
			UseVisualStyleBackColor = false
		};
		int x = 10;
		TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 3,
			ColumnCount = 1,
			BackColor = Color.Transparent
		};
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 70f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = Color.Transparent
		};
		Label value = new Label
		{
			Text = "Toggle Abilities",
			Location = new Point(x, 4),
			Size = new Size(300, 20),
			ForeColor = Color.FromArgb(220, 220, 220),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
		};
		panel.Controls.Add(value);
		Label value2 = new Label
		{
			Text = "Add toggles from the ability list via 'Add → Toggle' button on the left.",
			Location = new Point(x, 24),
			Size = new Size(450, 16),
			ForeColor = Color.FromArgb(160, 160, 160),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f)
		};
		panel.Controls.Add(value2);
		TextBox searchBox = new TextBox
		{
			Location = new Point(x, 44),
			Size = new Size(panel.Width - 20, 22),
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			BorderStyle = BorderStyle.FixedSingle
		};
		panel.Controls.Add(searchBox);
		tableLayoutPanel.Controls.Add(panel, 0, 0);
		Panel panel2 = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb(40, 40, 45),
			BorderStyle = BorderStyle.FixedSingle,
			AutoScroll = true
		};
		Panel togglePanel = new Panel
		{
			Location = new Point(0, 0),
			BackColor = Color.Transparent,
			AutoSize = true
		};
		panel2.Controls.Add(togglePanel);
		tableLayoutPanel.Controls.Add(panel2, 0, 1);
		_rebuildTogglePanel = delegate
		{
			togglePanel.SuspendLayout();
			togglePanel.Controls.Clear();
			string value3 = searchBox.Text.Trim();
			int num = 2;
			for (int i = 0; i < _plugin.ToggleBuffs.Count; i++)
			{
				RotationAbility rotationAbility = _plugin.ToggleBuffs[i];
				if (string.IsNullOrEmpty(value3) || rotationAbility.Name.IndexOf(value3, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					int num2 = togglePanel.ClientSize.Width - 20;
					if (num2 < 200)
					{
						num2 = 200;
					}
					Panel panel4 = new Panel
					{
						Location = new Point(4, num),
						Size = new Size(num2, 26),
						BackColor = ((i % 2 == 0) ? Color.FromArgb(50, 50, 55) : Color.FromArgb(45, 45, 50))
					};
					CheckBox chk = new CheckBox
					{
						Location = new Point(4, 3),
						Size = new Size(16, 20),
						BackColor = Color.Transparent,
						Checked = rotationAbility.Autocast
					};
					int capturedIdx = i;
					chk.CheckedChanged += delegate
					{
						if (capturedIdx < _plugin.ToggleBuffs.Count)
						{
							_plugin.ToggleBuffs[capturedIdx].Autocast = chk.Checked;
							_plugin.SaveToggleBuffs();
						}
					};
					panel4.Controls.Add(chk);
					Label value4 = new Label
					{
						Text = ((rotationAbility.ToggleStatePropertyDid != 0) ? $"{rotationAbility.Name} [0x{rotationAbility.ToggleStatePropertyDid:X8}]" : rotationAbility.Name),
						Location = new Point(24, 3),
						Size = new Size(num2 - 130, 20),
						ForeColor = Color.White,
						BackColor = Color.Transparent,
						Font = new System.Drawing.Font("Segoe UI", 8f),
						TextAlign = ContentAlignment.MiddleLeft
					};
					panel4.Controls.Add(value4);
					Button button5 = new Button
					{
						Text = "✕",
						Location = new Point(num2 - 24, 2),
						Size = new Size(22, 22),
						BackColor = Color.FromArgb(180, 40, 40),
						ForeColor = Color.White,
						FlatStyle = FlatStyle.Flat
					};
					int capturedIdxRemove = i;
					button5.Click += delegate
					{
						RemoveToggleAt(capturedIdxRemove);
					};
					panel4.Controls.Add(button5);
					Button button6 = new Button
					{
						Text = "▲",
						Location = new Point(num2 - 72, 2),
						Size = new Size(22, 22),
						BackColor = Color.FromArgb(60, 60, 65),
						ForeColor = Color.White,
						FlatStyle = FlatStyle.Flat
					};
					int capturedIdxUp = i;
					button6.Click += delegate
					{
						MoveToggleAt(capturedIdxUp, -1);
					};
					panel4.Controls.Add(button6);
					Button button7 = new Button
					{
						Text = "▼",
						Location = new Point(num2 - 48, 2),
						Size = new Size(22, 22),
						BackColor = Color.FromArgb(60, 60, 65),
						ForeColor = Color.White,
						FlatStyle = FlatStyle.Flat
					};
					int capturedIdxDown = i;
					button7.Click += delegate
					{
						MoveToggleAt(capturedIdxDown, 1);
					};
					panel4.Controls.Add(button7);
					Button button8 = new Button
					{
						Text = "⚙",
						Location = new Point(num2 - 96, 2),
						Size = new Size(22, 22),
						BackColor = Color.FromArgb(60, 60, 65),
						ForeColor = Color.White,
						FlatStyle = FlatStyle.Flat
					};
					int capturedIdxEdit = i;
					button8.Click += delegate
					{
						EditToggleStatePropertyAt(capturedIdxEdit);
					};
					panel4.Controls.Add(button8);
					togglePanel.Controls.Add(panel4);
					num += 28;
				}
			}
			togglePanel.ResumeLayout();
		};
		searchBox.TextChanged += delegate
		{
			_rebuildTogglePanel();
		};
		Panel panel3 = new Panel
		{
			Dock = DockStyle.Bottom,
			Height = 64,
			BackColor = Color.Transparent
		};
		Button button = new Button
		{
			Text = "Enable All",
			Location = new Point(10, 2),
			Size = new Size(80, 24),
			BackColor = Color.FromArgb(50, 90, 130),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button.Click += delegate
		{
			foreach (RotationAbility toggleBuff in _plugin.ToggleBuffs)
			{
				toggleBuff.Autocast = true;
			}
			_plugin.SaveToggleBuffs();
			_rebuildTogglePanel();
		};
		panel3.Controls.Add(button);
		Button button2 = new Button
		{
			Text = "Disable All",
			Location = new Point(95, 2),
			Size = new Size(85, 24),
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button2.Click += delegate
		{
			foreach (RotationAbility toggleBuff2 in _plugin.ToggleBuffs)
			{
				toggleBuff2.Autocast = false;
			}
			_plugin.SaveToggleBuffs();
			_rebuildTogglePanel();
		};
		panel3.Controls.Add(button2);
		Button button3 = new Button
		{
			Text = "Detect All RefDids",
			Location = new Point(195, 2),
			Size = new Size(105, 24),
			BackColor = Color.FromArgb(40, 90, 50),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button3.Click += delegate
		{
			_plugin.DetectAllToggleReferenceDids();
			_plugin.SaveToggleBuffs();
			_rebuildTogglePanel();
		};
		panel3.Controls.Add(button3);
		Button button4 = new Button
		{
			Text = "Discover All DIDs",
			Location = new Point(305, 2),
			Size = new Size(105, 24),
			BackColor = Color.FromArgb(80, 60, 30),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button4.Click += delegate
		{
			_plugin.DiscoverTogglePropertyForAll();
			_plugin.SaveToggleBuffs();
			_rebuildTogglePanel();
		};
		panel3.Controls.Add(button4);
		tableLayoutPanel.Controls.Add(panel3, 0, 2);
		tabPage.Controls.Add(tableLayoutPanel);
		tabs.TabPages.Add(tabPage);
		_rebuildTogglePanel();
	}

	private void BuildEmergencyTab(TabControl tabs)
	{
		TabPage tabPage = new TabPage("Emergency")
		{
			BackColor = Color.FromArgb(30, 30, 35),
			ForeColor = Color.White,
			UseVisualStyleBackColor = false
		};
		int num = 10;
		Label value = new Label
		{
			Text = "Emergency Abilities",
			Location = new Point(num, 4),
			Size = new Size(300, 20),
			ForeColor = Color.FromArgb(220, 220, 220),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
		};
		tabPage.Controls.Add(value);
		CheckBox chkPartyHeal = new CheckBox
		{
			Text = "Heal party members",
			Location = new Point(num + 15, 26),
			Size = new Size(200, 20),
			ForeColor = Color.White,
			BackColor = Color.Transparent
		};
		chkPartyHeal.CheckedChanged += delegate
		{
			_plugin._config.EmergencyPartyHeal = chkPartyHeal.Checked;
			_plugin.SaveConfig();
			if (_emergencyList != null && _emergencyList.Columns.Contains("PT HP"))
			{
				_emergencyList.Columns["PT HP"].ReadOnly = !chkPartyHeal.Checked;
			}
		};
		chkPartyHeal.Checked = _plugin._config.EmergencyPartyHeal;
		tabPage.Controls.Add(chkPartyHeal);
		Label rangeLbl = new Label
		{
			Text = "Range:",
			Location = new Point(num + 15 + 205, 28),
			Size = new Size(45, 20),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			TextAlign = ContentAlignment.MiddleRight
		};
		tabPage.Controls.Add(rangeLbl);
		NumericUpDown rangeNud = new NumericUpDown
		{
			Location = new Point(num + 15 + 250, 27),
			Size = new Size(65, 20),
			Minimum = 0,
			Maximum = 200,
			DecimalPlaces = 1,
			Increment = 5,
			Value = (decimal)_plugin._config.HealRange,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White
		};
		rangeNud.ValueChanged += delegate
		{
			_plugin._config.HealRange = (double)rangeNud.Value;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(rangeNud);
		Label rangeUnit = new Label
		{
			Text = "(0=off)",
			Location = new Point(num + 15 + 318, 28),
			Size = Size = new Size(60, 20),
			ForeColor = Color.Gray,
			BackColor = Color.Transparent
		};
		tabPage.Controls.Add(rangeUnit);
		Panel panel = new Panel
		{
			Dock = DockStyle.Bottom,
			Height = 36,
			BackColor = Color.Transparent
		};
		_emergencyRemoveBtn = new Button
		{
			Text = "Remove",
			Location = new Point(num, 4),
			Size = new Size(80, 28),
			BackColor = Color.FromArgb(140, 50, 40),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			UseVisualStyleBackColor = false
		};
		_emergencyRemoveBtn.Click += delegate
		{
			if (_emergencyList.SelectedRows.Count != 0)
			{
				int index = _emergencyList.SelectedRows[0].Index;
				if (index >= 0 && index < _plugin.Emergency.Count)
				{
					_plugin.Emergency.RemoveAt(index);
					_plugin.SaveEmergency();
					RefreshEmergencyList();
				}
			}
		};
		panel.Controls.Add(_emergencyRemoveBtn);
		_emergencyUpBtn = new Button
		{
			Text = "▲",
			Location = new Point(94, 4),
			Size = new Size(40, 28),
			BackColor = Color.FromArgb(60, 60, 70),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			UseVisualStyleBackColor = false
		};
		_emergencyUpBtn.Click += delegate
		{
			MoveEmergencyItem(-1);
		};
		panel.Controls.Add(_emergencyUpBtn);
		_emergencyDownBtn = new Button
		{
			Text = "▼",
			Location = new Point(138, 4),
			Size = new Size(40, 28),
			BackColor = Color.FromArgb(60, 60, 70),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			UseVisualStyleBackColor = false
		};
		_emergencyDownBtn.Click += delegate
		{
			MoveEmergencyItem(1);
		};
		panel.Controls.Add(_emergencyDownBtn);
		tabPage.Controls.Add(panel);
		_emergencyList = CreateGridBase();
		_emergencyList.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
		_emergencyList.ReadOnly = false;
		_emergencyList.BorderStyle = BorderStyle.FixedSingle;
		_emergencyList.Location = new Point(num, 50);
		_emergencyList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		_emergencyList.Width = tabPage.ClientSize.Width - 20;
		_emergencyList.Height = Math.Max(80, tabPage.ClientSize.Height - 50 - panel.Height - 4);
		_emergencyList.BackgroundColor = Color.FromArgb(30, 30, 35);
		_emergencyList.GridColor = Color.FromArgb(60, 60, 70);
		_emergencyList.CellPainting += delegate(object? sender, DataGridViewCellPaintingEventArgs e)
		{
			if (e.RowIndex >= 0)
			{
				e.CellStyle.BackColor = ((e.RowIndex % 2 == 0) ? Color.FromArgb(40, 40, 45) : Color.FromArgb(50, 50, 55));
			}
		};
		_emergencyList.CellClick += delegate(object? sender, DataGridViewCellEventArgs e)
		{
			if (e.RowIndex >= 0 && e.RowIndex < _plugin.Emergency.Count && _emergencyList.Columns[e.ColumnIndex]?.Name == "Scope")
			{
				RotationAbility rotationAbility = _plugin.Emergency[e.RowIndex];
				RotationAbility rotationAbility2 = rotationAbility;
				string healScope = rotationAbility.HealScope;
				bool flag = false;
				string healScope2 = ((healScope == "Self") ? "Party" : ((!(healScope == "Party")) ? "Self" : "Both"));
				bool flag2 = false;
				rotationAbility2.HealScope = healScope2;
				_emergencyList[e.ColumnIndex, e.RowIndex].Value = rotationAbility.HealScope;
				_plugin.SaveEmergency();
			}
		};
		_emergencyList.Columns.Add("HP", "Self HP %");
		_emergencyList.Columns.Add("PT HP", "Party HP %");
		_emergencyList.Columns.Add("Cnt", "Min #");
		_emergencyList.Columns.Add("Scope", "Scope");
		_emergencyList.Columns.Add("Ability", "Ability");
		DataGridViewCheckBoxColumn dataGridViewColumn = new DataGridViewCheckBoxColumn
		{
			Name = "Autocast",
			HeaderText = "Enabled",
			Width = 60
		};
		_emergencyList.Columns.Add(dataGridViewColumn);
		_emergencyList.Columns["HP"].FillWeight = 16f;
		_emergencyList.Columns["PT HP"].FillWeight = 16f;
		_emergencyList.Columns["Cnt"].FillWeight = 10f;
		_emergencyList.Columns["Scope"].FillWeight = 12f;
		_emergencyList.Columns["Ability"].FillWeight = 31f;
		_emergencyList.Columns["Autocast"].FillWeight = 15f;
		_emergencyList.Columns["Ability"].ReadOnly = true;
		_emergencyList.Columns["Scope"].ReadOnly = true;
		_emergencyList.Columns["PT HP"].ReadOnly = !_plugin._config.EmergencyPartyHeal;
		_emergencyList.EditingControlShowing += delegate(object? sender, DataGridViewEditingControlShowingEventArgs e)
		{
			Control control = e.Control;
			TextBox tb = control as TextBox;
			if (tb != null)
			{
				tb.BeginInvoke(delegate
				{
					tb.ForeColor = Color.Black;
				});
			}
		};
		_emergencyList.CurrentCellDirtyStateChanged += delegate
		{
			if (_emergencyList.CurrentCell is DataGridViewCheckBoxCell)
			{
				_emergencyList.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		};
		_emergencyList.CellValueChanged += delegate(object? sender, DataGridViewCellEventArgs e)
		{
			if (e.RowIndex >= 0 && e.RowIndex < _plugin.Emergency.Count)
			{
				DataGridViewColumn dataGridViewColumn2 = _emergencyList.Columns[e.ColumnIndex];
				RotationAbility rotationAbility = _plugin.Emergency[e.RowIndex];
				if (dataGridViewColumn2.Name == "HP")
				{
					string text = _emergencyList[e.ColumnIndex, e.RowIndex].Value?.ToString();
					int.TryParse(text ?? "50", out var result);
					rotationAbility.CooldownMs = Math.Clamp(result, 1, 100);
					_plugin.SaveEmergency();
				}
				else if (dataGridViewColumn2.Name == "PT HP")
				{
					if (_plugin._config.EmergencyPartyHeal)
					{
						string text2 = _emergencyList[e.ColumnIndex, e.RowIndex].Value?.ToString();
						int.TryParse(text2 ?? "0", out var result2);
						rotationAbility.PartyHpThreshold = Math.Clamp(result2, 1, 100);
						_plugin.SaveEmergency();
					}
				}
				else if (dataGridViewColumn2.Name == "Cnt")
				{
					string text3 = _emergencyList[e.ColumnIndex, e.RowIndex].Value?.ToString();
					int.TryParse(text3 ?? "0", out var result3);
					rotationAbility.PartyCountThreshold = Math.Max(0, result3);
					_plugin.SaveEmergency();
				}
				else if (dataGridViewColumn2.Name == "Autocast")
				{
					bool autocast = (bool)(_emergencyList[e.ColumnIndex, e.RowIndex].Value ?? ((object)false));
					rotationAbility.Autocast = autocast;
					_plugin.SaveEmergency();
				}
			}
		};
		tabPage.Controls.Add(_emergencyList);
		tabs.TabPages.Add(tabPage);
		RefreshEmergencyList();
	}

	private void RefreshEmergencyList()
	{
		_emergencyList.Rows.Clear();
		for (int i = 0; i < _plugin.Emergency.Count; i++)
		{
			RotationAbility rotationAbility = _plugin.Emergency[i];
			string text = ((rotationAbility.CooldownMs > 0) ? rotationAbility.CooldownMs.ToString() : "50");
			string text2 = ((rotationAbility.PartyHpThreshold > 0) ? rotationAbility.PartyHpThreshold.ToString() : "");
			string text3 = rotationAbility.PartyCountThreshold.ToString();
			string text4 = ((rotationAbility.HealScope == "Self" || rotationAbility.HealScope == "Party") ? rotationAbility.HealScope : "Both");
			_emergencyList.Rows.Add(text, text2, text3, text4, rotationAbility.Name ?? "?", rotationAbility.Autocast);
		}
	}

	private void MoveEmergencyItem(int direction)
	{
		if (_emergencyList.SelectedRows.Count == 0)
		{
			return;
		}
		int index = _emergencyList.SelectedRows[0].Index;
		int num = index + direction;
		if (num >= 0 && num < _plugin.Emergency.Count)
		{
			List<RotationAbility> emergency = _plugin.Emergency;
			RotationAbility item = emergency[index];
			emergency.RemoveAt(index);
			emergency.Insert(num, item);
			_plugin.SaveEmergency();
			RefreshEmergencyList();
			if (num >= 0 && num < _emergencyList.Rows.Count)
			{
				_emergencyList.Rows[num].Selected = true;
			}
		}
	}

	private void AddEmergencySelection()
	{
		if (_abilityList.SelectedItems.Count == 0)
		{
			return;
		}
		int num = 0;
		foreach (object selectedItem in _abilityList.SelectedItems)
		{
			string display = selectedItem.ToString();
			HotbarAbility hotbar = _hotbarAbilities.FirstOrDefault((HotbarAbility h) => h.Display == display);
			if (hotbar == null || _plugin.Emergency.Any((RotationAbility a) => a.ReferenceDid == hotbar.ReferenceDid && a.Name == hotbar.Name))
			{
				continue;
			}
			RotationAbility rotationAbility = BuildAbilityFromHotbar(hotbar);
			rotationAbility.CooldownMs = 50L;
			rotationAbility.Autocast = true;
			if (!string.IsNullOrEmpty(hotbar.ApiTargetType) || hotbar.ApiIsHarmful.HasValue)
			{
				if (hotbar.ApiIsHarmful == false)
				{
					rotationAbility.HealScope = "Both";
					_plugin.Log("API: set HealScope=Both for emergency [" + rotationAbility.Name + "] (non-harmful)");
				}
				else if (hotbar.ApiIsHarmful == true)
				{
					rotationAbility.HealScope = "Self";
					_plugin.Log("API: set HealScope=Self for emergency [" + rotationAbility.Name + "] (harmful)");
				}
			}
			_plugin.Emergency.Add(rotationAbility);
			num++;
		}
		if (num > 0)
		{
			_plugin.SaveEmergency();
			RefreshEmergencyList();
		}
	}

	private void BuildHudTab(TabControl tabs)
	{
		TabPage tabPage = new TabPage("HUD")
		{
			BackColor = Color.FromArgb(30, 30, 35),
			ForeColor = Color.White,
			UseVisualStyleBackColor = false
		};
		int num = 10;
		CheckBox chkEnabled = new CheckBox
		{
			Text = "Enable HUD Overlay",
			Location = new Point(num + 15, 10),
			Size = new Size(200, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.HudEnabled
		};
		chkEnabled.CheckedChanged += delegate
		{
			_plugin._config.HudEnabled = chkEnabled.Checked;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(chkEnabled);
		Label value = new Label
		{
			Text = "Sections",
			Location = new Point(num, 40),
			Size = new Size(200, 20),
			ForeColor = Color.FromArgb(220, 220, 220),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
		};
		tabPage.Controls.Add(value);
		int num2 = 64;
		CheckBox chkTarget = new CheckBox
		{
			Text = "Show Target",
			Location = new Point(num + 15, num2),
			Size = new Size(200, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.HudShowTarget
		};
		chkTarget.CheckedChanged += delegate
		{
			_plugin._config.HudShowTarget = chkTarget.Checked;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(chkTarget);
		num2 += 24;
		CheckBox chkCooldowns = new CheckBox
		{
			Text = "Show Cooldowns",
			Location = new Point(num + 15, num2),
			Size = new Size(200, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.HudShowCooldowns
		};
		chkCooldowns.CheckedChanged += delegate
		{
			_plugin._config.HudShowCooldowns = chkCooldowns.Checked;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(chkCooldowns);
		num2 += 24;
		CheckBox chkToggles = new CheckBox
		{
			Text = "Show Toggles",
			Location = new Point(num + 15, num2),
			Size = new Size(200, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.HudShowToggles
		};
		chkToggles.CheckedChanged += delegate
		{
			_plugin._config.HudShowToggles = chkToggles.Checked;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(chkToggles);
		num2 += 24;
		CheckBox chkBuffs = new CheckBox
		{
			Text = "Show Buffs",
			Location = new Point(num + 15, num2),
			Size = new Size(200, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.HudShowBuffs
		};
		chkBuffs.CheckedChanged += delegate
		{
			_plugin._config.HudShowBuffs = chkBuffs.Checked;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(chkBuffs);
		num2 += 24;
		CheckBox chkPaused = new CheckBox
		{
			Text = "Show Paused Indicator",
			Location = new Point(num + 15, num2),
			Size = new Size(200, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.HudShowPaused
		};
		chkPaused.CheckedChanged += delegate
		{
			_plugin._config.HudShowPaused = chkPaused.Checked;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(chkPaused);
		tabs.TabPages.Add(tabPage);
	}

	private void BuildSettingsTab(TabControl tabs)
	{
		TabPage tabPage = new TabPage("Settings")
		{
			BackColor = Color.FromArgb(30, 30, 35),
			ForeColor = Color.White,
			UseVisualStyleBackColor = false
		};
		int num = 10;
		Label value = new Label
		{
			Text = "Rotation Trigger",
			Location = new Point(num, 10),
			Size = new Size(300, 20),
			ForeColor = Color.FromArgb(220, 220, 220),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
		};
		tabPage.Controls.Add(value);
		_triggerKeyLabel = new Label
		{
			Text = "Trigger Key:",
			Location = new Point(num + 15, 34),
			Size = new Size(72, 22),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		tabPage.Controls.Add(_triggerKeyLabel);
		_triggerKeyCombo = new ComboBox
		{
			Location = new Point(num + 90, 32),
			Size = new Size(120, 24),
			DropDownStyle = ComboBoxStyle.DropDownList,
			DropDownWidth = 150,
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.White
		};
		ComboBox.ObjectCollection items = _triggerKeyCombo.Items;
		object[] triggerKeyOptions = TriggerKeyOptions;
		object[] items2 = triggerKeyOptions;
		items.AddRange(items2);
		_triggerKeyCombo.SelectedIndex = Array.IndexOf(TriggerKeyOptions, "R.Mouse");
		tabPage.Controls.Add(_triggerKeyCombo);
		Label value2 = new Label
		{
			Text = "Between keys (ms):",
			Location = new Point(num + 200, 34),
			Size = new Size(105, 22),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		tabPage.Controls.Add(value2);
		_delayNumeric = new NumericUpDown
		{
			Location = new Point(num + 310, 32),
			Size = new Size(70, 24),
			Minimum = 50m,
			Maximum = 5000m,
			Value = 200m,
			Increment = 50m,
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.White
		};
		tabPage.Controls.Add(_delayNumeric);
		Label value3 = new Label
		{
			Text = "Trigger Key 2:",
			Location = new Point(num + 15, 58),
			Size = new Size(78, 22),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		tabPage.Controls.Add(value3);
		_triggerKey2Combo = new ComboBox
		{
			Location = new Point(num + 95, 56),
			Size = new Size(120, 24),
			DropDownStyle = ComboBoxStyle.DropDownList,
			DropDownWidth = 150,
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.White
		};
		ComboBox.ObjectCollection items3 = _triggerKey2Combo.Items;
		triggerKeyOptions = PauseKeyOptions;
		items2 = triggerKeyOptions;
		items3.AddRange(items2);
		_triggerKey2Combo.SelectedIndex = 0;
		tabPage.Controls.Add(_triggerKey2Combo);
		Label value4 = new Label
		{
			Text = "Buff Trigger",
			Location = new Point(num, 98),
			Size = new Size(300, 20),
			ForeColor = Color.FromArgb(220, 220, 220),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
		};
		tabPage.Controls.Add(value4);
		Label value5 = new Label
		{
			Text = "Buff Trigger Key:",
			Location = new Point(num + 15, 122),
			Size = new Size(105, 22),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		tabPage.Controls.Add(value5);
		_buffTriggerKeyCombo = new ComboBox
		{
			Location = new Point(num + 120, 120),
			Size = new Size(120, 24),
			DropDownStyle = ComboBoxStyle.DropDownList,
			DropDownWidth = 150,
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.White
		};
		ComboBox.ObjectCollection items4 = _buffTriggerKeyCombo.Items;
		triggerKeyOptions = TriggerKeyOptions;
		items2 = triggerKeyOptions;
		items4.AddRange(items2);
		_buffTriggerKeyCombo.SelectedIndex = Array.IndexOf(TriggerKeyOptions, "R.Mouse");
		tabPage.Controls.Add(_buffTriggerKeyCombo);
		Label value6 = new Label
		{
			Text = "Delay(ms):",
			Location = new Point(num + 230, 122),
			Size = new Size(62, 22),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		tabPage.Controls.Add(value6);
		_buffDelayNumeric = new NumericUpDown
		{
			Location = new Point(num + 295, 120),
			Size = new Size(60, 24),
			Minimum = 50m,
			Maximum = 5000m,
			Value = 200m,
			Increment = 50m,
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.White
		};
		tabPage.Controls.Add(_buffDelayNumeric);
		Label value7 = new Label
		{
			Text = "Pause Toggle Key:",
			Location = new Point(num + 15, 152),
			Size = new Size(105, 22),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		tabPage.Controls.Add(value7);
		_pauseKeyCombo = new ComboBox
		{
			Location = new Point(num + 120, 150),
			Size = new Size(120, 24),
			DropDownStyle = ComboBoxStyle.DropDownList,
			DropDownWidth = 150,
			BackColor = Color.FromArgb(45, 45, 50),
			ForeColor = Color.White
		};
		ComboBox.ObjectCollection items5 = _pauseKeyCombo.Items;
		triggerKeyOptions = PauseKeyOptions;
		items2 = triggerKeyOptions;
		items5.AddRange(items2);
		_pauseKeyCombo.SelectedIndex = 0;
		tabPage.Controls.Add(_pauseKeyCombo);
		CheckBox chkAutoAttack = new CheckBox
		{
			Text = "Auto Attack (toggle on if not active)",
			Location = new Point(num + 15, 180),
			Size = new Size(280, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = _plugin._config.AutoAttackEnabled
		};
		chkAutoAttack.CheckedChanged += delegate
		{
			_plugin._config.AutoAttackEnabled = chkAutoAttack.Checked;
			_plugin.SaveConfig();
		};
		tabPage.Controls.Add(chkAutoAttack);
		Label value8 = new Label
		{
			Text = "Profile",
			Location = new Point(num, 232),
			Size = new Size(300, 20),
			ForeColor = Color.FromArgb(220, 220, 220),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f, FontStyle.Bold)
		};
		tabPage.Controls.Add(value8);
		Button button = new Button
		{
			Text = "Save Profile",
			Location = new Point(num + 15, 294),
			Size = new Size(120, 28),
			BackColor = Color.FromArgb(60, 120, 60),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button.Click += delegate
		{
			_plugin.SaveRotation();
			_plugin.SaveBuffOrder();
			_plugin.SaveEmergency();
			_plugin.SaveToggleBuffs();
			_plugin.SaveConfig();
			_plugin.Log("Profile saved for '" + _plugin.GetCharacterName() + "'");
		};
		tabPage.Controls.Add(button);
		Button button2 = new Button
		{
			Text = "Reload Profile",
			Location = new Point(num + 145, 294),
			Size = new Size(120, 28),
			BackColor = Color.FromArgb(60, 60, 65),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button2.Click += delegate
		{
			_plugin._config.CharacterName = _plugin.GetCharacterName();
			_plugin.LoadConfig();
			_plugin.LoadRotation();
			_plugin.LoadBuffOrder();
			_plugin.LoadEmergency();
			_plugin.LoadToggleBuffs();
			RefreshRotationList();
			RefreshBuffList();
			RefreshEmergencyList();
			_rebuildTogglePanel?.Invoke();
			_plugin.Log("Profile reloaded for '" + _plugin.GetCharacterName() + "'");
		};
		tabPage.Controls.Add(button2);
		Button button3 = new Button
		{
			Text = "Dump Properties",
			Location = new Point(num + 275, 294),
			Size = new Size(120, 28),
			BackColor = Color.FromArgb(80, 50, 50),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button3.Click += delegate
		{
			string text2 = Path.Combine(_plugin._pluginFolder, $"PropertyDump_{_plugin.GetCharacterName()}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
			_plugin.DumpCharacterProperties(text2);
			_plugin.Log("Property dump started: " + text2);
			MessageBox.Show("Dumping properties to:\n" + text2 + "\n\nRun with AutoAttack ON, then OFF, and compare the two files.", "Property Dump", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		};
		tabPage.Controls.Add(button3);
		Label value9 = new Label
		{
			Text = "Load Profile:",
			Location = new Point(num + 10, 326),
			Size = new Size(80, 22),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			TextAlign = ContentAlignment.MiddleLeft
		};
		tabPage.Controls.Add(value9);
		ComboBox profileCombo = new ComboBox
		{
			Location = new Point(num + 90, 326),
			Size = new Size(180, 24),
			DropDownStyle = ComboBoxStyle.DropDownList,
			BackColor = Color.FromArgb(40, 40, 45),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		string profilesDir = Path.Combine(_plugin._pluginFolder, "Character profiles");
		if (Directory.Exists(profilesDir))
		{
			string[] directories = Directory.GetDirectories(profilesDir);
			string[] array = directories;
			foreach (string text in array)
			{
				string fileName = Path.GetFileName(text);
				if (File.Exists(Path.Combine(text, "rotation.json")) || File.Exists(Path.Combine(text, "bufforder.json")) || File.Exists(Path.Combine(text, "config.json")))
				{
					profileCombo.Items.Add(fileName);
				}
			}
		}
		tabPage.Controls.Add(profileCombo);
		Button button4 = new Button
		{
			Text = "Load",
			Location = new Point(num + 275, 326),
			Size = new Size(60, 24),
			BackColor = Color.FromArgb(80, 80, 100),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		button4.Click += delegate
		{
			if (profileCombo.SelectedItem != null)
			{
				string text2 = profileCombo.SelectedItem.ToString();
				string path = Path.Combine(profilesDir, text2);
				string path2 = Path.Combine(path, "rotation.json");
				string path3 = Path.Combine(path, "bufforder.json");
				string path4 = Path.Combine(path, "emergency.json");
				string path5 = Path.Combine(path, "toggles.json");
				string path6 = Path.Combine(path, "config.json");
				if (File.Exists(path2))
				{
					string json = File.ReadAllText(path2);
					_plugin.Rotation = JsonSerializer.Deserialize<List<RotationAbility>>(json) ?? new List<RotationAbility>();
				}
				if (File.Exists(path3))
				{
					string json2 = File.ReadAllText(path3);
					_plugin.BuffOrder = JsonSerializer.Deserialize<List<RotationAbility>>(json2) ?? new List<RotationAbility>();
				}
				if (File.Exists(path4))
				{
					string json3 = File.ReadAllText(path4);
					_plugin.Emergency = JsonSerializer.Deserialize<List<RotationAbility>>(json3) ?? new List<RotationAbility>();
				}
				if (File.Exists(path5))
				{
					string json4 = File.ReadAllText(path5);
					_plugin.ToggleBuffs = JsonSerializer.Deserialize<List<RotationAbility>>(json4) ?? new List<RotationAbility>();
				}
				if (File.Exists(path6))
				{
					string json5 = File.ReadAllText(path6);
					RotationConfig rotationConfig = JsonSerializer.Deserialize<RotationConfig>(json5);
					if (rotationConfig != null)
					{
						_plugin._config = rotationConfig;
					}
				}
				_plugin.SaveRotation();
				_plugin.SaveBuffOrder();
				_plugin.SaveEmergency();
				_plugin.SaveToggleBuffs();
				_plugin.SaveConfig();
				RefreshRotationList();
				RefreshBuffList();
				RefreshEmergencyList();
				_rebuildTogglePanel?.Invoke();
				_plugin.Log("Profile loaded: '" + text2 + "'");
			}
		};
		tabPage.Controls.Add(button4);
		tabs.TabPages.Add(tabPage);
	}

	private void BuildBottomBar(SplitContainer splitter)
	{
		Panel panel = new Panel
		{
			Dock = DockStyle.Bottom,
			Height = 36,
			BackColor = Color.FromArgb(22, 22, 26)
		};
		Label statusLabel = new Label
		{
			Location = new Point(10, 8),
			Size = new Size(300, 22),
			ForeColor = Color.FromArgb(150, 255, 150),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 9f)
		};
		panel.Controls.Add(statusLabel);
		Button button = new Button
		{
			Text = "Save & Close",
			Location = new Point(panel.Width - 110, 4),
			Size = new Size(100, 28),
			BackColor = Color.FromArgb(60, 120, 60),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Anchor = (AnchorStyles.Top | AnchorStyles.Right)
		};
		button.Click += delegate
		{
			SaveAndClose();
		};
		panel.Controls.Add(button);
		System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer
		{
			Interval = 500
		};
		timer.Tick += delegate
		{
			bool flag = _plugin?.IsRunning ?? false;
			bool flag2 = _plugin?.BuffIsRunning ?? false;
			if (flag && flag2)
			{
				statusLabel.Text = "Status: ROTATION + BUFFS RUNNING";
			}
			else if (flag)
			{
				statusLabel.Text = "Status: ROTATION RUNNING";
			}
			else if (flag2)
			{
				statusLabel.Text = "Status: BUFFS RUNNING";
			}
			else
			{
				statusLabel.Text = "Status: Idle";
			}
			statusLabel.ForeColor = ((flag || flag2) ? Color.FromArgb(255, 200, 50) : Color.FromArgb(150, 255, 150));
		};
		timer.Start();
		base.Controls.Add(panel);
	}

	private void UpdateAbilityList()
	{
		_abilityList.Items.Clear();
		string searchText = _searchBox?.Text?.Trim() ?? "";
		if (searchText == "Search abilities...")
		{
			searchText = "";
		}
		IEnumerable<HotbarAbility> enumerable = _hotbarAbilities.AsEnumerable();
		if (searchText.Length > 0)
		{
			enumerable = enumerable.Where((HotbarAbility h) => h.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
		}
		foreach (HotbarAbility item in enumerable)
		{
			_abilityList.Items.Add(item.Display);
		}
	}

	private void ScanHotbarFromGame()
	{
		_scanHotbarBtn.Enabled = false;
		_scanHotbarBtn.Text = "Scanning...";
		List<HotbarAbility> list = (_hotbarAbilities = _plugin.ScanHotbar());
		foreach (RotationAbility item in _plugin.Rotation)
		{
			FillCooldownFromScan(item, list);
		}
		foreach (HotbarAbility item2 in list)
		{
			if (item2.ReferenceDid != 0)
			{
				_plugin.EnrichHotbarFromApi(item2);
			}
		}
		_plugin.SaveRotation();
		_plugin.SaveBuffOrder();
		if (list.Count == 0)
		{
			_scanHotbarBtn.Text = "No abilities found";
			Task.Delay(1500).ContinueWith(delegate
			{
				try
				{
					_scanHotbarBtn.Text = "Scan Hotbar (Game)";
					_scanHotbarBtn.Enabled = true;
				}
				catch
				{
				}
			});
		}
		else
		{
			_scanHotbarBtn.Text = $"Found {list.Count} abilities";
			Task.Delay(1500).ContinueWith(delegate
			{
				try
				{
					_scanHotbarBtn.Text = "Scan Hotbar (Game)";
					_scanHotbarBtn.Enabled = true;
				}
				catch
				{
				}
			});
		}
		UpdateAbilityList();
	}

	private static void FillCooldownFromScan(RotationAbility ability, List<HotbarAbility> results)
	{
		if (ability.CooldownMs <= 0 && ability.ReferenceDid.HasValue && ability.ReferenceDid.Value != 0)
		{
			HotbarAbility hotbarAbility = results.FirstOrDefault((HotbarAbility h) => h.ReferenceDid == ability.ReferenceDid.Value);
			if (hotbarAbility != null && hotbarAbility.CooldownMs > 0)
			{
				ability.CooldownMs = hotbarAbility.CooldownMs;
			}
		}
	}

	private RotationAbility BuildAbilityFromHotbar(HotbarAbility hotbar)
	{
		int num = hotbar.BarId * 10 + hotbar.SlotId;
		if (num >= KeyOptions.Length)
		{
			num %= KeyOptions.Length;
		}
		int virtualKey = KeyVkMap[KeyOptions[num]];
		string keyLabel = KeyOptions[num];
		return new RotationAbility
		{
			Name = hotbar.Name,
			Category = "Hotbar",
			VirtualKey = virtualKey,
			KeyLabel = keyLabel,
			CommandValue = ((hotbar.CommandValue != 0) ? new uint?(hotbar.CommandValue) : ((uint?)null)),
			ReferenceDid = ((hotbar.ReferenceDid != 0) ? new uint?(hotbar.ReferenceDid) : ((uint?)null)),
			CooldownMs = hotbar.CooldownMs,
			BuffDurationMs = hotbar.BuffDurationMs,
			CooldownTimerDid1 = hotbar.CooldownTimerDid1,
			CooldownTimerDid2 = hotbar.CooldownTimerDid2,
			CooldownTimerDid3 = hotbar.CooldownTimerDid3
		};
	}

	private void AddSelectedAbilities()
	{
		if (_abilityList.SelectedItems.Count == 0)
		{
			return;
		}
		foreach (object selectedItem in _abilityList.SelectedItems)
		{
			string display = selectedItem.ToString();
			HotbarAbility hotbarAbility = _hotbarAbilities.FirstOrDefault((HotbarAbility h) => h.Display == display);
			if (hotbarAbility != null)
			{
				RotationAbility rotationAbility = BuildAbilityFromHotbar(hotbarAbility);
				_plugin.EnrichFromApi(rotationAbility);
				if (!string.IsNullOrEmpty(hotbarAbility.ApiTargetType) && hotbarAbility.ApiTargetType == "Self" && !hotbarAbility.ApiTargetType.Contains("Foe"))
				{
					_plugin.Log("API: [" + rotationAbility.Name + "] targets Self only — consider adding to Buffs instead of Rotation");
				}
				_plugin.Rotation.Add(rotationAbility);
			}
		}
		_plugin.SaveRotation();
		RefreshRotationList();
	}

	private DataGridView CreateRotationGrid()
	{
		DataGridView dataGridView = CreateGridBase();
		dataGridView.AllowUserToResizeColumns = true;
		dataGridView.AllowUserToResizeRows = true;
		DataGridViewTextBoxColumn dataGridViewColumn = new DataGridViewTextBoxColumn
		{
			Name = "CD(s)",
			HeaderText = "Cooldown",
			Width = 70,
			DefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = Color.FromArgb(40, 40, 45),
				ForeColor = Color.White,
				SelectionBackColor = Color.FromArgb(60, 60, 70),
				SelectionForeColor = Color.White
			}
		};
		dataGridView.Columns.Add(dataGridViewColumn);
		AddGridColumns(dataGridView);
		return dataGridView;
	}

	private static DataGridView CreateGridBase()
	{
		DataGridView dataGridView = new DataGridView
		{
			AllowUserToAddRows = false,
			AllowUserToDeleteRows = false,
			AllowUserToOrderColumns = false,
			AllowUserToResizeColumns = false,
			AllowUserToResizeRows = false,
			RowHeadersVisible = false,
			SelectionMode = DataGridViewSelectionMode.FullRowSelect,
			MultiSelect = false,
			BorderStyle = BorderStyle.FixedSingle,
			BackgroundColor = Color.FromArgb(40, 40, 45),
			ForeColor = Color.White,
			GridColor = Color.FromArgb(60, 60, 65),
			CellBorderStyle = DataGridViewCellBorderStyle.None,
			ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = Color.FromArgb(50, 50, 55),
				ForeColor = Color.FromArgb(200, 200, 200),
				SelectionBackColor = Color.FromArgb(50, 50, 55),
				SelectionForeColor = Color.FromArgb(200, 200, 200)
			},
			DefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = Color.FromArgb(40, 40, 45),
				ForeColor = Color.White,
				SelectionBackColor = Color.FromArgb(60, 60, 70),
				SelectionForeColor = Color.White
			},
			AutoGenerateColumns = false,
			EditMode = DataGridViewEditMode.EditOnEnter,
			RowTemplate = 
			{
				Height = 20
			}
		};
		dataGridView.DataError += delegate(object? sender, DataGridViewDataErrorEventArgs args)
		{
			args.ThrowException = false;
		};
		return dataGridView;
	}

	private void AddGridColumns(DataGridView grid)
	{
		DataGridViewCheckBoxColumn dataGridViewColumn = new DataGridViewCheckBoxColumn
		{
			Name = "Auto",
			HeaderText = "Auto Cast",
			Width = 80
		};
		grid.Columns.Add(dataGridViewColumn);
		DataGridViewTextBoxColumn dataGridViewColumn2 = new DataGridViewTextBoxColumn
		{
			Name = "Ability",
			HeaderText = "Ability",
			Width = 200,
			ReadOnly = true,
			DefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = Color.FromArgb(40, 40, 45),
				ForeColor = Color.White,
				SelectionBackColor = Color.FromArgb(60, 60, 70),
				SelectionForeColor = Color.White
			}
		};
		grid.Columns.Add(dataGridViewColumn2);
		DataGridViewTextBoxColumn dataGridViewColumn3 = new DataGridViewTextBoxColumn
		{
			Name = "Near",
			HeaderText = "Min Nearby",
			Width = 65,
			DefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = Color.FromArgb(40, 40, 45),
				ForeColor = Color.White,
				SelectionBackColor = Color.FromArgb(60, 60, 70),
				SelectionForeColor = Color.White
			}
		};
		grid.Columns.Add(dataGridViewColumn3);
		DataGridViewTextBoxColumn dataGridViewColumn4 = new DataGridViewTextBoxColumn
		{
			Name = "HP%",
			HeaderText = "HP%",
			Width = 50,
			DefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = Color.FromArgb(40, 40, 45),
				ForeColor = Color.White,
				SelectionBackColor = Color.FromArgb(60, 60, 70),
				SelectionForeColor = Color.White
			}
		};
		grid.Columns.Add(dataGridViewColumn4);
		DataGridViewTextBoxColumn dataGridViewColumn5 = new DataGridViewTextBoxColumn
		{
			Name = "SP%",
			HeaderText = "SP%",
			Width = 50,
			DefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = Color.FromArgb(40, 40, 45),
				ForeColor = Color.White,
				SelectionBackColor = Color.FromArgb(60, 60, 70),
				SelectionForeColor = Color.White
			}
		};
		grid.Columns.Add(dataGridViewColumn5);
		DataGridViewTextBoxColumn dataGridViewColumn6 = new DataGridViewTextBoxColumn
		{
			Name = "Priority",
			HeaderText = "Priority",
			Width = 55,
			DefaultCellStyle = new DataGridViewCellStyle
			{
				BackColor = Color.FromArgb(40, 40, 45),
				ForeColor = Color.White,
				SelectionBackColor = Color.FromArgb(60, 60, 70),
				SelectionForeColor = Color.White
			}
		};
		grid.Columns.Add(dataGridViewColumn6);
	}

	private void RotationGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
	{
		if (e.RowIndex < 0 || e.RowIndex >= _plugin.Rotation.Count)
		{
			return;
		}
		RotationAbility rotationAbility = _plugin.Rotation[e.RowIndex];
		DataGridViewColumn dataGridViewColumn = _rotationList.Columns[e.ColumnIndex];
		if (dataGridViewColumn.Name == "Auto")
		{
			object value = _rotationList[e.ColumnIndex, e.RowIndex].Value;
			bool flag = false;
			int num;
			if (value is bool)
			{
				flag = (bool)value;
				num = 1;
			}
			else
			{
				num = 0;
			}
			rotationAbility.Autocast = (byte)(num & (flag ? 1 : 0)) != 0;
			_plugin.SaveRotation();
		}
		else if (dataGridViewColumn.Name == "CD(s)")
		{
			object value2 = _rotationList[e.ColumnIndex, e.RowIndex].Value;
			if (value2 != null && double.TryParse(value2.ToString(), out var result) && result >= 0.0)
			{
				rotationAbility.CooldownMs = (long)(result * 1000.0);
				_plugin.SaveRotation();
			}
		}
		else if (dataGridViewColumn.Name == "Near")
		{
			object value3 = _rotationList[e.ColumnIndex, e.RowIndex].Value;
			if (value3 != null && int.TryParse(value3.ToString(), out var result2) && result2 >= 0)
			{
				rotationAbility.MinNearbyEnemies = result2;
				_plugin.SaveRotation();
			}
		}
		else if (dataGridViewColumn.Name == "HP%")
		{
			object value4 = _rotationList[e.ColumnIndex, e.RowIndex].Value;
			if (value4 != null && int.TryParse(value4.ToString(), out var result3) && result3 >= 0 && result3 <= 100)
			{
				rotationAbility.TargetHpThreshold = result3;
				_plugin.SaveRotation();
			}
		}
		else if (dataGridViewColumn.Name == "SP%")
		{
			object value5 = _rotationList[e.ColumnIndex, e.RowIndex].Value;
			if (value5 != null && int.TryParse(value5.ToString(), out var result4) && result4 >= 0 && result4 <= 100)
			{
				rotationAbility.MinSpThreshold = result4;
				_plugin.SaveRotation();
			}
		}
		else if (dataGridViewColumn.Name == "Priority")
		{
			object value6 = _rotationList[e.ColumnIndex, e.RowIndex].Value;
			if (value6 != null && int.TryParse(value6.ToString(), out var result5) && result5 >= 0)
			{
				rotationAbility.Priority = result5;
				_plugin.SaveRotation();
			}
		}
	}

	private void RemoveSelected()
	{
		if (_rotationList.CurrentRow != null)
		{
			int index = _rotationList.CurrentRow.Index;
			_plugin.Rotation.RemoveAt(index);
			_plugin.SaveRotation();
			RefreshRotationList();
		}
	}

	private void MoveUp()
	{
		if (_rotationList.CurrentRow != null)
		{
			int index = _rotationList.CurrentRow.Index;
			if (index > 0)
			{
				SwapRotationRows(index, index - 1);
			}
		}
	}

	private void MoveDown()
	{
		if (_rotationList.CurrentRow != null)
		{
			int index = _rotationList.CurrentRow.Index;
			if (index >= 0 && index < _plugin.Rotation.Count - 1)
			{
				SwapRotationRows(index, index + 1);
			}
		}
	}

	private void SwapRotationRows(int a, int b)
	{
		List<RotationAbility> rotation = _plugin.Rotation;
		List<RotationAbility> rotation2 = _plugin.Rotation;
		RotationAbility value = _plugin.Rotation[b];
		RotationAbility value2 = _plugin.Rotation[a];
		rotation[a] = value;
		rotation2[b] = value2;
		_plugin.SaveRotation();
		_rotationList.SuspendLayout();
		for (int i = 0; i < _rotationList.Columns.Count; i++)
		{
			object value3 = _rotationList[i, a].Value;
			_rotationList[i, a].Value = _rotationList[i, b].Value;
			_rotationList[i, b].Value = value3;
		}
		_rotationList.CurrentCell = _rotationList[0, b];
		_rotationList.Rows[b].Selected = true;
		_rotationList.ResumeLayout();
	}

	private void RefreshRotationList()
	{
		_rotationList.CommitEdit(DataGridViewDataErrorContexts.Commit);
		_rotationList.Rows.Clear();
		foreach (RotationAbility item in _plugin.Rotation)
		{
			_rotationList.Rows.Add((item.CooldownMs > 0) ? ((object)((double)item.CooldownMs / 1000.0)) : ((object)0.0), item.Autocast, "[" + item.KeyLabel + "] " + item.Name, item.MinNearbyEnemies, item.TargetHpThreshold, (item.MinSpThreshold > 0) ? ((object)item.MinSpThreshold) : ((object)0), item.Priority);
		}
	}

	private void AddBuffSelection()
	{
		if (_abilityList.SelectedItems.Count == 0)
		{
			return;
		}
		foreach (object selectedItem in _abilityList.SelectedItems)
		{
			string display = selectedItem.ToString();
			HotbarAbility hotbarAbility = _hotbarAbilities.FirstOrDefault((HotbarAbility h) => h.Display == display);
			if (hotbarAbility != null)
			{
				RotationAbility rotationAbility = BuildAbilityFromHotbar(hotbarAbility);
				_plugin.EnrichFromApi(rotationAbility);
				_plugin.DiscoverToggleProperty(rotationAbility);
				if (hotbarAbility.ApiTargetType == "Foe")
				{
					_plugin.Log("API: [" + rotationAbility.Name + "] targets Foe — consider adding to Rotation instead of Buffs");
				}
				_plugin.BuffOrder.Add(rotationAbility);
			}
		}
		_plugin.SaveBuffOrder();
		RefreshBuffList();
		if (_buffPanel.Controls.Count > 0)
		{
			_buffPanel.ScrollControlIntoView(_buffPanel.Controls[_buffPanel.Controls.Count - 1]);
		}
	}

	private void RemoveBuffSelected()
	{
		if (_buffSelectedIndex >= 0 && _buffSelectedIndex < _plugin.BuffOrder.Count)
		{
			int buffSelectedIndex = _buffSelectedIndex;
			_plugin.BuffOrder.RemoveAt(buffSelectedIndex);
			_plugin.SaveBuffOrder();
			_buffSelectedIndex = -1;
			RefreshBuffList();
		}
	}

	private void RemoveBuffAt(int idx)
	{
		if (idx >= 0 && idx < _plugin.BuffOrder.Count)
		{
			_plugin.BuffOrder.RemoveAt(idx);
			_plugin.SaveBuffOrder();
			RefreshBuffList();
		}
	}

	private void MoveBuffAt(int idx, int direction)
	{
		int num = idx + direction;
		if (idx >= 0 && num >= 0 && num < _plugin.BuffOrder.Count)
		{
			List<RotationAbility> buffOrder = _plugin.BuffOrder;
			List<RotationAbility> buffOrder2 = _plugin.BuffOrder;
			int index = num;
			RotationAbility value = _plugin.BuffOrder[num];
			RotationAbility value2 = _plugin.BuffOrder[idx];
			buffOrder[idx] = value;
			buffOrder2[index] = value2;
			_plugin.SaveBuffOrder();
			RefreshBuffList();
		}
	}

	private void RefreshBuffList()
	{
		_buffPanel.SuspendLayout();
		_buffPanel.Controls.Clear();
		int y = 4;
		for (int i = 0; i < _plugin.BuffOrder.Count; i++)
		{
			GroupBox value = CreateBuffEntryCard(i, _plugin.BuffOrder[i], ref y);
			_buffPanel.Controls.Add(value);
		}
		_buffPanel.AutoScrollMinSize = new Size(0, y + 30);
		_buffPanel.ResumeLayout();
		_buffPanel.PerformLayout();
		if (_plugin.BuffOrder.Count == 0)
		{
			Label value2 = new Label
			{
				Text = "No buffs configured. Click \"+ Add Buff\" above to add one.",
				Location = new Point(12, 10),
				Size = new Size(_buffPanel.Width - 24, 30),
				ForeColor = Color.FromArgb(140, 140, 140),
				BackColor = Color.Transparent,
				Font = new System.Drawing.Font("Segoe UI", 10f, FontStyle.Italic),
				TextAlign = ContentAlignment.MiddleLeft
			};
			_buffPanel.Controls.Add(value2);
		}
	}

	private GroupBox CreateBuffEntryCard(int index, RotationAbility ability, ref int y)
	{
		int num = _buffPanel.ClientSize.Width - 24;
		if (num < 300)
		{
			num = 300;
		}
		GroupBox card = new GroupBox
		{
			Text = $"#{index + 1}: {ability.Name}",
			Location = new Point(6, y),
			Size = new Size(num, 220),
			ForeColor = Color.White,
			BackColor = Color.FromArgb(38, 38, 44),
			FlatStyle = FlatStyle.Flat
		};
		int num2 = 22;
		int num3 = 12;
		Label value = new Label
		{
			Text = "When to Cast:",
			Location = new Point(num3, num2),
			Size = new Size(100, 20),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f)
		};
		card.Controls.Add(value);
		RadioButton autoRadio = new RadioButton
		{
			Text = "Automatic (cast when buff is missing or expires)",
			Location = new Point(num3 + 100, num2),
			Size = new Size(310, 18),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = ability.BuffOnMissing,
			Tag = index
		};
		int capturedIndexAuto = index;
		autoRadio.CheckedChanged += delegate
		{
			if (autoRadio.Checked && capturedIndexAuto < _plugin.BuffOrder.Count)
			{
				RotationAbility rotationAbility = _plugin.BuffOrder[capturedIndexAuto];
				rotationAbility.BuffOnMissing = true;
				_plugin.SaveBuffOrder();
			}
		};
		card.Controls.Add(autoRadio);
		num2 += 22;
		RadioButton manualRadio = new RadioButton
		{
			Text = "Manual Timer (cast on a set schedule)",
			Location = new Point(num3 + 100, num2),
			Size = new Size(260, 18),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = (!ability.BuffOnMissing && (ability.DurationMinutes > 0 || ability.DurationSeconds > 0)),
			Tag = index
		};
		int capturedIndexManual = index;
		manualRadio.CheckedChanged += delegate
		{
			if (manualRadio.Checked && capturedIndexManual < _plugin.BuffOrder.Count)
			{
				RotationAbility rotationAbility = _plugin.BuffOrder[capturedIndexManual];
				rotationAbility.BuffOnMissing = false;
				_plugin.SaveBuffOrder();
			}
		};
		card.Controls.Add(manualRadio);
		num2 += 22;
		Label value2 = new Label
		{
			Text = "Minutes",
			Location = new Point(num3 + 110, num2),
			Size = new Size(55, 20),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			TextAlign = ContentAlignment.MiddleLeft
		};
		card.Controls.Add(value2);
		NumericUpDown minsUpDown = new NumericUpDown
		{
			Location = new Point(num3 + 165, num2),
			Size = new Size(55, 22),
			Minimum = 0m,
			Maximum = 60m,
			Value = ability.DurationMinutes,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			BorderStyle = BorderStyle.FixedSingle,
			TextAlign = HorizontalAlignment.Center
		};
		int capturedIndexMins = index;
		minsUpDown.ValueChanged += delegate
		{
			if (capturedIndexMins < _plugin.BuffOrder.Count)
			{
				RotationAbility rotationAbility = _plugin.BuffOrder[capturedIndexMins];
				rotationAbility.DurationMinutes = (int)minsUpDown.Value;
				_plugin.SaveBuffOrder();
				UpdateBuffSummary(card, rotationAbility, capturedIndexMins);
			}
		};
		card.Controls.Add(minsUpDown);
		Label value3 = new Label
		{
			Text = "and",
			Location = new Point(num3 + 223, num2),
			Size = new Size(28, 20),
			ForeColor = Color.FromArgb(200, 200, 200),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			TextAlign = ContentAlignment.MiddleCenter
		};
		card.Controls.Add(value3);
		NumericUpDown secsUpDown = new NumericUpDown
		{
			Location = new Point(num3 + 252, num2),
			Size = new Size(55, 22),
			Minimum = 0m,
			Maximum = 59m,
			Value = ability.DurationSeconds,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			BorderStyle = BorderStyle.FixedSingle,
			TextAlign = HorizontalAlignment.Center
		};
		int capturedIndexSecs = index;
		secsUpDown.ValueChanged += delegate
		{
			if (capturedIndexSecs < _plugin.BuffOrder.Count)
			{
				RotationAbility rotationAbility = _plugin.BuffOrder[capturedIndexSecs];
				rotationAbility.DurationSeconds = (int)secsUpDown.Value;
				_plugin.SaveBuffOrder();
				UpdateBuffSummary(card, rotationAbility, capturedIndexSecs);
			}
		};
		card.Controls.Add(secsUpDown);
		Label value4 = new Label
		{
			Text = "Seconds",
			Location = new Point(num3 + 310, num2),
			Size = new Size(55, 20),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			TextAlign = ContentAlignment.MiddleLeft
		};
		card.Controls.Add(value4);
		num2 += 24;
		Label label = new Label
		{
			Location = new Point(num3 + 110, num2),
			Size = new Size(320, 18),
			ForeColor = Color.FromArgb(200, 200, 150),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f, FontStyle.Italic),
			Text = ""
		};
		card.Controls.Add(label);
		card.Tag = label;
		UpdateBuffSummary(card, ability, index);
		num2 += 22;
		CheckBox requiresChk = new CheckBox
		{
			Text = "Does this ability need a target before casting?",
			Location = new Point(num3, num2),
			Size = new Size(320, 20),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = ability.RequiresTarget
		};
		int capturedIndexReq = index;
		card.Controls.Add(requiresChk);
		num2 += 22;
		Label targetLabel = new Label
		{
			Text = "Who should be targeted?",
			Location = new Point(num3 + 20, num2),
			Size = new Size(160, 20),
			ForeColor = Color.FromArgb(180, 180, 180),
			BackColor = Color.Transparent,
			Font = new System.Drawing.Font("Segoe UI", 8f),
			Visible = ability.RequiresTarget
		};
		targetLabel.Tag = "targetLabel";
		card.Controls.Add(targetLabel);
		ComboBox targetCombo = new ComboBox
		{
			Location = new Point(num3 + 180, num2),
			Size = new Size(130, 22),
			DropDownStyle = ComboBoxStyle.DropDownList,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			Visible = ability.RequiresTarget,
			Tag = "targetCombo"
		};
		ComboBox.ObjectCollection items = targetCombo.Items;
		object[] array = new string[3] { "Myself", "A Party Member", "Both" };
		object[] items2 = array;
		items.AddRange(items2);
		targetCombo.SelectedItem = ((ability.TargetType == "A Party Member") ? "A Party Member" : ((ability.TargetType == "Both") ? "Both" : "Myself"));
		int capturedIndexTarget = index;
		targetCombo.SelectedIndexChanged += delegate
		{
			if (capturedIndexTarget < _plugin.BuffOrder.Count)
			{
				_plugin.BuffOrder[capturedIndexTarget].TargetType = targetCombo.SelectedItem?.ToString() ?? "Myself";
				_plugin.SaveBuffOrder();
			}
		};
		card.Controls.Add(targetCombo);
		requiresChk.CheckedChanged += delegate
		{
			targetLabel.Visible = requiresChk.Checked;
			targetCombo.Visible = requiresChk.Checked;
			if (capturedIndexReq < _plugin.BuffOrder.Count)
			{
				RotationAbility rotationAbility = _plugin.BuffOrder[capturedIndexReq];
				rotationAbility.RequiresTarget = requiresChk.Checked;
				if (!requiresChk.Checked)
				{
					rotationAbility.TargetType = "Myself";
				}
				_plugin.SaveBuffOrder();
			}
		};
		num2 += 24;
		CheckBox activeChk = new CheckBox
		{
			Text = "Active",
			Location = new Point(num3, num2),
			Size = new Size(70, 20),
			ForeColor = Color.White,
			BackColor = Color.Transparent,
			Checked = ability.Autocast
		};
		int capturedIndexActive = index;
		activeChk.CheckedChanged += delegate
		{
			if (capturedIndexActive < _plugin.BuffOrder.Count)
			{
				RotationAbility rotationAbility = _plugin.BuffOrder[capturedIndexActive];
				rotationAbility.Autocast = activeChk.Checked;
				if (!activeChk.Checked)
				{
					rotationAbility.BuffOnMissing = false;
				}
				else
				{
					rotationAbility.BuffOnMissing = autoRadio.Checked;
				}
				_plugin.SaveBuffOrder();
			}
		};
		card.Controls.Add(activeChk);
		Button button = new Button
		{
			Text = "✕ Remove",
			Location = new Point(num3 + 75, num2),
			Size = new Size(72, 22),
			BackColor = Color.FromArgb(180, 40, 40),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat,
			Font = new System.Drawing.Font("Segoe UI", 7f)
		};
		int capturedRemove = index;
		button.Click += delegate
		{
			RemoveBuffAt(capturedRemove);
		};
		card.Controls.Add(button);
		Button button2 = new Button
		{
			Text = "▲",
			Location = new Point(num - 155, num2 - 2),
			Size = new Size(35, 24),
			BackColor = Color.FromArgb(60, 60, 65),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		int capturedIndexUp = index;
		button2.Click += delegate
		{
			MoveBuffAt(capturedIndexUp, -1);
		};
		card.Controls.Add(button2);
		Button button3 = new Button
		{
			Text = "▼",
			Location = new Point(num - 118, num2 - 2),
			Size = new Size(35, 24),
			BackColor = Color.FromArgb(60, 60, 65),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		int capturedIndexDown = index;
		button3.Click += delegate
		{
			MoveBuffAt(capturedIndexDown, 1);
		};
		card.Controls.Add(button3);
		num2 += 28;
		int capturedIndexSelect = index;
		card.Click += delegate
		{
			_buffSelectedIndex = capturedIndexSelect;
		};
		foreach (Control control in card.Controls)
		{
			control.Click += delegate
			{
				_buffSelectedIndex = capturedIndexSelect;
			};
		}
		button.BringToFront();
		y += card.Height + 6;
		return card;
	}

	private void UpdateBuffSummary(GroupBox card, RotationAbility ability, int index)
	{
		if (!(card.Tag is Label label))
		{
			return;
		}
		if (ability.DurationMinutes == 0 && ability.DurationSeconds == 0)
		{
			if (ability.BuffOnMissing)
			{
				label.Text = "→ Automatically detects when buff is missing and re-applies it.";
			}
			else
			{
				label.Text = "⚠ Please enter a duration greater than 0.";
			}
			return;
		}
		string text = ((ability.DurationMinutes > 0) ? $"{ability.DurationMinutes} minute{((ability.DurationMinutes != 1) ? "s" : "")}" : "");
		string text2 = ((ability.DurationSeconds > 0) ? $"{ability.DurationSeconds} second{((ability.DurationSeconds != 1) ? "s" : "")}" : "");
		string text3 = "";
		text3 = ((string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text2)) ? (text + text2) : (text + " and " + text2));
		label.Text = "→ This buff will be cast every " + text3 + ".";
	}

	private void SetAllRotationAutocast(bool enabled)
	{
		foreach (RotationAbility item in _plugin.Rotation)
		{
			item.Autocast = enabled;
		}
		_plugin.SaveRotation();
		RefreshRotationList();
	}

	private void SetAllBuffAutocast(bool enabled)
	{
		foreach (RotationAbility item in _plugin.BuffOrder)
		{
			item.Autocast = enabled;
			if (enabled)
			{
				item.BuffOnMissing = false;
			}
		}
		_plugin.SaveBuffOrder();
		RefreshBuffList();
	}

	private void SetAllRotationAutoCd()
	{
		_plugin.SaveRotation();
		RefreshRotationList();
	}

	private void AddToggleSelection()
	{
		if (_abilityList.SelectedItems.Count == 0)
		{
			return;
		}
		foreach (object selectedItem in _abilityList.SelectedItems)
		{
			string display = selectedItem.ToString();
			HotbarAbility hotbarAbility = _hotbarAbilities.FirstOrDefault((HotbarAbility h) => h.Display == display);
			if (hotbarAbility != null)
			{
				RotationAbility rotationAbility = BuildAbilityFromHotbar(hotbarAbility);
				rotationAbility.Autocast = true;
				_plugin.EnrichFromApi(rotationAbility);
				_plugin.DiscoverToggleProperty(rotationAbility);
				_plugin.ToggleBuffs.Add(rotationAbility);
			}
		}
		_plugin.SaveToggleBuffs();
		_rebuildTogglePanel?.Invoke();
	}

	private void RemoveToggleAt(int idx)
	{
		if (idx >= 0 && idx < _plugin.ToggleBuffs.Count)
		{
			_plugin.ToggleBuffs.RemoveAt(idx);
			_plugin.SaveToggleBuffs();
			_rebuildTogglePanel?.Invoke();
		}
	}

	private void MoveToggleAt(int idx, int direction)
	{
		int num = idx + direction;
		if (idx >= 0 && num >= 0 && num < _plugin.ToggleBuffs.Count)
		{
			List<RotationAbility> toggleBuffs = _plugin.ToggleBuffs;
			List<RotationAbility> toggleBuffs2 = _plugin.ToggleBuffs;
			int index = num;
			RotationAbility value = _plugin.ToggleBuffs[num];
			RotationAbility value2 = _plugin.ToggleBuffs[idx];
			toggleBuffs[idx] = value;
			toggleBuffs2[index] = value2;
			_plugin.SaveToggleBuffs();
			_rebuildTogglePanel?.Invoke();
		}
	}

	private void EditToggleStatePropertyAt(int idx)
	{
		if (idx < 0 || idx >= _plugin.ToggleBuffs.Count)
		{
			return;
		}
		RotationAbility rotationAbility = _plugin.ToggleBuffs[idx];
		using Form form = new Form
		{
			Text = "Toggle State Property",
			Size = new Size(400, 180),
			FormBorderStyle = FormBorderStyle.FixedDialog,
			MaximizeBox = false,
			MinimizeBox = false,
			StartPosition = FormStartPosition.CenterParent,
			BackColor = Color.FromArgb(30, 30, 35),
			ForeColor = Color.White
		};
		Label label = new Label
		{
			Text = "Property DID (0x...):",
			Location = new Point(12, 16),
			Size = new Size(360, 20)
		};
		TextBox textBox = new TextBox
		{
			Text = ((rotationAbility.ToggleStatePropertyDid != 0) ? $"0x{rotationAbility.ToggleStatePropertyDid:X8}" : ""),
			Location = new Point(12, 40),
			Width = 360,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			BorderStyle = BorderStyle.FixedSingle
		};
		Label label2 = new Label
		{
			Text = "Expected value when active:",
			Location = new Point(12, 68),
			Size = new Size(360, 20)
		};
		TextBox textBox2 = new TextBox
		{
			Text = rotationAbility.ToggleExpectedValue,
			Location = new Point(12, 92),
			Width = 360,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			BorderStyle = BorderStyle.FixedSingle
		};
		Button button = new Button
		{
			Text = "OK",
			Location = new Point(216, 120),
			Size = new Size(75, 23),
			DialogResult = DialogResult.OK,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		Button button2 = new Button
		{
			Text = "Cancel",
			Location = new Point(297, 120),
			Size = new Size(75, 23),
			DialogResult = DialogResult.Cancel,
			BackColor = Color.FromArgb(50, 50, 55),
			ForeColor = Color.White,
			FlatStyle = FlatStyle.Flat
		};
		form.Controls.AddRange(new Control[6] { label, textBox, label2, textBox2, button, button2 });
		if (form.ShowDialog(this) == DialogResult.OK)
		{
			string text = textBox.Text.Trim();
			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				text = text.Substring(2);
			}
			if (uint.TryParse(text, NumberStyles.HexNumber, null, out var result))
			{
				rotationAbility.ToggleStatePropertyDid = result;
			}
			else
			{
				rotationAbility.ToggleStatePropertyDid = 0u;
			}
			rotationAbility.ToggleExpectedValue = textBox2.Text.Trim();
			_plugin.SaveToggleBuffs();
			_rebuildTogglePanel?.Invoke();
		}
	}

	private void LoadSettings()
	{
		if (_plugin._config == null)
		{
			return;
		}
		_delayNumeric.Value = Math.Max(50, _plugin._config.PressDelayMs);
		_buffDelayNumeric.Value = Math.Max(50, _plugin._config.BuffPressDelayMs);
		int triggerKey = _plugin._config.TriggerKey;
		if (triggerKey > 0)
		{
			string key = TriggerVkMap.FirstOrDefault((KeyValuePair<string, int> kvp) => kvp.Value == triggerKey).Key;
			if (key != null)
			{
				int num = _triggerKeyCombo.Items.IndexOf(key);
				if (num >= 0)
				{
					_triggerKeyCombo.SelectedIndex = num;
				}
			}
		}
		int triggerKey2 = _plugin._config.TriggerKey2;
		if (triggerKey2 > 0)
		{
			string key2 = TriggerVkMap.FirstOrDefault((KeyValuePair<string, int> kvp) => kvp.Value == triggerKey2).Key;
			if (key2 != null)
			{
				int num2 = _triggerKey2Combo.Items.IndexOf(key2);
				if (num2 >= 0)
				{
					_triggerKey2Combo.SelectedIndex = num2;
				}
			}
		}
		int buffTriggerKey = _plugin._config.BuffTriggerKey;
		if (buffTriggerKey > 0)
		{
			string key3 = TriggerVkMap.FirstOrDefault((KeyValuePair<string, int> kvp) => kvp.Value == buffTriggerKey).Key;
			if (key3 != null)
			{
				int num3 = _buffTriggerKeyCombo.Items.IndexOf(key3);
				if (num3 >= 0)
				{
					_buffTriggerKeyCombo.SelectedIndex = num3;
				}
			}
		}
		int pauseKey = _plugin._config.PauseToggleKey;
		if (pauseKey > 0)
		{
			string key4 = TriggerVkMap.FirstOrDefault((KeyValuePair<string, int> kvp) => kvp.Value == pauseKey).Key;
			if (key4 != null)
			{
				int num4 = _pauseKeyCombo.Items.IndexOf(key4);
				if (num4 >= 0)
				{
					_pauseKeyCombo.SelectedIndex = num4;
				}
			}
		}
		_autoOnTargetChk.Checked = _plugin._config.AutocastRotationOnTarget;
		_meleeChk.Checked = _plugin._config.MeleeRangeEnabled;
		RefreshBuffList();
	}

	private void SaveAndClose()
	{
		if (_plugin._config != null)
		{
			string key = _triggerKeyCombo.SelectedItem?.ToString() ?? "R.Mouse";
			_plugin._config.TriggerKey = (TriggerVkMap.TryGetValue(key, out var value) ? value : 2);
			_plugin._config.PressDelayMs = (int)_delayNumeric.Value;
			string text = _triggerKey2Combo.SelectedItem?.ToString() ?? "None";
			_plugin._config.TriggerKey2 = ((!(text == "None")) ? (TriggerVkMap.TryGetValue(text, out var value2) ? value2 : 0) : 0);
			string key2 = _buffTriggerKeyCombo.SelectedItem?.ToString() ?? "R.Mouse";
			_plugin._config.BuffTriggerKey = (TriggerVkMap.TryGetValue(key2, out var value3) ? value3 : 2);
			_plugin._config.BuffPressDelayMs = (int)_buffDelayNumeric.Value;
			string text2 = _pauseKeyCombo.SelectedItem?.ToString() ?? "None";
			_plugin._config.PauseToggleKey = ((!(text2 == "None")) ? (TriggerVkMap.TryGetValue(text2, out var value4) ? value4 : 0) : 0);
			_plugin._config.AutocastRotationOnTarget = _autoOnTargetChk.Checked;
			_plugin._config.MeleeRangeEnabled = _meleeChk.Checked;
			_plugin._config.FormX = base.Location.X;
			_plugin._config.FormY = base.Location.Y;
			_plugin._config.FormWidth = base.Width;
			_plugin._config.FormHeight = base.Height;
			_rotationList.CommitEdit(DataGridViewDataErrorContexts.Commit);
			_plugin.SaveConfig();
			_plugin.SaveRotation();
			_plugin.SaveBuffOrder();
			_plugin.SaveToggleBuffs();
			_plugin.BuffManager?.SaveProfile();
			Close();
		}
	}
}
public class StanceStatus
{
	public ToggledStanceConfig Config { get; set; }

	public bool IsActive { get; set; }

	public string CurrentValue { get; set; }

	public bool FiredThisZone { get; set; }
}
public class ToggledStanceConfig
{
	public string Name { get; set; }

	public uint ActionDid { get; set; }

	public uint CommandValue { get; set; }

	public uint PropertyDid { get; set; }

	public string ExpectedValue { get; set; }

	public bool Enabled { get; set; } = true;
}
