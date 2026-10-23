using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Events;
// DH5-PORT: removed using VoK.Sdk.Plugins (no IPluginUI in DH5)
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

	internal IEntity _currentEntity;
	internal IEntity _selectedObject;
	internal IReadOnlyList<IEntity> _entities = System.Array.Empty<IEntity>();

	internal void RefreshCache()
	{
		if (_gameData == null)
		{
			return;
		}
		try
		{
			_currentEntity = _gameData.GetCurrentCharacterAsync().ConfigureAwait(false).GetAwaiter().GetResult();
		}
		catch
		{
		}
		try
		{
			_selectedObject = _gameData.GetSelectedObjectAsync().ConfigureAwait(false).GetAwaiter().GetResult();
		}
		catch
		{
		}
		try
		{
			_entities = _gameData.GetEntitiesAsync().ConfigureAwait(false).GetAwaiter().GetResult();
		}
		catch
		{
		}
	}

	private readonly IDdoEventProvider _eventProvider;

	private readonly ConcurrentDictionary<int, BuffEffectState> _activeEffects = new ConcurrentDictionary<int, BuffEffectState>();

	private readonly GameDataCache _gameCache;

	public BuffManager(IDdoGameDataProvider gameDataProvider, IDdoEventProvider eventProvider, string configDir, Action<string> log, GameDataCache gameCache = null)
	{
		_gameDataProvider = gameDataProvider;
		_gameData = gameDataProvider;
		_propertyMaster = _gameData?.PropertyMaster;
		_configDir = configDir;
		_eventProvider = eventProvider;
		_log = log;
		_gameCache = gameCache;
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
		IDdoEventProvider eventProvider = _eventProvider;
		if (eventProvider != null)
		{
			eventProvider.OnLogin.AddHandler(delegate
			{
				_log("BuffManager: OnLogin — resetting state");
				_lastFired.Clear();
				_activeEffects.Clear();
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
				_activeEffects.Clear();
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
			eventProvider.OnEffectExpress.AddHandler(delegate(IEffectExpress effect)
			{
				try
				{
					if (effect != null)
					{
						int effectId = effect.EffectId.GetValueOrDefault();
						BuffEffectState state = new BuffEffectState
						{
							EffectId = effectId,
							EffectDid = effect.EffectDid,
							Duration = effect.Duration.GetValueOrDefault(),
							IsPaused = false,
							ApplicationTimeUtc = DateTime.UtcNow,
							LastUpdateUtc = DateTime.UtcNow
						};
						if (effectId != 0)
						{
							_activeEffects[effectId] = state;
						}
						CaptureEffectDidFromExpress(effect);
					}
				}
				catch (Exception ex)
				{
					_log("BuffManager: OnEffectExpress error " + ex.Message);
				}
				return Task.CompletedTask;
			});
			eventProvider.OnEffectSuppress.AddHandler(delegate(int effectId)
			{
				try
				{
					_activeEffects.TryRemove(effectId, out var _);
				}
				catch (Exception ex)
				{
					_log("BuffManager: OnEffectSuppress error " + ex.Message);
				}
				return Task.CompletedTask;
			});
			eventProvider.OnEffectUpdateDuration.AddHandler(delegate(IEffectUpdateDuration update)
			{
				try
				{
					if (update != null && _activeEffects.TryGetValue(update.EffectId, out var state))
					{
						state.Duration = update.RemainingSeconds;
						state.IsPaused = update.IsPaused;
						state.LastUpdateUtc = DateTime.UtcNow;
					}
				}
				catch (Exception ex)
				{
					_log("BuffManager: OnEffectUpdateDuration error " + ex.Message);
				}
				return Task.CompletedTask;
			});
		}
	}

	private void CaptureEffectDidFromExpress(IEffectExpress effect)
	{
		try
		{
			if (_propertyMaster == null || effect.EffectDid == 0)
			{
				return;
			}
			IPropertyCollection effectProps = _propertyMaster.GetPropertyCollection((uint)effect.EffectDid);
			if (effectProps == null)
			{
				return;
			}
			foreach (IProperty property in effectProps.Properties)
			{
				if (!(property is IArrayProperty arr))
				{
					continue;
				}
				foreach (IProperty entry in arr.Properties)
				{
					if (!(entry is IPropertyCollection entryProps))
					{
						continue;
					}
					foreach (IProperty ep in entryProps.Properties)
					{
						uint actionDid = 0u;
						if (ep is IUInt32Property u32 && u32.UInt32Value.HasValue && u32.UInt32Value.Value > 1879048192 && u32.UInt32Value.Value < 1895825407)
						{
							actionDid = u32.UInt32Value.Value;
						}
						else if (ep is IInt32Property i32 && i32.Int32Value.HasValue && i32.Int32Value.Value > 1879048192 && i32.Int32Value.Value < 1895825407)
						{
							actionDid = (uint)i32.Int32Value.Value;
						}
						if (actionDid != 0 && _gameCache != null && _gameCache.GetCachedEffectDid(actionDid) == 0)
						{
							_gameCache.CacheEffectDid(actionDid, (uint)effect.EffectDid);
							_log($"Effect DID captured: action 0x{actionDid:X8} → effect 0x{effect.EffectDid:X8}");
						}
					}
				}
			}
		}
		catch
		{
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
			bool? flag = _gameDataProvider?.InTownAsync().GetAwaiter().GetResult();
			if (flag.HasValue)
			{
				return flag.Value;
			}
			IEntity entity = _currentEntity;
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

	public bool IsEffectActiveByDid(uint effectDid)
	{
		if (effectDid == 0)
		{
			return false;
		}
		try
		{
			if (_activeEffects.Count > 0)
			{
				foreach (BuffEffectState state in _activeEffects.Values)
				{
					if (state.EffectDid == (int)effectDid)
					{
						if (state.Duration == -1f || state.Duration > 0f)
						{
							return true;
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

	public bool IsBuffActive(AutoBuffConfig buff)
	{
		try
		{
			uint num = buff.EffectDid ?? buff.ActionDid;
			if (num == 0)
			{
				return false;
			}
			bool flag = false;
			bool flag2 = _activeEffects.Count > 0;
			if (flag2)
			{
				foreach (BuffEffectState state in _activeEffects.Values)
				{
					if (state.EffectDid != (int)num)
					{
						continue;
					}
					if (state.Duration == -1f)
					{
						return true;
					}
					if (state.Duration > 0f && !state.IsPaused)
					{
						flag = true;
						break;
					}
					if (state.Duration > 0f && state.IsPaused)
					{
						flag = true;
						break;
					}
				}
				if (flag)
				{
					return true;
				}
			}
			IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _gameDataProvider?.GetActiveEffects();
			if (readOnlyDictionary == null || readOnlyDictionary.Count == 0)
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
					DateTime? dateTime = (Dh5Compat.EffectExpiration(value));
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
					IEntity entity = _currentEntity;
					if (entity != null && _propertyMaster != null)
					{
						foreach (KeyValuePair<int, IActiveEffect> item2 in readOnlyDictionary)
						{
							IPropertyCollection propertyCollection = _propertyMaster.GetPropertyCollection((uint)item2.Value.EffectDid);
							if (propertyCollection == null)
							{
								continue;
							}
							foreach (IProperty property in propertyCollection.Properties)
							{
								if (property is IUInt32Property { UInt32Value: var uInt32Value } && uInt32Value == buff.ActionDid)
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
			System.IO.File.AppendAllText(System.IO.Path.Combine(RotationHelperPlugin.AppDataDir, "hudentry.log"), $"[{DateTime.Now:HH:mm:ss.fff}] GetBuffRemaining entry name={buff.Name} gdppNull={_gameDataProvider==null} activeEffects={_activeEffects.Count}\r\n");
			uint num = buff.EffectDid ?? buff.ActionDid;
			if (num == 0)
			{
				return new BuffRemaining
				{
					IsActive = false
				};
			}
			if (_activeEffects.Count > 0)
			{
				foreach (BuffEffectState state in _activeEffects.Values)
				{
					if (state.EffectDid != (int)num)
					{
						continue;
					}
					if (state.Duration == -1f)
					{
						return new BuffRemaining
						{
							IsActive = true,
							RemainingSeconds = int.MaxValue
						};
					}
					if (state.Duration > 0f)
					{
						int remainingSeconds = (int)state.Duration;
						if (state.IsPaused)
						{
							return new BuffRemaining
							{
								IsActive = true,
								RemainingSeconds = remainingSeconds
							};
						}
						double elapsed = (DateTime.UtcNow - state.LastUpdateUtc).TotalSeconds;
						int num3 = Math.Max(0, remainingSeconds - (int)elapsed);
						return new BuffRemaining
						{
							IsActive = num3 > 0,
							RemainingSeconds = num3
						};
					}
				}
			}
			IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = null;
			try { readOnlyDictionary = _gameDataProvider?.GetActiveEffects(); } catch (Exception ex) { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "buffdebug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] GetActiveEffects THREW: {ex}\r\n"); }
			System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "buffdebug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] BuffDebug call from {buff.Name} num=0x{num:X8} dictNull={readOnlyDictionary==null} dictCount={(readOnlyDictionary==null?0:readOnlyDictionary.Count)}\r\n");
			if (readOnlyDictionary != null && readOnlyDictionary.Count > 0)
			{
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
						DateTime? dateTime = (Dh5Compat.EffectExpiration(value));
						if (dateTime.HasValue && dateTime.Value > DateTime.UtcNow)
						{
							int remainingSeconds2 = (int)(dateTime.Value - DateTime.UtcNow).TotalSeconds;
							return new BuffRemaining
							{
								IsActive = true,
								RemainingSeconds = remainingSeconds2
							};
						}
					}
				}
				if (!string.IsNullOrEmpty(buff.Name))
				{
					IPropertyMaster namePm = _gameDataProvider?.PropertyMaster;
					if (namePm != null)
					{
						foreach (KeyValuePair<int, IActiveEffect> nameItem in readOnlyDictionary)
						{
							IPropertyCollection nameProps = namePm.GetPropertyCollection((uint)nameItem.Value.EffectDid);
							if (nameProps == null)
							{
								continue;
							}
							string effText = RotationHelperPlugin.ExtractTextFromProps(nameProps, (uint)nameItem.Value.EffectDid, namePm);
							if (string.IsNullOrEmpty(effText))
							{
								try { File.AppendAllText(Path.Combine(System.IO.Path.GetTempPath(), "buffdebug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] BuffDebug [{buff.Name}] effectDid=0x{nameItem.Value.EffectDid:X8} effText=null\r\n"); } catch {}
								continue;
							}
							string stripped = effText.StartsWith("Spell: ", StringComparison.OrdinalIgnoreCase) ? effText.Substring(7) : effText;
							try { File.AppendAllText(Path.Combine(System.IO.Path.GetTempPath(), "buffdebug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] BuffDebug [{buff.Name}] effectDid=0x{nameItem.Value.EffectDid:X8} effText='{effText}' stripped='{stripped.Trim()}'\r\n"); } catch {}
							if (!string.Equals(stripped.Trim(), buff.Name.Trim(), StringComparison.OrdinalIgnoreCase) && !stripped.Trim().Contains(buff.Name.Trim(), StringComparison.OrdinalIgnoreCase) && !buff.Name.Trim().Contains(stripped.Trim(), StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}
							float nameDuration = nameItem.Value.Duration.GetValueOrDefault();
							if (nameDuration == -1f)
							{
								return new BuffRemaining
								{
									IsActive = true,
									RemainingSeconds = int.MaxValue
								};
							}
							DateTime? nameExpiry = Dh5Compat.EffectExpiration(nameItem.Value);
							int nameRemaining = (nameExpiry.HasValue ? (int)Math.Max(0.0, (nameExpiry.Value - DateTime.UtcNow).TotalSeconds) : (int)Math.Max(0f, nameDuration));
							return new BuffRemaining
							{
								IsActive = true,
								RemainingSeconds = nameRemaining
							};
						}
					}
				}
				return new BuffRemaining
				{
					IsActive = false
				};
			}
			if (!string.IsNullOrEmpty(buff.Name))
			{
				IPropertyMaster namePm2 = _gameDataProvider?.PropertyMaster;
				if (namePm2 != null && readOnlyDictionary != null)
				{
					foreach (KeyValuePair<int, IActiveEffect> nameItem in readOnlyDictionary)
					{
						IPropertyCollection nameProps = namePm2.GetPropertyCollection((uint)nameItem.Value.EffectDid);
						if (nameProps == null)
						{
							continue;
						}
						string effText = RotationHelperPlugin.ExtractTextFromProps(nameProps, (uint)nameItem.Value.EffectDid, namePm2);
						if (string.IsNullOrEmpty(effText))
						{
							continue;
						}
						if (effText.StartsWith("Spell: ", StringComparison.OrdinalIgnoreCase))
						{
							effText = effText.Substring(7);
						}
						if (!string.Equals(effText.Trim(), buff.Name.Trim(), StringComparison.OrdinalIgnoreCase) && !effText.Trim().Contains(buff.Name.Trim(), StringComparison.OrdinalIgnoreCase) && !buff.Name.Trim().Contains(effText.Trim(), StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						float nameDuration = nameItem.Value.Duration.GetValueOrDefault();
						if (nameDuration == -1f)
						{
							return new BuffRemaining
							{
								IsActive = true,
								RemainingSeconds = int.MaxValue
							};
						}
						DateTime? nameExpiry = Dh5Compat.EffectExpiration(nameItem.Value);
						int nameRemaining = (nameExpiry.HasValue ? (int)Math.Max(0.0, (nameExpiry.Value - DateTime.UtcNow).TotalSeconds) : (int)Math.Max(0f, nameDuration));
						return new BuffRemaining
						{
							IsActive = true,
							RemainingSeconds = nameRemaining
						};
					}
				}
			}
			if (!string.IsNullOrEmpty(buff.Name))
			{
				IPropertyMaster namePm2 = _gameDataProvider?.PropertyMaster;
				if (namePm2 != null && readOnlyDictionary != null)
				{
					foreach (KeyValuePair<int, IActiveEffect> nameItem in readOnlyDictionary)
					{
						IPropertyCollection nameProps = namePm2.GetPropertyCollection((uint)nameItem.Value.EffectDid);
						if (nameProps == null)
						{
							continue;
						}
						string effText = RotationHelperPlugin.ExtractTextFromProps(nameProps, (uint)nameItem.Value.EffectDid, namePm2);
						if (string.IsNullOrEmpty(effText))
						{
							continue;
						}
						if (effText.StartsWith("Spell: ", StringComparison.OrdinalIgnoreCase))
						{
							effText = effText.Substring(7);
						}
						if (!string.Equals(effText.Trim(), buff.Name.Trim(), StringComparison.OrdinalIgnoreCase) && !effText.Trim().Contains(buff.Name.Trim(), StringComparison.OrdinalIgnoreCase) && !buff.Name.Trim().Contains(effText.Trim(), StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						float nameDuration = nameItem.Value.Duration.GetValueOrDefault();
						if (nameDuration == -1f)
						{
							return new BuffRemaining
							{
								IsActive = true,
								RemainingSeconds = int.MaxValue
							};
						}
						DateTime? nameExpiry = Dh5Compat.EffectExpiration(nameItem.Value);
						int nameRemaining = (nameExpiry.HasValue ? (int)Math.Max(0.0, (nameExpiry.Value - DateTime.UtcNow).TotalSeconds) : (int)Math.Max(0f, nameDuration));
						return new BuffRemaining
						{
							IsActive = true,
							RemainingSeconds = nameRemaining
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
			IEntity entity = _currentEntity;
			if (entity == null)
			{
				return false;
			}
			IProperty property = entity.GetProperty(stance.PropertyDid);
			if (property == null)
			{
				return false;
			}
			string text = property?.ToString();
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
				IEntity entity = _currentEntity;
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
public class BuffEffectState
{
	public int EffectId { get; set; }

	public int EffectDid { get; set; }

	public float Duration { get; set; }

	public bool IsPaused { get; set; }

	public DateTime ApplicationTimeUtc { get; set; }

	public DateTime LastUpdateUtc { get; set; }
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
public struct HudToggleState
{
	public string Name;

	public bool Active;
}

// DH5-PORT: ImGui-based HUD (replaces HudOverlayForm / WinForms)
public class HudImGui
{
	private readonly RotationHelperPlugin _plugin;

	public HudImGui(RotationHelperPlugin plugin)
	{
		_plugin = plugin;
	}

	public void Render()
	{
		RotationConfig config = _plugin._config;
		if (config == null || !config.HudEnabled)
		{
			return;
		}
		try { System.IO.File.AppendAllText(System.IO.Path.Combine(RotationHelperPlugin.AppDataDir, "hudtrace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] Hud.Render: showBuffs={config.HudShowBuffs} showCooldowns={config.HudShowCooldowns} showToggles={config.HudShowToggles} showTarget={config.HudShowTarget}\r\n"); } catch {}
		ImGuiNET.ImGui.SetNextWindowPos(new System.Numerics.Vector2(config.HudX, config.HudY), ImGuiNET.ImGuiCond.FirstUseEver);
		ImGuiNET.ImGui.SetNextWindowBgAlpha(0.65f);
		ImGuiNET.ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(230f, 40f), new System.Numerics.Vector2(400f, 600f));
		ImGuiNET.ImGui.Begin($"Rotation Helper HUD##{RotationHelperPlugin.PluginGuid}", ImGuiNET.ImGuiWindowFlags.AlwaysAutoResize | ImGuiNET.ImGuiWindowFlags.NoCollapse);
		config.HudX = (int)ImGuiNET.ImGui.GetWindowPos().X;
		config.HudY = (int)ImGuiNET.ImGui.GetWindowPos().Y;
		if (config.HudShowPaused && _plugin.IsPaused)
		{
			ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f), "PAUSED");
			ImGuiNET.ImGui.Separator();
		}
		if (config.HudShowTarget)
		{
			RenderTarget();
		}
		if (config.HudShowToggles)
		{
			RenderToggles();
		}
		if (config.HudShowCooldowns)
		{
			RenderCooldowns();
		}
		if (config.HudShowBuffs)
		{
			RenderBuffs();
		}
		ImGuiNET.ImGui.End();
	}

	private void RenderTarget()
	{
		IEntity selectedObject = _plugin._selectedObject;
		IEntity currentEntity = _plugin._currentEntity;
		if (selectedObject != null && currentEntity != null && selectedObject.InstanceId != 0 && selectedObject.InstanceId != currentEntity.InstanceId)
		{
			double? distance = _plugin.GetDistanceToTarget();
			string text = distance.HasValue ? $"{distance.Value:F1}u" : "--";
			ImGuiNET.ImGui.Text($"Target: {selectedObject.Name ?? "Unknown"} ({text})");
		}
		else
		{
			ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f), "Target: none");
		}
	}

	private void RenderToggles()
	{
		HudToggleState[] toggleStates = _plugin.GetToggleHudStates();
		if (toggleStates == null || toggleStates.Length == 0)
		{
			return;
		}
		ImGuiNET.ImGui.Separator();
		HudToggleState[] array = toggleStates;
		foreach (HudToggleState toggleState in array)
		{
			System.Numerics.Vector4 color = toggleState.Active ? new System.Numerics.Vector4(0.4f, 0.86f, 0.4f, 1f) : new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f);
			ImGuiNET.ImGui.TextColored(color, $"{(toggleState.Active ? "[ON] " : "[--] ")}{toggleState.Name}");
		}
	}

	private void RenderCooldowns()
	{
		var rotationEntries = new System.Collections.Generic.List<RotationAbility>();
		foreach (RotationAbility item in _plugin.Rotation)
		{
			if (item.Autocast && item.CooldownMs > 0)
			{
				long remainingCooldownMs = _plugin.GetRemainingCooldownMs(item);
				if (remainingCooldownMs > 0)
				{
					rotationEntries.Add(item);
				}
			}
		}
		if (rotationEntries.Count == 0)
		{
			return;
		}
		ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(0.55f, 0.75f, 1f, 1f), "Rotation / Ability Timers");
		ImGuiNET.ImGui.Separator();
		foreach (RotationAbility item in rotationEntries)
		{
			long remainingCooldownMs = _plugin.GetRemainingCooldownMs(item);
			float progress = (float)((double)remainingCooldownMs / (double)item.CooldownMs);
			string countdown = $"{remainingCooldownMs / 1000.0:F1}s";
			System.Numerics.Vector4 rowBg = new System.Numerics.Vector4(0.05f, 0.10f, 0.20f, 0.6f);
			System.Numerics.Vector4 barColor = new System.Numerics.Vector4(0.20f, 0.50f, 0.95f, 1f);
			System.Numerics.Vector4 countdownColor = new System.Numerics.Vector4(0.55f, 0.75f, 1f, 1f);
			ImGuiNET.ImGui.PushStyleColor(ImGuiNET.ImGuiCol.FrameBg, rowBg);
			ImGuiNET.ImGui.PushStyleColor(ImGuiNET.ImGuiCol.PlotHistogram, barColor);
			ImGuiNET.ImGui.PushStyleColor(ImGuiNET.ImGuiCol.Text, new System.Numerics.Vector4(1f, 1f, 1f, 1f));
			float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
			float cdWidth = 42f;
			float barWidth = avail - cdWidth - 4f;
			if (barWidth < 40f) barWidth = 40f;
			ImGuiNET.ImGui.ProgressBar(progress, new System.Numerics.Vector2(barWidth, 14f), item.Name ?? "");
			ImGuiNET.ImGui.PopStyleColor(3);
			ImGuiNET.ImGui.SameLine(0, 4);
			ImGuiNET.ImGui.TextColored(countdownColor, countdown);
		}
	}

private static string FormatTime(int seconds)
	{
		if (seconds < 0)
		{
			seconds = 0;
		}
		if (seconds < 60)
		{
			return $"{seconds}s";
		}
		int minutes = seconds / 60;
		int remainingSeconds = seconds % 60;
		return $"{minutes}m {remainingSeconds:D2}s";
	}

	private void RenderBuffs()
	{
		BuffManager buffManager = _plugin.BuffManager;
		if (buffManager == null)
		{
			_plugin.Log("HUD[Buffs] buffManager=null");
			return;
		}
		var activeBuffs = new System.Collections.Generic.List<System.Tuple<RotationAbility, BuffRemaining>>();
		int bfTotal = 0;
		foreach (RotationAbility item in _plugin.BuffOrder)
		{
			if (item.ReferenceDid.GetValueOrDefault() == 0)
			{
				continue;
			}
			bfTotal++;
			uint cachedEffect = _plugin.GameCache.GetCachedEffectDid(item.ReferenceDid.Value);
			AutoBuffConfig autoBuffConfig = new AutoBuffConfig
			{
				Name = item.Name,
				ActionDid = item.ReferenceDid.Value,
				EffectDid = cachedEffect != 0 ? (uint?)cachedEffect : null
			};
			BuffRemaining buffRemaining = buffManager.GetBuffRemaining(autoBuffConfig);
			if (!buffRemaining.IsActive)
			{
				continue;
			}
			activeBuffs.Add(System.Tuple.Create(item, buffRemaining));
		}
		if (activeBuffs.Count == 0)
		{
			_plugin.Log($"HUD[Buffs] total={bfTotal} active=0");
			return;
		}
		ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(0.55f, 0.95f, 0.65f, 1f), "Active Buffs");
		ImGuiNET.ImGui.Separator();
		foreach (var pair in activeBuffs)
		{
			RotationAbility item = pair.Item1;
			BuffRemaining buffRemaining = pair.Item2;
			System.Numerics.Vector4 rowBg = new System.Numerics.Vector4(0.08f, 0.16f, 0.10f, 0.6f);
			System.Numerics.Vector4 barColor = new System.Numerics.Vector4(0.20f, 0.75f, 0.35f, 1f);
			System.Numerics.Vector4 countdownColor = new System.Numerics.Vector4(0.55f, 0.95f, 0.65f, 1f);
			if (buffRemaining.RemainingSeconds == int.MaxValue)
			{
				ImGuiNET.ImGui.TextColored(countdownColor, item.Name ?? "");
				continue;
			}
			int num = (int)((item.BuffDurationMs > 0) ? (item.BuffDurationMs / 1000) : 0);
			if (num <= 0)
			{
				ImGuiNET.ImGui.TextColored(countdownColor, $"{item.Name}  {FormatTime(buffRemaining.RemainingSeconds)}");
				continue;
			}
			float progress = (float)((double)buffRemaining.RemainingSeconds / (double)num);
			ImGuiNET.ImGui.PushStyleColor(ImGuiNET.ImGuiCol.FrameBg, rowBg);
			ImGuiNET.ImGui.PushStyleColor(ImGuiNET.ImGuiCol.PlotHistogram, barColor);
			ImGuiNET.ImGui.PushStyleColor(ImGuiNET.ImGuiCol.Text, new System.Numerics.Vector4(1f, 1f, 1f, 1f));
			float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
			float cdWidth = 52f;
			float barWidth = avail - cdWidth - 4f;
			if (barWidth < 40f) barWidth = 40f;
			ImGuiNET.ImGui.ProgressBar(progress, new System.Numerics.Vector2(barWidth, 14f), item.Name ?? "");
			ImGuiNET.ImGui.PopStyleColor(3);
			ImGuiNET.ImGui.SameLine(0, 4);
			ImGuiNET.ImGui.TextColored(countdownColor, FormatTime(buffRemaining.RemainingSeconds));
		}
		_plugin.Log($"HUD[Buffs] total={bfTotal} active={activeBuffs.Count}");
	}

}

public class ImGuiConfigWindow
{
	private readonly RotationHelperPlugin _plugin;

	private string _search = "";

	private readonly List<HotbarAbility> _hotbarAbilities = new List<HotbarAbility>();

	private bool _manualCdMode;

	private int _buffSelectedIndex = -1;

	private string _statusMessage = "";

	private int _editToggleIndex = -1;

	private string _editToggleDid = "";

	private string _editToggleExpected = "";

	private string _loadProfileSelection = "";

	private List<string> _loadProfiles = new List<string>();

	private bool _scannedOnOpen;

	private bool _closeRequested;

	private static readonly string[] KeyOptions = new string[56]
	{
		"None", "L.Mouse", "R.Mouse", "Cancel", "M.Mouse", "X1.Mouse", "X2.Mouse", "Backspace", "Tab", "Enter",
		"Shift", "Ctrl", "Alt", "Pause", "CapsLock", "Esc", "Space", "PageUp", "PageDown", "End",
		"Home", "Left", "Up", "Right", "Down", "PrtScn", "Insert", "Delete", "D0", "D1",
		"D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "A", "B",
		"C", "D", "E", "F", "G", "H", "I", "J", "K", "L",
		"M", "N", "O", "P", "Q", "R"
	};

	public ImGuiConfigWindow(RotationHelperPlugin plugin)
	{
		_plugin = plugin;
	}

	public void Render()
	{
		RotationConfig config = _plugin._config;
		if (config == null)
		{
			return;
		}
		ImGuiNET.ImGui.SetNextWindowSize(new System.Numerics.Vector2(920f, 640f), ImGuiNET.ImGuiCond.FirstUseEver);
		bool open = _plugin._showConfigWindow;
		ImGuiNET.ImGui.SetNextWindowBgAlpha(0.95f);
		if (ImGuiNET.ImGui.Begin($"Rotation Helper - Setup##{RotationHelperPlugin.PluginGuid}", ref open, ImGuiNET.ImGuiWindowFlags.NoCollapse))
		{
			if (!_scannedOnOpen)
			{
				_scannedOnOpen = true;
				ScanHotbarFromGame();
			}
			RenderStatus();
			if (ImGuiNET.ImGui.BeginTabBar("##tabs"))
			{
				if (ImGuiNET.ImGui.BeginTabItem("Rotation"))
				{
					RenderRotationTab();
					ImGuiNET.ImGui.EndTabItem();
				}
				if (ImGuiNET.ImGui.BeginTabItem("Toggle Buffs"))
				{
					RenderToggleTab();
					ImGuiNET.ImGui.EndTabItem();
				}
				if (ImGuiNET.ImGui.BeginTabItem("Buffs"))
				{
					RenderBuffsTab();
					ImGuiNET.ImGui.EndTabItem();
				}
				if (ImGuiNET.ImGui.BeginTabItem("Emergency"))
				{
					RenderEmergencyTab();
					ImGuiNET.ImGui.EndTabItem();
				}
				if (ImGuiNET.ImGui.BeginTabItem("HUD"))
				{
					RenderHudTab();
					ImGuiNET.ImGui.EndTabItem();
				}
				if (ImGuiNET.ImGui.BeginTabItem("Settings"))
				{
					RenderSettingsTab();
					ImGuiNET.ImGui.EndTabItem();
				}
				ImGuiNET.ImGui.EndTabBar();
			}
		}
		ImGuiNET.ImGui.End();
		if (_closeRequested)
		{
			_closeRequested = false;
			open = false;
		}
		_plugin._showConfigWindow = open;
		_plugin.WindowVisible = open;
		if (!open)
		{
			_scannedOnOpen = false;
		}
		RenderToggleEditPopup();
	}

	private void RenderStatus()
	{
		string text = ((_plugin.IsRunning && _plugin.BuffIsRunning) ? "Status: ROTATION + BUFFS RUNNING" : (_plugin.IsRunning ? "Status: ROTATION RUNNING" : (_plugin.BuffIsRunning ? "Status: BUFFS RUNNING" : "Status: Idle")));
		System.Numerics.Vector4 color = (_plugin.IsRunning || _plugin.BuffIsRunning) ? new System.Numerics.Vector4(1f, 0.78f, 0.2f, 1f) : new System.Numerics.Vector4(0.6f, 1f, 0.6f, 1f);
		ImGuiNET.ImGui.TextColored(color, text);
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Save & Close"))
		{
			SaveAll();
			_closeRequested = true;
		}
		if (!string.IsNullOrEmpty(_statusMessage))
		{
			ImGuiNET.ImGui.SameLine();
			ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.8f, 1f, 1f), _statusMessage);
		}
		ImGuiNET.ImGui.Separator();
	}

	private void RenderLeftPanel(float height)
	{
		if (!ImGuiNET.ImGui.BeginChild("##left", new System.Numerics.Vector2(280f, height), ImGuiNET.ImGuiChildFlags.Borders))
		{
			ImGuiNET.ImGui.EndChild();
			return;
		}
		ImGuiNET.ImGui.Text("Search:");
		ImGuiNET.ImGui.SetNextItemWidth(-1f);
		ImGuiNET.ImGui.InputText("##search", ref _search, 128);
		ImGuiNET.ImGui.Text("Abilities (Ctrl+click multi):");
		if (ImGuiNET.ImGui.BeginChild("##list", new System.Numerics.Vector2(-1f, height - 130f), ImGuiNET.ImGuiChildFlags.Borders))
		{
			string text = (_search ?? "").Trim();
			foreach (HotbarAbility item in _hotbarAbilities)
			{
				if (string.IsNullOrEmpty(text) || (item.Name ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					bool selected = _abilitySelection.Contains(item);
					float listItemWidth = ImGuiNET.ImGui.GetContentRegionAvail().X;
					if (ImGuiNET.ImGui.Selectable(item.Display, selected, ImGuiNET.ImGuiSelectableFlags.AllowDoubleClick))
					{
						if (ImGuiNET.ImGui.GetIO().KeyCtrl)
						{
							if (selected)
							{
								_abilitySelection.Remove(item);
							}
							else
							{
								_abilitySelection.Add(item);
							}
						}
						else
						{
							_abilitySelection.Clear();
							_abilitySelection.Add(item);
						}
					}
					ShowTruncatedTooltip(item.Display, listItemWidth);
				}
			}
		}
		ImGuiNET.ImGui.EndChild();
		if (ImGuiNET.ImGui.Button("Add -> Rotation"))
		{
			AddSelectedAbilities();
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Add -> Toggle"))
		{
			AddToggleSelection();
		}
		if (ImGuiNET.ImGui.Button("Add -> Buffs"))
		{
			AddBuffSelection();
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Add -> Emergency"))
		{
			AddEmergencySelection();
		}
		if (ImGuiNET.ImGui.Button("Scan Hotbar"))
		{
			ScanHotbarFromGame();
		}
		ImGuiNET.ImGui.EndChild();
	}

	private readonly List<HotbarAbility> _abilitySelection = new List<HotbarAbility>();

	private int _rotSelectedIndex = -1;

	private void RenderRotationTab()
	{
		float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
		RenderLeftPanel(ImGuiNET.ImGui.GetContentRegionAvail().Y);
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.BeginChild("##rotright", new System.Numerics.Vector2(avail - 290f, 0f)))
		{
			RotationConfig config = _plugin._config;
			ImGuiNET.ImGui.Text("Controls");
			if (ImGuiNET.ImGui.Button("Auto All"))
			{
				SetAllRotationAutocast(true);
			}
			ImGuiNET.ImGui.SameLine();
			if (ImGuiNET.ImGui.Button("Disable All"))
			{
				SetAllRotationAutocast(false);
			}
			ImGuiNET.ImGui.SameLine();
			if (ImGuiNET.ImGui.Button("Auto CD"))
			{
				_manualCdMode = false;
			}
			ImGuiNET.ImGui.SameLine();
			if (ImGuiNET.ImGui.Button("Manual CD"))
			{
				_manualCdMode = true;
			}
			bool onTarget = config.AutocastRotationOnTarget;
			if (ImGuiNET.ImGui.Checkbox("Enemy target only", ref onTarget))
			{
				config.AutocastRotationOnTarget = onTarget;
				_plugin.SaveConfig();
			}
			bool melee = config.MeleeRangeEnabled;
			if (ImGuiNET.ImGui.Checkbox("Melee range check", ref melee))
			{
				config.MeleeRangeEnabled = melee;
				_plugin.SaveConfig();
			}
			bool furthest = config.AutoTargetFurthestEnemy;
			if (ImGuiNET.ImGui.Checkbox("Auto target furthest", ref furthest))
			{
				config.AutoTargetFurthestEnemy = furthest;
				_plugin.SaveConfig();
			}
			bool closest = config.AutoTargetClosestEnemy;
			if (ImGuiNET.ImGui.Checkbox("Auto target closest enemy", ref closest))
			{
				config.AutoTargetClosestEnemy = closest;
				_plugin.SaveConfig();
			}
			float range = config.AutoTargetMaxRange;
			ImGuiNET.ImGui.SetNextItemWidth(120f);
			if (ImGuiNET.ImGui.InputFloat("Max Range", ref range))
			{
				config.AutoTargetMaxRange = range;
				_plugin.SaveConfig();
			}
			bool smart = config.SmartOrder;
			if (ImGuiNET.ImGui.Checkbox("Smart Order", ref smart))
			{
				config.SmartOrder = smart;
				_plugin.SaveConfig();
			}
			double? targetDistance = _plugin.GetTargetDistance();
			ImGuiNET.ImGui.Text($"Target dist: {(targetDistance.HasValue ? $"{targetDistance.Value:F1}" : "--")}");
			ImGuiNET.ImGui.Separator();
			ImGuiNET.ImGui.Text("Rotation Abilities");
			RenderRotationTable();
			ImGuiNET.ImGui.Separator();
			if (ImGuiNET.ImGui.Button("Remove"))
			{
				RemoveSelectedRotation();
			}
			ImGuiNET.ImGui.SameLine();
			if (ImGuiNET.ImGui.Button("Up"))
			{
				MoveRotation(-1);
			}
			ImGuiNET.ImGui.SameLine();
			if (ImGuiNET.ImGui.Button("Down"))
			{
				MoveRotation(1);
			}
			ImGuiNET.ImGui.SameLine();
			if (ImGuiNET.ImGui.Button("Remove All"))
			{
				_plugin.Rotation.Clear();
				_rotSelectedIndex = -1;
				_plugin.SaveRotation();
			}
		}
		ImGuiNET.ImGui.EndChild();
	}

	private static bool IsTextTruncated(string text, float availableWidth)
	{
		if (string.IsNullOrEmpty(text) || availableWidth <= 0f)
		{
			return false;
		}
		ImGuiNET.ImGuiStylePtr style = ImGuiNET.ImGui.GetStyle();
		float usable = availableWidth - (style.CellPadding.X * 2f) - 2f;
		if (usable <= 0f)
		{
			return true;
		}
		return ImGuiNET.ImGui.CalcTextSize(text).X > usable;
	}

	private static void ShowTruncatedTooltip(string text, float availableWidth)
	{
		if (!IsTextTruncated(text, availableWidth))
		{
			return;
		}
		if (ImGuiNET.ImGui.IsItemHovered())
		{
			ImGuiNET.ImGui.SetTooltip(text);
		}
	}

	private void RenderRotationTable()
	{
		int removeAt = -1;
		bool manualCd = _manualCdMode;
		if (ImGuiNET.ImGui.BeginTable("##rottable", 7,
			ImGuiNET.ImGuiTableFlags.Borders |
			ImGuiNET.ImGuiTableFlags.RowBg |
			ImGuiNET.ImGuiTableFlags.ScrollY |
			ImGuiNET.ImGuiTableFlags.ScrollX |
			ImGuiNET.ImGuiTableFlags.Resizable |
			ImGuiNET.ImGuiTableFlags.SizingStretchProp,
			new System.Numerics.Vector2(0f, 260f)))
		{
			ImGuiNET.ImGui.TableSetupColumn("Auto", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 46f);
			ImGuiNET.ImGui.TableSetupColumn("Ability", ImGuiNET.ImGuiTableColumnFlags.WidthStretch, 220f);
			ImGuiNET.ImGui.TableSetupColumn("CD(s)", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 70f);
			ImGuiNET.ImGui.TableSetupColumn("Near", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 60f);
			ImGuiNET.ImGui.TableSetupColumn("HP%", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 60f);
			ImGuiNET.ImGui.TableSetupColumn("SP%", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 60f);
			ImGuiNET.ImGui.TableSetupColumn("Prio", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 58f);
			ImGuiNET.ImGui.TableHeadersRow();
			for (int i = 0; i < _plugin.Rotation.Count; i++)
			{
				RotationAbility ability = _plugin.Rotation[i];
				ImGuiNET.ImGui.TableNextRow();
				ImGuiNET.ImGui.TableNextColumn();
				ImGuiNET.ImGui.PushID(i);
				bool autocast = ability.Autocast;
				if (ImGuiNET.ImGui.Checkbox("##auto", ref autocast))
				{
					ability.Autocast = autocast;
					_plugin.SaveRotation();
				}
				ImGuiNET.ImGui.TableNextColumn();
				bool selected = (i == _rotSelectedIndex);
				string abilityName = string.IsNullOrEmpty(ability.Name) ? "(unnamed)" : ability.Name;
				System.Numerics.Vector2 cellMin = ImGuiNET.ImGui.GetCursorScreenPos();
				float cellWidth = ImGuiNET.ImGui.GetColumnWidth();
				if (ImGuiNET.ImGui.Selectable($"{abilityName}##sel", selected, ImGuiNET.ImGuiSelectableFlags.SpanAllColumns | ImGuiNET.ImGuiSelectableFlags.AllowOverlap))
				{
					_rotSelectedIndex = i;
				}
				if (ImGuiNET.ImGui.IsItemHovered())
				{
					System.Numerics.Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
					bool overAbilityColumn = (mouse.X >= cellMin.X) && (mouse.X <= (cellMin.X + cellWidth));
					if (overAbilityColumn && IsTextTruncated(abilityName, cellWidth))
					{
						ImGuiNET.ImGui.SetTooltip($"[{ability.KeyLabel}] {abilityName}");
					}
				}
				ImGuiNET.ImGui.TableNextColumn();
				if (manualCd)
				{
					double cdSec = (ability.CooldownMs > 0) ? ((double)ability.CooldownMs / 1000.0) : 0.0;
					ImGuiNET.ImGui.SetNextItemWidth(-1f);
					if (ImGuiNET.ImGui.InputDouble("##cd", ref cdSec))
					{
						ability.CooldownMs = (long)Math.Max(0.0, cdSec * 1000.0);
						_plugin.SaveRotation();
					}
				}
				else
				{
					ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f), "Auto");
				}
				ImGuiNET.ImGui.TableNextColumn();
				int near = ability.MinNearbyEnemies;
				ImGuiNET.ImGui.SetNextItemWidth(-1f);
				if (ImGuiNET.ImGui.InputInt("##near", ref near))
				{
					ability.MinNearbyEnemies = Math.Max(0, near);
					_plugin.SaveRotation();
				}
				ImGuiNET.ImGui.TableNextColumn();
				int hp = ability.TargetHpThreshold;
				ImGuiNET.ImGui.SetNextItemWidth(-1f);
				if (ImGuiNET.ImGui.InputInt("##hp", ref hp))
				{
					ability.TargetHpThreshold = Math.Clamp(hp, 0, 100);
					_plugin.SaveRotation();
				}
				ImGuiNET.ImGui.TableNextColumn();
				int sp = ability.MinSpThreshold;
				ImGuiNET.ImGui.SetNextItemWidth(-1f);
				if (ImGuiNET.ImGui.InputInt("##sp", ref sp))
				{
					ability.MinSpThreshold = Math.Clamp(sp, 0, 100);
					_plugin.SaveRotation();
				}
				ImGuiNET.ImGui.TableNextColumn();
				int prio = ability.Priority;
				ImGuiNET.ImGui.SetNextItemWidth(-1f);
				if (ImGuiNET.ImGui.InputInt("##prio", ref prio))
				{
					ability.Priority = Math.Max(0, prio);
					_plugin.SaveRotation();
				}
				ImGuiNET.ImGui.PopID();
			}
			ImGuiNET.ImGui.EndTable();
		}
		_ = removeAt;
	}

	private void RenderToggleTab()
	{
		RotationConfig config = _plugin._config;
		ImGuiNET.ImGui.Text("Toggle Abilities");
		ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f), "Add toggles from the ability list via 'Add -> Toggle' on the Rotation tab.");
		if (ImGuiNET.ImGui.Button("Enable All"))
		{
			foreach (RotationAbility item in _plugin.ToggleBuffs)
			{
				item.Autocast = true;
			}
			_plugin.SaveToggleBuffs();
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Disable All"))
		{
			foreach (RotationAbility item2 in _plugin.ToggleBuffs)
			{
				item2.Autocast = false;
			}
			_plugin.SaveToggleBuffs();
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Detect All RefDids"))
		{
			_plugin.DiscoverTogglePropertyForAll();
			_plugin.SaveToggleBuffs();
		}
		ImGuiNET.ImGui.Separator();
		if (ImGuiNET.ImGui.BeginChild("##toglist", new System.Numerics.Vector2(0f, 0f), ImGuiNET.ImGuiChildFlags.Borders))
		{
			for (int i = 0; i < _plugin.ToggleBuffs.Count; i++)
			{
				RotationAbility ability = _plugin.ToggleBuffs[i];
				ImGuiNET.ImGui.PushID(i);
				bool autocast = ability.Autocast;
				if (ImGuiNET.ImGui.Checkbox("##on", ref autocast))
				{
					ability.Autocast = autocast;
					_plugin.SaveToggleBuffs();
				}
				ImGuiNET.ImGui.SameLine();
				string label = (ability.ToggleStatePropertyDid != 0) ? $"{ability.Name} [0x{ability.ToggleStatePropertyDid:X8}]" : ability.Name;
				ImGuiNET.ImGui.Text(label);
				ImGuiNET.ImGui.SameLine();
				if (ImGuiNET.ImGui.SmallButton("X"))
				{
					_plugin.ToggleBuffs.RemoveAt(i);
					_plugin.SaveToggleBuffs();
					ImGuiNET.ImGui.PopID();
					break;
				}
				ImGuiNET.ImGui.SameLine();
				if (ImGuiNET.ImGui.SmallButton("Up"))
				{
					MoveToggleAt(i, -1);
				}
				ImGuiNET.ImGui.SameLine();
				if (ImGuiNET.ImGui.SmallButton("Down"))
				{
					MoveToggleAt(i, 1);
				}
				ImGuiNET.ImGui.SameLine();
				if (ImGuiNET.ImGui.SmallButton("Edit"))
				{
					_editToggleIndex = i;
					_editToggleDid = ((ability.ToggleStatePropertyDid == 0) ? "" : $"0x{ability.ToggleStatePropertyDid:X8}");
					_editToggleExpected = ability.ToggleExpectedValue ?? "1";
					ImGuiNET.ImGui.OpenPopup("##edittoggle");
				}
				ImGuiNET.ImGui.PopID();
			}
		}
		ImGuiNET.ImGui.EndChild();
	}

	private void RenderToggleEditPopup()
	{
		if (ImGuiNET.ImGui.BeginPopup("##edittoggle"))
		{
			ImGuiNET.ImGui.Text("Toggle state property");
			ImGuiNET.ImGui.SetNextItemWidth(220f);
			ImGuiNET.ImGui.InputText("Property DID (0x...)", ref _editToggleDid, 32);
			ImGuiNET.ImGui.SetNextItemWidth(220f);
			ImGuiNET.ImGui.InputText("Expected when active", ref _editToggleExpected, 32);
			if (ImGuiNET.ImGui.Button("OK"))
			{
				if (_editToggleIndex >= 0 && _editToggleIndex < _plugin.ToggleBuffs.Count)
				{
					RotationAbility ability = _plugin.ToggleBuffs[_editToggleIndex];
					string text = (_editToggleDid ?? "").Trim();
					if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
					{
						text = text.Substring(2);
					}
					ability.ToggleStatePropertyDid = (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result) ? result : 0u);
					ability.ToggleExpectedValue = _editToggleExpected;
					_plugin.SaveToggleBuffs();
				}
				ImGuiNET.ImGui.CloseCurrentPopup();
			}
			ImGuiNET.ImGui.SameLine();
			if (ImGuiNET.ImGui.Button("Cancel"))
			{
				ImGuiNET.ImGui.CloseCurrentPopup();
			}
			ImGuiNET.ImGui.EndPopup();
		}
	}

	private void RenderBuffsTab()
	{
		RotationConfig config = _plugin._config;
		if (ImGuiNET.ImGui.Button("+ Add Buff"))
		{
			_plugin.BuffOrder.Add(new RotationAbility
			{
				Name = $"New Buff {_plugin.BuffOrder.Count + 1}",
				BuffOnMissing = true,
				Autocast = true
			});
			_plugin.SaveBuffOrder();
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Manual All"))
		{
			SetAllBuffAutocast(true);
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Missing All"))
		{
			foreach (RotationAbility item in _plugin.BuffOrder)
			{
				item.BuffOnMissing = true;
				item.Autocast = false;
			}
			_plugin.SaveBuffOrder();
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Clear All"))
		{
			foreach (RotationAbility item2 in _plugin.BuffOrder)
			{
				item2.Autocast = false;
				item2.BuffOnMissing = false;
				item2.DurationMinutes = 0;
				item2.DurationSeconds = 0;
			}
			_plugin.SaveBuffOrder();
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Remove All"))
		{
			_plugin.BuffOrder.Clear();
			_plugin.SaveBuffOrder();
		}
		bool wild = config.BuffDisableWilderness;
		if (ImGuiNET.ImGui.Checkbox("Disable in wilderness", ref wild))
		{
			config.BuffDisableWilderness = wild;
			_plugin.SaveConfig();
		}
		ImGuiNET.ImGui.SameLine();
		bool pub = config.BuffDisablePublic;
		if (ImGuiNET.ImGui.Checkbox("Disable in public areas", ref pub))
		{
			config.BuffDisablePublic = pub;
			_plugin.SaveConfig();
		}
		ImGuiNET.ImGui.Separator();
		if (ImGuiNET.ImGui.BeginChild("##bufflist", new System.Numerics.Vector2(0f, 0f), ImGuiNET.ImGuiChildFlags.Borders))
		{
			if (_plugin.BuffOrder.Count == 0)
			{
				ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f), "No buffs configured. Click '+ Add Buff' above to add one.");
			}
			int removeAt = -1;
			for (int i = 0; i < _plugin.BuffOrder.Count; i++)
			{
				RotationAbility ability = _plugin.BuffOrder[i];
				ImGuiNET.ImGui.PushID(i);
				if (ImGuiNET.ImGui.CollapsingHeader($"#{i + 1}: {ability.Name}", ImGuiNET.ImGuiTreeNodeFlags.DefaultOpen))
				{
					if (ImGuiNET.ImGui.RadioButton("Automatic (cast when missing or expires)", ability.BuffOnMissing))
					{
						ability.BuffOnMissing = true;
						_plugin.SaveBuffOrder();
					}
					if (ImGuiNET.ImGui.RadioButton("Manual Timer", !ability.BuffOnMissing))
					{
						ability.BuffOnMissing = false;
						_plugin.SaveBuffOrder();
					}
					int mins = ability.DurationMinutes;
					ImGuiNET.ImGui.SetNextItemWidth(80f);
					if (ImGuiNET.ImGui.InputInt("Minutes", ref mins))
					{
						ability.DurationMinutes = Math.Clamp(mins, 0, 60);
						_plugin.SaveBuffOrder();
					}
					ImGuiNET.ImGui.SameLine();
					int secs = ability.DurationSeconds;
					ImGuiNET.ImGui.SetNextItemWidth(80f);
					if (ImGuiNET.ImGui.InputInt("Seconds", ref secs))
					{
						ability.DurationSeconds = Math.Clamp(secs, 0, 59);
						_plugin.SaveBuffOrder();
					}
					bool requires = ability.RequiresTarget;
					if (ImGuiNET.ImGui.Checkbox("Needs target before casting", ref requires))
					{
						ability.RequiresTarget = requires;
						if (!requires)
						{
							ability.TargetType = "Myself";
						}
						_plugin.SaveBuffOrder();
					}
					if (ability.RequiresTarget)
					{
						string targetType = ability.TargetType ?? "Myself";
						ImGuiNET.ImGui.SetNextItemWidth(160f);
						if (ImGuiNET.ImGui.BeginCombo("Target", targetType))
						{
							foreach (string option in new string[3] { "Myself", "A Party Member", "Both" })
							{
								if (ImGuiNET.ImGui.Selectable(option, option == targetType))
								{
									ability.TargetType = option;
									_plugin.SaveBuffOrder();
								}
							}
							ImGuiNET.ImGui.EndCombo();
						}
					}
					bool active = ability.Autocast;
					if (ImGuiNET.ImGui.Checkbox("Active", ref active))
					{
						ability.Autocast = active;
						ability.BuffOnMissing = active && ability.BuffOnMissing;
						_plugin.SaveBuffOrder();
					}
					if (ImGuiNET.ImGui.SmallButton("Remove"))
					{
						removeAt = i;
					}
					ImGuiNET.ImGui.SameLine();
					if (ImGuiNET.ImGui.SmallButton("Up"))
					{
						MoveBuffAt(i, -1);
					}
					ImGuiNET.ImGui.SameLine();
					if (ImGuiNET.ImGui.SmallButton("Down"))
					{
						MoveBuffAt(i, 1);
					}
				}
				ImGuiNET.ImGui.PopID();
			}
			if (removeAt >= 0)
			{
				_plugin.BuffOrder.RemoveAt(removeAt);
				_plugin.SaveBuffOrder();
			}
		}
		ImGuiNET.ImGui.EndChild();
	}

	private void RenderEmergencyTab()
	{
		RotationConfig config = _plugin._config;
		bool partyHeal = config.EmergencyPartyHeal;
		if (ImGuiNET.ImGui.Checkbox("Heal party members", ref partyHeal))
		{
			config.EmergencyPartyHeal = partyHeal;
			_plugin.SaveConfig();
		}
		float healRange = (float)config.HealRange;
		ImGuiNET.ImGui.SetNextItemWidth(120f);
		if (ImGuiNET.ImGui.InputFloat("Range (0=off)", ref healRange))
		{
			config.HealRange = healRange;
			_plugin.SaveConfig();
		}
		ImGuiNET.ImGui.Separator();
		if (ImGuiNET.ImGui.BeginTable("##emtable", 7,
			ImGuiNET.ImGuiTableFlags.Borders |
			ImGuiNET.ImGuiTableFlags.RowBg |
			ImGuiNET.ImGuiTableFlags.ScrollY |
			ImGuiNET.ImGuiTableFlags.ScrollX |
			ImGuiNET.ImGuiTableFlags.Resizable |
			ImGuiNET.ImGuiTableFlags.SizingStretchProp,
			new System.Numerics.Vector2(0f, 320f)))
		{
			ImGuiNET.ImGui.TableSetupColumn("Enabled", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 56f);
			ImGuiNET.ImGui.TableSetupColumn("Self HP%", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 74f);
			ImGuiNET.ImGui.TableSetupColumn("Party HP%", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 80f);
			ImGuiNET.ImGui.TableSetupColumn("Min #", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 60f);
			ImGuiNET.ImGui.TableSetupColumn("Scope", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 70f);
			ImGuiNET.ImGui.TableSetupColumn("Ability", ImGuiNET.ImGuiTableColumnFlags.WidthStretch, 180f);
			ImGuiNET.ImGui.TableSetupColumn("Actions", ImGuiNET.ImGuiTableColumnFlags.WidthFixed, 110f);
			ImGuiNET.ImGui.TableHeadersRow();
			int removeAt = -1;
			for (int i = 0; i < _plugin.Emergency.Count; i++)
			{
				RotationAbility ability = _plugin.Emergency[i];
				ImGuiNET.ImGui.TableNextRow();
				ImGuiNET.ImGui.TableNextColumn();
				ImGuiNET.ImGui.PushID(i);
				bool enabled = ability.Autocast;
				if (ImGuiNET.ImGui.Checkbox("##en", ref enabled))
				{
					ability.Autocast = enabled;
					_plugin.SaveEmergency();
				}
				ImGuiNET.ImGui.TableNextColumn();
				int selfHp = ((ability.CooldownMs > 0) ? ((int)ability.CooldownMs) : 50);
				ImGuiNET.ImGui.SetNextItemWidth(-1f);
				if (ImGuiNET.ImGui.InputInt("##shp", ref selfHp))
				{
					ability.CooldownMs = Math.Clamp(selfHp, 1, 100);
					_plugin.SaveEmergency();
				}
				ImGuiNET.ImGui.TableNextColumn();
				int partyHp = ability.PartyHpThreshold;
				ImGuiNET.ImGui.SetNextItemWidth(-1f);
				if (!config.EmergencyPartyHeal)
				{
					ImGuiNET.ImGui.BeginDisabled();
				}
				if (ImGuiNET.ImGui.InputInt("##php", ref partyHp) && config.EmergencyPartyHeal)
				{
					ability.PartyHpThreshold = Math.Clamp((partyHp == 0) ? 0 : partyHp, 1, 100);
					if (partyHp == 0)
					{
						ability.PartyHpThreshold = 0;
					}
					_plugin.SaveEmergency();
				}
				if (!config.EmergencyPartyHeal)
				{
					ImGuiNET.ImGui.EndDisabled();
				}
				ImGuiNET.ImGui.TableNextColumn();
				int count = ability.PartyCountThreshold;
				ImGuiNET.ImGui.SetNextItemWidth(-1f);
				if (ImGuiNET.ImGui.InputInt("##cnt", ref count))
				{
					ability.PartyCountThreshold = Math.Max(0, count);
					_plugin.SaveEmergency();
				}
				ImGuiNET.ImGui.TableNextColumn();
				string scope = ((ability.HealScope == "Self" || ability.HealScope == "Party") ? ability.HealScope : "Both");
				if (ImGuiNET.ImGui.SmallButton(scope + "##scope"))
				{
					ability.HealScope = ((scope == "Self") ? "Party" : ((scope == "Party") ? "Both" : "Self"));
					_plugin.SaveEmergency();
				}
				ImGuiNET.ImGui.TableNextColumn();
				string emergencyName = string.IsNullOrEmpty(ability.Name) ? "(unnamed)" : ability.Name;
				float emergencyColWidth = ImGuiNET.ImGui.GetColumnWidth();
				ImGuiNET.ImGui.TextUnformatted(emergencyName);
				ShowTruncatedTooltip(emergencyName, emergencyColWidth);
				ImGuiNET.ImGui.TableNextColumn();
				if (ImGuiNET.ImGui.SmallButton("X##rem"))
				{
					removeAt = i;
				}
				ImGuiNET.ImGui.SameLine();
				if (ImGuiNET.ImGui.SmallButton("Up##em"))
				{
					MoveEmergencyItem(i, -1);
				}
				ImGuiNET.ImGui.SameLine();
				if (ImGuiNET.ImGui.SmallButton("Down##em"))
				{
					MoveEmergencyItem(i, 1);
				}
				ImGuiNET.ImGui.PopID();
			}
			ImGuiNET.ImGui.EndTable();
			if (removeAt >= 0)
			{
				_plugin.Emergency.RemoveAt(removeAt);
				_plugin.SaveEmergency();
			}
		}
	}

	private void RenderHudTab()
	{
		RotationConfig config = _plugin._config;
		bool enabled = config.HudEnabled;
		if (ImGuiNET.ImGui.Checkbox("Enable HUD Overlay", ref enabled))
		{
			config.HudEnabled = enabled;
			_plugin.SaveConfig();
		}
		ImGuiNET.ImGui.Text("Sections");
		RenderHudCheckbox("Show Target", config.HudShowTarget, delegate(bool v) { config.HudShowTarget = v; });
		RenderHudCheckbox("Show Cooldowns", config.HudShowCooldowns, delegate(bool v) { config.HudShowCooldowns = v; });
		RenderHudCheckbox("Show Toggles", config.HudShowToggles, delegate(bool v) { config.HudShowToggles = v; });
		RenderHudCheckbox("Show Buffs", config.HudShowBuffs, delegate(bool v) { config.HudShowBuffs = v; });
		RenderHudCheckbox("Show Paused Indicator", config.HudShowPaused, delegate(bool v) { config.HudShowPaused = v; });
	}

	private void RenderHudCheckbox(string label, bool value, Action<bool> apply)
	{
		bool flag = value;
		if (ImGuiNET.ImGui.Checkbox(label, ref flag))
		{
			apply(flag);
			_plugin.SaveConfig();
		}
	}

	private void RenderSettingsTab()
	{
		RotationConfig config = _plugin._config;
		ImGuiNET.ImGui.Text("Rotation Trigger");
		RenderKeyCombo("Trigger Key", config.TriggerKey, false, delegate(int v) { config.TriggerKey = v; });
		ImGuiNET.ImGui.SetNextItemWidth(120f);
		int delay = config.PressDelayMs;
		if (ImGuiNET.ImGui.InputInt("Between keys (ms)", ref delay))
		{
			config.PressDelayMs = Math.Clamp(delay, 50, 5000);
			_plugin.SaveConfig();
		}
		RenderKeyCombo("Trigger Key 2", config.TriggerKey2, true, delegate(int v) { config.TriggerKey2 = v; });
		ImGuiNET.ImGui.Text("Buff Trigger");
		RenderKeyCombo("Buff Trigger Key", config.BuffTriggerKey, false, delegate(int v) { config.BuffTriggerKey = v; });
		ImGuiNET.ImGui.SetNextItemWidth(120f);
		int buffDelay = config.BuffPressDelayMs;
		if (ImGuiNET.ImGui.InputInt("Delay (ms)", ref buffDelay))
		{
			config.BuffPressDelayMs = Math.Clamp(buffDelay, 50, 5000);
			_plugin.SaveConfig();
		}
		RenderKeyCombo("Pause Toggle Key", config.PauseToggleKey, true, delegate(int v) { config.PauseToggleKey = v; });
		bool autoAttack = config.AutoAttackEnabled;
		if (ImGuiNET.ImGui.Checkbox("Auto Attack (toggle on if not active)", ref autoAttack))
		{
			config.AutoAttackEnabled = autoAttack;
			_plugin.SaveConfig();
		}
		ImGuiNET.ImGui.Separator();
		ImGuiNET.ImGui.Text("Profile");
		if (ImGuiNET.ImGui.Button("Save Profile"))
		{
			_plugin.SaveProfileSnapshot();
			_statusMessage = "Profile saved for '" + _plugin.GetCharacterName() + "'";
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Revert to Saved"))
		{
			_statusMessage = _plugin.LoadProfileSnapshot() ? "Reverted to last saved profile" : "No saved snapshot to revert to";
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Reload Profile"))
		{
			ReloadProfile();
			_statusMessage = "Profile reloaded";
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Dump Properties"))
		{
			string text = Path.Combine(_plugin._pluginFolder, $"PropertyDump_{_plugin.GetCharacterName()}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
			try
			{
				_plugin.DumpCharacterProperties(text);
				_statusMessage = "Dumped to " + text;
			}
			catch (Exception ex)
			{
				_statusMessage = "Dump failed: " + ex.Message;
			}
		}
		RefreshLoadProfiles();
		ImGuiNET.ImGui.SetNextItemWidth(240f);
		if (ImGuiNET.ImGui.BeginCombo("Load Profile", string.IsNullOrEmpty(_loadProfileSelection) ? "(select)" : _loadProfileSelection))
		{
			foreach (string profile in _loadProfiles)
			{
				if (ImGuiNET.ImGui.Selectable(profile, profile == _loadProfileSelection))
				{
					_loadProfileSelection = profile;
				}
			}
			ImGuiNET.ImGui.EndCombo();
		}
		ImGuiNET.ImGui.SameLine();
		if (ImGuiNET.ImGui.Button("Load"))
		{
			LoadSelectedProfile();
		}
	}

	private void RenderKeyCombo(string label, int vk, bool allowNone, Action<int> apply)
	{
		string current = KeyNameForVk(vk);
		ImGuiNET.ImGui.SetNextItemWidth(160f);
		if (ImGuiNET.ImGui.BeginCombo(label, current))
		{
			if (allowNone && ImGuiNET.ImGui.Selectable("None", vk == 0))
			{
				apply(0);
				_plugin.SaveConfig();
			}
			foreach (string option in KeyOptions)
			{
				if (option == "None")
				{
					continue;
				}
				if (ImGuiNET.ImGui.Selectable(option, option == current))
				{
					apply(VkForKeyName(option));
					_plugin.SaveConfig();
				}
			}
			ImGuiNET.ImGui.EndCombo();
		}
	}

	private static string KeyNameForVk(int vk)
	{
		if (vk == 0)
		{
			return "None";
		}
		foreach (string option in KeyOptions)
		{
			if (VkForKeyName(option) == vk)
			{
				return option;
			}
		}
		return $"0x{vk:X2}";
	}

	private static int VkForKeyName(string name)
	{
		switch (name)
		{
		case "None":
			return 0;
		case "L.Mouse":
			return 1;
		case "R.Mouse":
			return 2;
		case "Cancel":
			return 3;
		case "M.Mouse":
			return 4;
		case "X1.Mouse":
			return 5;
		case "X2.Mouse":
			return 6;
		case "Backspace":
			return 8;
		case "Tab":
			return 9;
		case "Enter":
			return 13;
		case "Shift":
			return 16;
		case "Ctrl":
			return 17;
		case "Alt":
			return 18;
		case "Pause":
			return 19;
		case "CapsLock":
			return 20;
		case "Esc":
			return 27;
		case "Space":
			return 32;
		case "PageUp":
			return 33;
		case "PageDown":
			return 34;
		case "End":
			return 35;
		case "Home":
			return 36;
		case "Left":
			return 37;
		case "Up":
			return 38;
		case "Right":
			return 39;
		case "Down":
			return 40;
		case "PrtScn":
			return 44;
		case "Insert":
			return 45;
		case "Delete":
			return 46;
		default:
			if (name.Length == 2 && name[0] == 'D' && name[1] >= '0' && name[1] <= '9')
			{
				return 48 + (name[1] - 48);
			}
			if (name.Length == 1)
			{
				char c = name[0];
				if (c >= 'A' && c <= 'Z')
				{
					return 65 + (c - 65);
				}
			}
			return 0;
		}
	}

	private void SaveAll()
	{
		_plugin.SaveRotation();
		_plugin.SaveBuffOrder();
		_plugin.SaveEmergency();
		_plugin.SaveToggleBuffs();
		_plugin.SaveConfig();
	}

	private void ReloadProfile()
	{
		RotationConfig config = _plugin._config;
		config.CharacterName = _plugin.GetCharacterName();
		_plugin.LoadConfig();
		_plugin.LoadRotation();
		_plugin.LoadBuffOrder();
		_plugin.LoadEmergency();
		_plugin.LoadToggleBuffs();
	}

	private void RefreshLoadProfiles()
	{
		_loadProfiles.Clear();
		try
		{
			string accountDir = _plugin.GetAccountDir();
			if (Directory.Exists(accountDir))
			{
				foreach (string directory in Directory.GetDirectories(accountDir))
				{
					string name = Path.GetFileName(directory);
					if (Directory.GetFiles(directory, "*-rotation.json").Length != 0)
					{
						_loadProfiles.Add(name);
					}
				}
			}
			if (_loadProfiles.Count == 0)
			{
				string legacy = Path.Combine(_plugin._pluginFolder, "Character profiles");
				if (Directory.Exists(legacy))
				{
					foreach (string directory2 in Directory.GetDirectories(legacy))
					{
						_loadProfiles.Add(Path.GetFileName(directory2));
					}
				}
			}
			_loadProfiles.Sort();
		}
		catch
		{
		}
	}

	private void LoadSelectedProfile()
	{
		if (string.IsNullOrEmpty(_loadProfileSelection))
		{
			return;
		}
		try
		{
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				WriteIndented = true
			};
			string path = Path.Combine(_plugin.GetAccountDir(), _loadProfileSelection);
			if (Directory.Exists(path))
			{
				string[] rotationFiles = Directory.GetFiles(path, "*-rotation.json");
				if (rotationFiles.Length != 0)
				{
					string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(rotationFiles[0]);
					string stem = fileNameWithoutExtension.Substring(0, fileNameWithoutExtension.Length - "-rotation".Length);
					string text = Path.Combine(path, stem + "-rotation.json");
					_plugin.Rotation = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(text), options) ?? new List<RotationAbility>();
					string text2 = Path.Combine(path, stem + "-bufforder.json");
					if (File.Exists(text2))
					{
						_plugin.BuffOrder = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(text2), options) ?? new List<RotationAbility>();
					}
					string text3 = Path.Combine(path, stem + "-emergency.json");
					if (File.Exists(text3))
					{
						_plugin.Emergency = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(text3), options) ?? new List<RotationAbility>();
					}
					string text4 = Path.Combine(path, stem + "-toggles.json");
					if (File.Exists(text4))
					{
						_plugin.ToggleBuffs = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(text4), options) ?? new List<RotationAbility>();
					}
					string text5 = Path.Combine(path, stem + "-settings.json");
					if (File.Exists(text5))
					{
						RotationConfig rotationConfig = JsonSerializer.Deserialize<RotationConfig>(File.ReadAllText(text5), options);
						if (rotationConfig != null)
						{
							_plugin._config = rotationConfig;
						}
					}
					_plugin._config.CharacterName = _loadProfileSelection;
					_plugin._profileCharId = ParseStemCharId(stem);
					SaveAll();
					_statusMessage = "Loaded profile '" + _loadProfileSelection + "'";
					return;
				}
			}
			string legacyDir = Path.Combine(Path.Combine(_plugin._pluginFolder, "Character profiles"), _loadProfileSelection);
			_plugin.Rotation = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(Path.Combine(legacyDir, "rotation.json")), options) ?? new List<RotationAbility>();
			_plugin.BuffOrder = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(Path.Combine(legacyDir, "bufforder.json")), options) ?? new List<RotationAbility>();
			_plugin.Emergency = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(Path.Combine(legacyDir, "emergency.json")), options) ?? new List<RotationAbility>();
			_plugin.ToggleBuffs = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(Path.Combine(legacyDir, "toggles.json")), options) ?? new List<RotationAbility>();
			RotationConfig rotationConfig2 = JsonSerializer.Deserialize<RotationConfig>(File.ReadAllText(Path.Combine(legacyDir, "config.json")), options);
			if (rotationConfig2 != null)
			{
				_plugin._config = rotationConfig2;
			}
			_plugin._config.CharacterName = _loadProfileSelection;
			_plugin._profileCharId = 0uL;
			SaveAll();
			_statusMessage = "Loaded profile '" + _loadProfileSelection + "'";
		}
		catch (Exception ex)
		{
			_statusMessage = "Load failed: " + ex.Message;
		}
	}

	private static ulong ParseStemCharId(string stem)
	{
		int num = stem.LastIndexOf('-');
		if (num >= 0 && ulong.TryParse(stem.Substring(num + 1), out var result))
		{
			return result;
		}
		return 0uL;
	}

	private void SetAllRotationAutocast(bool value)
	{
		foreach (RotationAbility item in _plugin.Rotation)
		{
			item.Autocast = value;
		}
		_plugin.SaveRotation();
	}

	private void SetAllBuffAutocast(bool value)
	{
		foreach (RotationAbility item in _plugin.BuffOrder)
		{
			item.Autocast = value;
			if (value)
			{
				item.BuffOnMissing = false;
			}
		}
		_plugin.SaveBuffOrder();
	}

	private void RemoveSelectedRotation()
	{
		if (_rotSelectedIndex >= 0 && _rotSelectedIndex < _plugin.Rotation.Count)
		{
			_plugin.Rotation.RemoveAt(_rotSelectedIndex);
			if (_rotSelectedIndex >= _plugin.Rotation.Count)
			{
				_rotSelectedIndex = _plugin.Rotation.Count - 1;
			}
			_plugin.SaveRotation();
		}
	}

	private void MoveRotation(int direction)
	{
		int num = _rotSelectedIndex + direction;
		if (_rotSelectedIndex >= 0 && _rotSelectedIndex < _plugin.Rotation.Count && num >= 0 && num < _plugin.Rotation.Count)
		{
			RotationAbility value = _plugin.Rotation[_rotSelectedIndex];
			_plugin.Rotation[_rotSelectedIndex] = _plugin.Rotation[num];
			_plugin.Rotation[num] = value;
			_rotSelectedIndex = num;
			_plugin.SaveRotation();
		}
	}

	private void MoveToggleAt(int idx, int direction)
	{
		int num = idx + direction;
		if (idx >= 0 && idx < _plugin.ToggleBuffs.Count && num >= 0 && num < _plugin.ToggleBuffs.Count)
		{
			RotationAbility value = _plugin.ToggleBuffs[idx];
			_plugin.ToggleBuffs[idx] = _plugin.ToggleBuffs[num];
			_plugin.ToggleBuffs[num] = value;
			_plugin.SaveToggleBuffs();
		}
	}

	private void MoveBuffAt(int idx, int direction)
	{
		int num = idx + direction;
		if (idx >= 0 && idx < _plugin.BuffOrder.Count && num >= 0 && num < _plugin.BuffOrder.Count)
		{
			RotationAbility value = _plugin.BuffOrder[idx];
			_plugin.BuffOrder[idx] = _plugin.BuffOrder[num];
			_plugin.BuffOrder[num] = value;
			_plugin.SaveBuffOrder();
		}
	}

	private void MoveEmergencyItem(int idx, int direction)
	{
		int num = idx + direction;
		if (idx >= 0 && idx < _plugin.Emergency.Count && num >= 0 && num < _plugin.Emergency.Count)
		{
			RotationAbility value = _plugin.Emergency[idx];
			_plugin.Emergency[idx] = _plugin.Emergency[num];
			_plugin.Emergency[num] = value;
			_plugin.SaveEmergency();
		}
	}

	private void AddSelectedAbilities()
	{
		foreach (HotbarAbility hotbar in _abilitySelection)
		{
			if (hotbar == null)
			{
				continue;
			}
			RotationAbility ability = BuildAbilityFromHotbar(hotbar);
			_plugin.EnrichFromApi(ability);
			_plugin.Rotation.Add(ability);
		}
		_plugin.SaveRotation();
		_abilitySelection.Clear();
	}

	private void AddBuffSelection()
	{
		foreach (HotbarAbility hotbar in _abilitySelection)
		{
			if (hotbar == null)
			{
				continue;
			}
			RotationAbility ability = BuildAbilityFromHotbar(hotbar);
			_plugin.EnrichFromApi(ability);
			_plugin.DiscoverToggleProperty(ability);
			_plugin.BuffOrder.Add(ability);
		}
		_plugin.SaveBuffOrder();
		_abilitySelection.Clear();
	}

	private void AddEmergencySelection()
	{
		RotationConfig config = _plugin._config;
		foreach (HotbarAbility hotbar in _abilitySelection)
		{
			if (hotbar == null)
			{
				continue;
			}
			RotationAbility ability = BuildAbilityFromHotbar(hotbar);
			ability.CooldownMs = 50L;
			ability.Autocast = true;
			if (!string.IsNullOrEmpty(hotbar.ApiTargetType) || hotbar.ApiIsHarmful.HasValue)
			{
				ability.HealScope = ((hotbar.ApiIsHarmful == false) ? "Both" : "Self");
			}
			_plugin.Emergency.Add(ability);
		}
		_ = config;
		_plugin.SaveEmergency();
		_abilitySelection.Clear();
	}

	private void AddToggleSelection()
	{
		foreach (HotbarAbility hotbar in _abilitySelection)
		{
			if (hotbar == null)
			{
				continue;
			}
			RotationAbility ability = BuildAbilityFromHotbar(hotbar);
			ability.Autocast = true;
			_plugin.EnrichFromApi(ability);
			_plugin.DiscoverToggleProperty(ability);
			_plugin.ToggleBuffs.Add(ability);
		}
		_plugin.SaveToggleBuffs();
		_abilitySelection.Clear();
	}

	private static RotationAbility BuildAbilityFromHotbar(HotbarAbility hotbar)
	{
		int num = hotbar.BarId * 10 + hotbar.SlotId;
		if (num >= KeyOptions.Length)
		{
			num %= KeyOptions.Length;
		}
		string text = KeyOptions[num];
		RotationAbility rotationAbility = new RotationAbility();
		rotationAbility.Name = hotbar.Name;
		rotationAbility.Category = "Hotbar";
		rotationAbility.VirtualKey = VkForKeyName(text);
		rotationAbility.KeyLabel = text;
		rotationAbility.CooldownMs = hotbar.CooldownMs;
		rotationAbility.BuffDurationMs = hotbar.BuffDurationMs;
		rotationAbility.CooldownTimerDid1 = hotbar.CooldownTimerDid1;
		rotationAbility.CooldownTimerDid2 = hotbar.CooldownTimerDid2;
		rotationAbility.CooldownTimerDid3 = hotbar.CooldownTimerDid3;
		rotationAbility.CommandValue = ((hotbar.CommandValue != 0) ? new uint?(hotbar.CommandValue) : null);
		rotationAbility.ReferenceDid = ((hotbar.ReferenceDid != 0) ? new uint?(hotbar.ReferenceDid) : null);
		return rotationAbility;
	}

	private void ScanHotbarFromGame()
	{
		try
		{
			_hotbarAbilities.Clear();
			_hotbarAbilities.AddRange(_plugin.ScanHotbar());
			foreach (RotationAbility item in _plugin.Rotation)
			{
				FillCooldownFromScan(item, _hotbarAbilities);
			}
			foreach (HotbarAbility item2 in _hotbarAbilities)
			{
				if (item2.ReferenceDid != 0)
				{
					_plugin.EnrichHotbarFromApi(item2);
				}
			}
			_plugin.SaveRotation();
			_plugin.SaveBuffOrder();
			_statusMessage = $"Found {_hotbarAbilities.Count} abilities";
		}
		catch (Exception ex)
		{
			_statusMessage = "Scan failed: " + ex.Message;
		}
	}

	private static void FillCooldownFromScan(RotationAbility ability, List<HotbarAbility> results)
	{
		if (ability.CooldownMs <= 0 && ability.ReferenceDid.HasValue && ability.ReferenceDid.Value != 0)
		{
			foreach (HotbarAbility result in results)
			{
				if (result.ReferenceDid == ability.ReferenceDid.Value && result.CooldownMs > 0)
				{
					ability.CooldownMs = result.CooldownMs;
					break;
				}
			}
		}
	}
}

internal static class NativeMethods
{
	[DllImport("user32.dll")]
	internal static extern short GetAsyncKeyState(int vKey);

	[DllImport("user32.dll")]
	internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

	internal const uint KEYEVENTF_KEYUP = 2u;

	internal static void SendKeyPress(ushort virtualKey)
	{
		keybd_event((byte)virtualKey, 0, 0u, UIntPtr.Zero);
		keybd_event((byte)virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
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
[VoK.Sdk.PluginId("b1c2d3e4-f5a6-7890-1234-56789abcdef0")]
[VoK.Sdk.PluginName("RotationHelper")]
[VoK.Sdk.PluginIcon("Icon.png")]
public class RotationHelperPlugin : IPlugin, IDisposable
{
	internal static readonly Guid PluginGuid = new Guid("B1C2D3E4-F5A6-7890-1234-56789ABCDEF0");
	public bool WindowVisible { get; set; } = true;

	internal bool _showConfigWindow;
	private ImGuiConfigWindow _configWindow;
	internal HudImGui _hudImGui;

	private byte[] _toolbarImageCache;

	public byte[] ToolbarImage
	{
		get
		{
			if (_toolbarImageCache != null)
			{
				return _toolbarImageCache;
			}
			try
			{
				using Stream stream = typeof(RotationHelperPlugin).Assembly.GetManifestResourceStream("Icon.png");
				if (stream == null)
				{
					_toolbarImageCache = Array.Empty<byte>();
				}
				else
				{
					using MemoryStream memoryStream = new MemoryStream();
					stream.CopyTo(memoryStream);
					_toolbarImageCache = memoryStream.ToArray();
				}
			}
			catch
			{
				_toolbarImageCache = Array.Empty<byte>();
			}
			return _toolbarImageCache;
		}
	}

	public void ClientFrameStep()
	{
		// DH5-PORT: cached entity refresh (TODO throttle)
		if (_gameDataProvider == null) return;
		try
		{
			_currentEntity = _gameDataProvider.GetCurrentCharacterAsync().ConfigureAwait(false).GetAwaiter().GetResult();
			_selectedObject = _gameDataProvider.GetSelectedObjectAsync().ConfigureAwait(false).GetAwaiter().GetResult();
			_entities = _gameDataProvider.GetEntitiesAsync().ConfigureAwait(false).GetAwaiter().GetResult();
		}
catch { }
		BuffManager?.RefreshCache();
	}

	public void ImGui_Render()
	{
		if (_gameDataProvider == null) return;

		// The toolbar icon toggles WindowVisible. The HUD is independent of the
		// toolbar toggle (governed by HudEnabled); the "main" config window
		// follows WindowVisible so the toolbar icon opens/closes it.
		try
		{
			if (_hudImGui == null) _hudImGui = new HudImGui(this);
			if (_config != null && _config.HudEnabled) _hudImGui.Render();
		}
		catch (Exception ex) { Log("HUD render failed: " + ex.Message); }

		_showConfigWindow = WindowVisible;
		try
		{
			if (_showConfigWindow)
			{
				if (_configWindow == null) _configWindow = new ImGuiConfigWindow(this);
				_configWindow.Render();
			}
		}
		catch (Exception ex) { Log("Config render failed: " + ex.Message); }
	}

	public void Dispose()
	{
		Terminate();
	}

	internal const int VK_BOTH_MOUSE = 1073741823;

	internal static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RotationHelper");

	internal IDdoGameDataProvider _gameDataProvider;
	internal IDdoEventProvider _eventProvider;
	internal IEntity _currentEntity;
	internal IEntity _selectedObject;
	internal IReadOnlyList<IEntity> _entities = Array.Empty<IEntity>();

	private volatile bool _progressActive;

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
	private DateTime _lastNearbyLogTime = DateTime.MinValue;
	private DateTime _lastNoTargetLogTime = DateTime.MinValue;

	private ulong? _pendingEmergencyTargetId;

	private RotationAbility _pendingEmergencyAbility;

	private ulong? _pendingTargetRevertId;

	private DateTime _pendingTargetRevertTime = DateTime.MinValue;

	private ulong? _pendingBuffTargetId;

	private RotationAbility _pendingBuffAbility;

	private ulong? _pendingBuffRevertId;

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

	private readonly Dictionary<string, DateTime> _lastPartyBuffTime = new Dictionary<string, DateTime>();

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
		IEntity selectedObject = _selectedObject;
		if (selectedObject == null)
		{
			return false;
		}
		if (selectedObject.InstanceId == gdp.GetCurrentCharacterId().GetValueOrDefault())
		{
			return false;
		}
		if (ReadPropertyBool(selectedObject.GetProperty(268437503u)) == true)
		{
			return true;
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
				if (item.IsValid() && item.Connected() && string.Equals(item.Name, selectedObject.Name, StringComparison.OrdinalIgnoreCase))
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

	/* DH5-PORT: GetPluginUI() removed (no IPluginUI in DH5) */

	public void Terminate()
	{
		Shutdown();
	}

	public void Initialize(IDdoGameDataProvider gameDataProvider, IDdoEventProvider eventProvider, string folder)
	{
		_gameDataProvider = gameDataProvider;
		_eventProvider = eventProvider;
		_pluginFolder = folder;
		if (!Directory.Exists(AppDataDir))
		{
			Directory.CreateDirectory(AppDataDir);
		}
		_config = RotationConfig.Load(Path.Combine(folder, "RotationHelper.json"));
		SwapToCharacterConfig();
		LoadRotation();
		LoadBuffOrder();
		LoadEmergency();
		LoadToggleBuffs();
		ReloadToggleBuffStatesFromConfig();
		BuffManager = new BuffManager(_gameDataProvider, _eventProvider, _pluginFolder, Log, GameCache);
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
		_eventProvider.OnHotbarUsage.AddHandler(delegate(IHotbarSlot slot)
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
		_eventProvider.OnMapAreaChange.AddHandler(delegate(uint newAreaDid)
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
		_eventProvider.OnProgressStart.AddHandler(delegate
		{
			_progressActive = true;
			Log("Progress start (shrine/recall/etc) — pausing autocast");
			return Task.CompletedTask;
		});
		_eventProvider.OnProgressStop.AddHandler(delegate
		{
			_progressActive = false;
			Log("Progress stop — resuming autocast");
			return Task.CompletedTask;
		});
		_eventProvider.OnLogin.AddHandler(delegate
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
	}

	private void Shutdown()
	{
		StopRotation();
		StopBuffRotation();
		_pollTimer?.Dispose();
		_autocastTimer?.Dispose();
		_refreshTimer?.Dispose();
		BuffManager?.Shutdown();
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
				if ((_config?.AutocastRotationOnTarget ?? false) && _gameDataProvider != null && !IsHostileOrBreakable(_gameDataProvider))
				{
					_rotationIndex = 0;
					Task.Delay(100, token).Wait(token);
					continue;
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
						rotationAbility2.LastFired = DateTime.UtcNow;
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
								rotationAbility3.LastFired = DateTime.UtcNow;
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
			IEntity currentCharacter = _currentEntity;
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
					object gameTimeVal = property.Value;
					if (gameTimeVal != null)
					{
						num3 = Convert.ToDouble(gameTimeVal);
					}
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
					try { System.IO.File.AppendAllText(System.IO.Path.Combine(RotationHelperPlugin.AppDataDir, "cdiag.log"), $"[{DateTime.Now:HH:mm:ss.fff}] CD [{ability.Name}] did=0x{num4:X8} timer={num5:F2} gameTime={num3:F2} result={result}ms\r\n"); } catch {}
					lock (_cacheLock)
					{
						_cooldownCache[valueOrDefault] = (num6, DateTime.UtcNow, num3);
					}
					return result;
				}
				else
				{
					try { System.IO.File.AppendAllText(System.IO.Path.Combine(RotationHelperPlugin.AppDataDir, "cdiag.log"), $"[{DateTime.Now:HH:mm:ss.fff}] CD [{ability.Name}] did=0x{num4:X8} timer={num5:F2} gameTime={num3:F2} (timer<=gameTime, skip)\r\n"); } catch {}
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
			IEntity currentCharacter = _currentEntity;
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
				IEntity entity = _selectedObject;
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
		if (ability.CommandValue.HasValue && ability.CommandValue.Value != 0)
		{
			_gameDataProvider?.SendInput(ability.CommandValue.Value);
			ability.LastFired = DateTime.UtcNow;
			Log($"FIRE [{ability.KeyLabel}] {ability.Name} via SendInput(0x{ability.CommandValue.Value:X8})");
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
				return true;
			}
			catch (Exception ex)
			{
				Log("SendKey failed: " + ex.Message + ", falling back to OS SendInput");
				NativeMethods.SendKeyPress((ushort)ability.VirtualKey);
				ability.LastFired = DateTime.UtcNow;
				Log($"FIRE [{ability.KeyLabel}] {ability.Name} via OS SendInput");
				return true;
			}
		}
		Log($"SKIP [{ability.KeyLabel}] {ability.Name} - no CommandValue or VirtualKey");
		return false;
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
							foreach (IProperty property6 in propertyCollection.Properties)
							{
								string text2 = null;
								string text3 = null;
								if (property6 is IStringInfoProperty stringInfoProperty2)
								{
									text2 = stringInfoProperty2.GetText(propertyMaster, referenceDid, propertyCollection);
									text3 = "StringInfo";
								}
								else if (property6 is IStringProperty stringProperty2)
								{
									text2 = stringProperty2.StringValue;
									text3 = "String";
								}
								else if (property6 is IUInt32Property { UInt32Value: var uInt32Value })
								{
									text2 = uInt32Value?.ToString();
									text3 = "U32";
								}
								else if (property6 is IInt32Property { Int32Value: var int32Value })
								{
									text2 = int32Value?.ToString();
									text3 = "I32";
								}
								else if (property6 is IFloatProperty { FloatValue: var floatValue })
								{
									text2 = floatValue?.ToString();
									text3 = "Float";
								}
								else if (property6 is IDoubleProperty { DoubleValue: var doubleValue })
								{
									text2 = doubleValue?.ToString();
									text3 = "Double";
								}
								else if (property6 is IByteProperty { ByteValue: var byteValue })
								{
									text2 = byteValue.ToString();
									text3 = "Byte";
								}
								else
								{
									text3 = "OTHER";
								}
								Log($"    prop[{property6.PropertyId}] via {text3} = \"{text2 ?? "(null)"}\"");
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
							foreach (IProperty property7 in propertyCollection2.Properties)
							{
								string text4 = null;
								string text5 = null;
								if (property7 is IStringInfoProperty stringInfoProperty4)
								{
									text4 = stringInfoProperty4.GetText(propertyMaster, referenceDid, propertyCollection2);
									text5 = "StringInfo";
								}
								else if (property7 is IStringProperty stringProperty3)
								{
									text4 = stringProperty3.StringValue;
									text5 = "String";
								}
								else if (property7 is IUInt32Property { UInt32Value: var uInt32Value2 })
								{
									text4 = uInt32Value2?.ToString();
									text5 = "U32";
								}
								else if (property7 is IInt32Property { Int32Value: var int32Value2 })
								{
									text4 = int32Value2?.ToString();
									text5 = "I32";
								}
								else if (property7 is IFloatProperty { FloatValue: var floatValue2 })
								{
									text4 = floatValue2?.ToString();
									text5 = "Float";
								}
								else if (property7 is IDoubleProperty { DoubleValue: var doubleValue2 })
								{
									text4 = doubleValue2?.ToString();
									text5 = "Double";
								}
								else if (property7 is IByteProperty { ByteValue: var byteValue2 })
								{
									text4 = byteValue2.ToString();
									text5 = "Byte";
								}
								else
								{
									text5 = "OTHER";
								}
								Log($"    weenie[{property7.PropertyId}] via {text5} = \"{text4 ?? "(null)"}\"");
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
								ICharacter characterBuildData = _gameDataProvider.GetCharacterBuildDataAsync(num3.Value).ConfigureAwait(false).GetAwaiter().GetResult();
								if (characterBuildData != null)
								{
									Log($"  CharacterBuildData: {characterBuildData.Properties.Properties.Count} entries");
									try
									{
										Log("  buildData.Name = \"" + (characterBuildData.Name ?? "(null)") + "\"");
									}
									catch
									{
									}
									int num4 = 0;
									foreach (IProperty property8 in characterBuildData.Properties.Properties)
									{
										if (property8 is IStringInfoProperty stringInfoProperty5)
										{
											string text6 = stringInfoProperty5.GetText(propertyMaster, 0u, characterBuildData.Properties);
											if (!string.IsNullOrEmpty(text6) && num4 < 20)
											{
												Log($"  buildData prop[{property8.PropertyId}] (StringInfo) = \"{text6}\"");
												num4++;
											}
										}
										else if (property8 is IStringProperty stringProperty4)
										{
											if (!string.IsNullOrEmpty(stringProperty4.StringValue) && num4 < 20)
											{
												Log($"  buildData prop[{property8.PropertyId}] (String) = \"{stringProperty4.StringValue}\"");
												num4++;
											}
										}
										else if (property8 is IUInt32Property iUInt32Property3 && (property8.PropertyId >= 1879048192 || property8.PropertyId < 256))
										{
											Log($"  buildData prop[{property8.PropertyId}] (U32) = 0x{iUInt32Property3.UInt32Value:X8}");
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
						IEntity entity = _currentEntity;
						if (entity != null)
						{
							double num10 = 0.0;
							IProperty property4 = entity.GetProperty(GameTimeDid);
							if (property4 != null)
							{
								try
								{
									num10 = Convert.ToDouble(property4);
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
											num12 = Convert.ToDouble(property5);
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
			foreach (IProperty property in propertyCollection.Properties)
			{
				uint? num = null;
				if (property is IUInt32Property { UInt32Value: not null } iUInt32Property)
				{
					num = iUInt32Property.UInt32Value.Value;
				}
				else if (property is IInt32Property { Int32Value: var int32Value } int32Property && int32Value.HasValue && int32Value.Value > 0)
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
						Log($"  ChaseName: DID 0x{did:X8} prop[{property.PropertyId}] -> 0x{value:X8} name=\"{text}\"");
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
							Log($"  ChaseName: DID 0x{did:X8} prop[{property.PropertyId}] -> weenie 0x{value:X8} name=\"{text}\"");
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

	internal static string ExtractTextFromProps(IPropertyCollection props, uint did, IPropertyMaster propMaster)
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

	internal HudToggleState[] GetToggleHudStates()
	{
		lock (_lock)
		{
			int count = ToggleBuffs.Count;
			if (count == 0)
			{
				return Array.Empty<HudToggleState>();
			}
			HudToggleState[] array = new HudToggleState[count];
			for (int i = 0; i < count; i++)
			{
				RotationAbility rotationAbility = ToggleBuffs[i];
				uint? referenceDid = rotationAbility.ReferenceDid;
				bool active = ((referenceDid != 1879062547) ? (referenceDid.HasValue && _sessionToggleFired.Contains(rotationAbility.ReferenceDid.Value)) : ((_currentEntity?.InstanceProperties?.GetBytePropertyValue(268442925u)).GetValueOrDefault() > 0));
				array[i] = new HudToggleState
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
			IEntity entity = _currentEntity;
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
						IEntity entity = _currentEntity;
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
						if (IsToggleActive(value, rotationAbility.Name))
						{
							continue;
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
						IEntity entity2 = _currentEntity;
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
									flag2 = !item.IsValid() || !item.Connected() || item.HitPoints <= 0;
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
								IEntity entity3 = _currentEntity;
								if (entity3 != null)
								{
									IProperty property2 = entity3.GetProperty(_pendingBuffAbility.ToggleStatePropertyDid);
									flag3 = property2 != null && string.Equals(Convert.ToString(property2.Value), _pendingBuffAbility.ToggleExpectedValue, StringComparison.OrdinalIgnoreCase);
								}
							}
							if (!flag3 && valueOrDefault != 0)
							{
								uint cachedEffectDid = GameCache.GetCachedEffectDid(valueOrDefault);
								if (BuffManager != null && (BuffManager.IsEffectActiveByDid(valueOrDefault) || (cachedEffectDid != 0 && BuffManager.IsEffectActiveByDid(cachedEffectDid))))
								{
									flag3 = true;
								}
								if (!flag3)
								{
									IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary = _gameDataProvider?.GetActiveEffects();
									if (readOnlyDictionary != null)
									{
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
						}
						else if (_pendingBuffAbility.ToggleStatePropertyDid != 0)
						{
							IEntity entity4 = _selectedObject;
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
				bool _skipMounted = _config.SkipWhileMounted && (_currentEntity?.GetProperty(3872u) != null);
				if (_skipMounted)
				{
					Log("EMERGENCY SKIP: mounted");
					goto IL_SkipEmergency;
				}
				Log($"EMERGENCY CHECK: {Emergency.Count} abilities, not pending");
				try
				{
					IEntity entity5 = _currentEntity;
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
									if (item3.IsValid() && item3.Connected() && item3.MaxHitPoints > 0 && item3.HitPoints > 0)
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
							Vector3? myPos = _currentEntity?.Location?.Position;
							Dictionary<string, ulong> partyEntityIds = new Dictionary<string, ulong>();
							try
							{
								foreach (IEntity e in _entities ?? new List<IEntity>())
								{
									if (!string.IsNullOrEmpty(e.Name))
										partyEntityIds[e.Name] = e.InstanceId;
								}
							}
							catch { }
						foreach (IPartyMember item4 in readOnlyList2)
							{
								if (!item4.IsValid() || !item4.Connected() || item4.MaxHitPoints <= 0 || item4.HitPoints <= 0)
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
							if ((utcNow - _lastNoTargetLogTime).TotalSeconds >= 2.0)
							{
								_lastNoTargetLogTime = utcNow;
								Log($"NO HOSTILE TARGET — rotation held (selected={_selectedObject?.Name ?? "none"})");
							}
							continue;
						}
					}
					if (rotationAbility5.MinNearbyEnemies > 0)
					{
						int num13 = CountNearbyEnemies();
						if (num13 < rotationAbility5.MinNearbyEnemies)
						{
							if ((utcNow - _lastNearbyLogTime).TotalSeconds >= 1.0)
							{
								_lastNearbyLogTime = utcNow;
								Log($"MIN NEARBY [{l}] [{rotationAbility5.KeyLabel}] {rotationAbility5.Name} — nearby={num13} need={rotationAbility5.MinNearbyEnemies} — {DescribeNearbyCensus()}");
							}
							continue;
						}
					}
					if (rotationAbility5.TargetHpThreshold > 0)
					{
						IEntity entity6 = _selectedObject;
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
						IEntity entity7 = _currentEntity;
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
						bool? flag6 = _gameDataProvider?.InTownAsync().GetAwaiter().GetResult();
						bool? flag7 = flag6;
						int num16;
						if (!flag7.HasValue)
						{
							IDdoGameDataProvider gameDataProvider2 = _gameDataProvider;
							if (gameDataProvider2 != null)
							{
								IEntity currentCharacter = _currentEntity;
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
						bool? wInTown = _gameDataProvider?.InTownAsync().GetAwaiter().GetResult();
						if ((!wInTown.HasValue || !wInTown.Value) && _gameDataProvider?.GetInstanceQuestDidAsync().GetAwaiter().GetResult().GetValueOrDefault() == 0)
						{
							IEntity wChar = _currentEntity;
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
								IEntity entity8 = _currentEntity;
								if (entity8 != null)
								{
									IProperty property4 = entity8.GetProperty(rotationAbility7.ToggleStatePropertyDid);
									flag9 = property4 != null && string.Equals(Convert.ToString(property4.Value), rotationAbility7.ToggleExpectedValue, StringComparison.OrdinalIgnoreCase);
								}
							}
							if (!flag9)
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
							IEntity entity9 = _currentEntity;
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
										uint cachedEffectDid2 = GameCache.GetCachedEffectDid(valueOrDefault2);
										if (BuffManager != null && (BuffManager.IsEffectActiveByDid(valueOrDefault2) || (cachedEffectDid2 != 0 && BuffManager.IsEffectActiveByDid(cachedEffectDid2))))
										{
											flag10 = true;
										}
										if (!flag10)
										{
											IReadOnlyDictionary<int, IActiveEffect> readOnlyDictionary2 = _gameDataProvider?.GetActiveEffects();
											if (readOnlyDictionary2 != null)
											{
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
									}
									if (flag10)
									{
										foreach (IPartyMember item6 in readOnlyList3)
										{
											if (item6.IsValid() && item6.Connected() && item6.HitPoints > 0 && item6.MaxHitPoints > 0)
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
											foreach (IEntity e2 in _entities ?? new List<IEntity>())
											{
												if (!string.IsNullOrEmpty(e2.Name))
													buffEntityIds[e2.Name] = e2.InstanceId;
											}
										}
										catch { }
										Vector3? myPos2 = entity9?.Location?.Position;
										foreach (IPartyMember item7 in readOnlyList3)
										{
											if (!item7.IsValid() || !item7.Connected() || item7.HitPoints <= 0 || item7.MaxHitPoints <= 0)
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
			bool? flag = gameDataProvider.InTownAsync().GetAwaiter().GetResult();
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
			IEntity entity = _currentEntity;
			if (entity == null)
			{
				return null;
			}
			IEntity entity2 = _selectedObject;
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
			IEntity entity = _currentEntity;
			if (entity == null || !(entity.Location?.Position).HasValue)
			{
				return;
			}
			Vector3 value = entity.Location.Position.Value;
			double num = _config.AutoTargetMaxRange;
			double num2 = -1.0;
			IEntity entity2 = null;
			IReadOnlyCollection<IEntity> readOnlyCollection = _entities;
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
				IEntity entity3 = _selectedObject;
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
			IEntity entity = _currentEntity;
			if (entity == null || !(entity.Location?.Position).HasValue)
			{
				return;
			}
			Vector3 value = entity.Location.Position.Value;
			double num = _config.AutoTargetMaxRange;
			double num2 = double.MaxValue;
			IEntity entity2 = null;
			IReadOnlyCollection<IEntity> readOnlyCollection = _entities;
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
				IEntity entity3 = _selectedObject;
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

	private static bool? ReadPropertyBool(IProperty property)
	{
		if (property?.Value == null)
		{
			return null;
		}
		try
		{
			if (property.Value is bool b)
			{
				return b;
			}
			if (property.Value is IConvertible)
			{
				return Convert.ToBoolean(property.Value);
			}
		}
		catch
		{
		}
		return null;
	}

	private static long? ReadPropertyLong(IProperty property)
	{
		if (property?.Value == null)
		{
			return null;
		}
		try
		{
			if (property.Value is string s)
			{
				return long.TryParse(s, out long parsed) ? parsed : (long?)null;
			}
			if (property.Value is IConvertible)
			{
				return Convert.ToInt64(property.Value);
			}
		}
		catch
		{
		}
		return null;
	}

	private bool IsHostileEntity(IEntity entity)
	{
		try
		{
			if (entity == null || entity.InstanceId == 0L || entity.IsDeleted)
			{
				return false;
			}
			if (entity.InstanceId == _gameDataProvider?.GetCurrentCharacterId().GetValueOrDefault())
			{
				return false;
			}
			if (entity.HitPoints_Current.GetValueOrDefault() <= 0)
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
			// DH5 SDK: IProperty is not IConvertible, so Convert.ToBoolean(property) always threw.
			if (ReadPropertyBool(entity.GetProperty(268437503u)) == true)
			{
				return true;
			}
			long? attackable = ReadPropertyLong(entity.GetProperty(268435581u));
			if (attackable.HasValue)
			{
				return attackable.Value > 0;
			}
			uint? weenieType = entity.WeeniePropertyCollection?.GetEnumProperty(287u)?.UInt32Value;
			if (weenieType == 1245313)
			{
				return true;
			}
			if (weenieType.HasValue)
			{
				switch ((WeenieType)weenieType.Value)
				{
				case WeenieType.Trap:
				case WeenieType.Chest:
				case WeenieType.Door:
				case WeenieType.Collectable:
				case WeenieType.Henchman:
				case WeenieType.Restpoint:
				case WeenieType.Mount:
				case WeenieType.CosmeticPet:
					return false;
				}
			}
			return entity.HitPoints_Max.GetValueOrDefault() > 0;
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
			IEntity entity = _currentEntity;
			if (entity == null || !(entity.Location?.Position).HasValue)
			{
				return null;
			}
			Vector3 value = entity.Location.Position.Value;
			IEntity entity2 = _selectedObject;
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
			IEntity entity = _currentEntity;
			Vector3? myPos = entity?.Location?.Position ?? entity?.Position;
			if (entity == null || !myPos.HasValue)
			{
				return 0;
			}
			double num = (((double)_config.AutoTargetMaxRange > 0.0) ? ((double)_config.AutoTargetMaxRange) : 40.0);
			IReadOnlyCollection<IEntity> readOnlyCollection = _entities;
			if (readOnlyCollection == null || readOnlyCollection.Count == 0)
			{
				return 0;
			}
			int num2 = 0;
			foreach (IEntity item in readOnlyCollection)
			{
				if (!IsHostileEntity(item))
				{
					continue;
				}
				Vector3? vector = item.Location?.Position ?? item.Position;
				if (vector.HasValue && Distance(myPos.Value, vector.Value) <= num)
				{
					num2++;
				}
			}
			return num2;
		}
		catch
		{
			return 0;
		}
	}

	// Diagnostic: explains why a "Near" (MinNearbyEnemies) ability is being blocked.
	internal string DescribeNearbyCensus()
	{
		try
		{
			IEntity entity = _currentEntity;
			Vector3? myPos = entity?.Location?.Position ?? entity?.Position;
			double num = (((double)_config.AutoTargetMaxRange > 0.0) ? ((double)_config.AutoTargetMaxRange) : 40.0);
			IReadOnlyCollection<IEntity> readOnlyCollection = _entities;
			int total = readOnlyCollection?.Count ?? 0;
			int withPos = 0;
			int alive = 0;
			List<(double Dist, string Text)> nearest = new List<(double, string)>();
			if (readOnlyCollection != null && myPos.HasValue)
			{
				foreach (IEntity item in readOnlyCollection)
				{
					if (item == null)
					{
						continue;
					}
					Vector3? vector = item.Location?.Position ?? item.Position;
					if (!vector.HasValue)
					{
						continue;
					}
					withPos++;
					if (item.HitPoints_Current.GetValueOrDefault() <= 0)
					{
						continue;
					}
					alive++;
					uint? weenieType = item.WeeniePropertyCollection?.GetEnumProperty(287u)?.UInt32Value;
					nearest.Add((Distance(myPos.Value, vector.Value), $"{item.Name}[wt=0x{weenieType.GetValueOrDefault():X}]"));
				}
			}
			nearest.Sort((a, b) => a.Dist.CompareTo(b.Dist));
			string text = string.Join(", ", nearest.Take(6).Select(x => $"{x.Text}@{x.Dist:F0}u"));
			return $"census radius={num:F0} entities={total} withPos={withPos} alive={alive} hostileInRange={CountNearbyEnemies()} near=[{text}]";
		}
		catch (Exception ex)
		{
			return "census failed: " + ex.Message;
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
				DateTime? dateTime = (Dh5Compat.EffectExpiration(value2));
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
					DateTime? dateTime3 = (Dh5Compat.EffectExpiration(value4));
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
		foreach (IProperty property4 in propertyCollection.Properties)
		{
			string text = null;
			if (property4 is IStringInfoProperty stringInfoProperty)
			{
				text = stringInfoProperty.GetText(propMaster, did, propertyCollection);
			}
			else if (property4 is IStringProperty stringProperty)
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
			foreach (IProperty property in propertyCollection.Properties)
			{
				uint? num = null;
				if (property is IUInt32Property { UInt32Value: not null } iUInt32Property)
				{
					num = iUInt32Property.UInt32Value.Value;
				}
				else if (property is IInt32Property { Int32Value: var int32Value } int32Property && int32Value.HasValue && int32Value.Value > 0)
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
		try
		{
			SwapToCharacterConfig();
		}
		catch (Exception ex)
		{
			Log("OpenSetupSkills failed: " + ex.Message);
		}
		_showConfigWindow = true;
	}

	private void SwapToCharacterConfig()
	{
		try
		{
			string text = _gameDataProvider?.GetCurrentCharacterNameAsync().GetAwaiter().GetResult();
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			ulong num = _gameDataProvider?.GetCurrentCharacterId().GetValueOrDefault() ?? 0;
			if (string.IsNullOrEmpty(_config.CharacterName))
			{
				_config.CharacterName = text;
				_profileCharId = num;
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
				Log($"Initialized character '{text}' (id {num})");
				return;
			}
			if (_config.CharacterName == text && _profileCharId == num)
			{
				return;
			}
			SaveRotation();
			SaveBuffOrder();
			SaveEmergency();
			SaveToggleBuffs();
			SaveConfig();
			Log($"Saved profile for '{_config.CharacterName}' (id {_profileCharId})");
			string characterName = _config.CharacterName;
			ulong num2 = _profileCharId;
			_config.CharacterName = text;
			_profileCharId = num;
			LoadConfig();
			LoadRotation();
			LoadBuffOrder();
			LoadEmergency();
			LoadToggleBuffs();
			ClearToggleCache();
			BuffManager?.LoadProfile();
			BuffManager?.ResetZoneState();
			Log($"Switched to character '{text}' (id {num}) (was '{characterName ?? "none"}' id {num2})");
		}
		catch
		{
		}
	}

	private string ProfileRoot => Path.Combine(_pluginFolder, "profiles");

	private string GetServerFolderName()
	{
		try
		{
			string text = _gameDataProvider?.GetServerName();
			if (!string.IsNullOrEmpty(text))
			{
				return string.Join("_", text.Split(Path.GetInvalidFileNameChars()));
			}
		}
		catch
		{
		}
		return "Unknown";
	}

	private string GetAccountFolderName()
	{
		try
		{
			string text = _gameDataProvider?.GetSubscriptionKeyHash(this);
			if (!string.IsNullOrEmpty(text))
			{
				return text;
			}
		}
		catch
		{
		}
		return "Unknown";
	}

	internal string GetAccountDir()
	{
		return Path.Combine(ProfileRoot, GetServerFolderName(), GetAccountFolderName());
	}

	internal string GetProfileDir()
	{
		string text = _config?.CharacterName;
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}
		string path = string.Join("_", text.Split(Path.GetInvalidFileNameChars()));
		return Path.Combine(ProfileRoot, GetServerFolderName(), GetAccountFolderName(), path);
	}

	internal ulong _profileCharId;

	private string GetFileStem()
	{
		string text = _config?.CharacterName;
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}
		string text2 = string.Join("_", text.Split(Path.GetInvalidFileNameChars()));
		ulong num = _profileCharId;
		if (num == 0)
		{
			num = ResolveStoredCharIdForProfile(GetProfileDir(), text2);
		}
		if (num != 0)
		{
			return text2 + "-" + num;
		}
		return text2;
	}

	private ulong ResolveStoredCharIdForProfile(string profileDir, string safeName)
	{
		if (string.IsNullOrEmpty(profileDir) || !Directory.Exists(profileDir))
		{
			return 0L;
		}
		try
		{
			string[] files = Directory.GetFiles(profileDir, safeName + "-*settings.json");
			foreach (string path in files)
			{
				string fileName = Path.GetFileNameWithoutExtension(path);
				if (fileName.StartsWith(safeName + "-", StringComparison.Ordinal))
				{
					string idText = fileName.Substring((safeName + "-").Length);
					if (ulong.TryParse(idText, out var parsed))
					{
						return parsed;
					}
				}
			}
		}
		catch
		{
		}
		return 0L;
	}

	private string GetConfigPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, GetFileStem() + "-settings.json");
		}
		return Path.Combine(_pluginFolder, "RotationHelper.json");
	}

	private string GetSnapshotPath(string suffix)
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, GetFileStem() + suffix);
		}
		return null;
	}

	internal void SaveProfileSnapshot()
	{
		try
		{
			SaveRotation();
			SaveBuffOrder();
			SaveEmergency();
			SaveToggleBuffs();
			SaveConfig();
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				WriteIndented = true
			};
			string[] suffixes = new string[5] { "-rotation.json", "-bufforder.json", "-emergency.json", "-toggles.json", "-settings.json" };
			bool wroteAny = false;
			foreach (string suffix in suffixes)
			{
				string livePath = GetSnapshotPath(suffix);
				if (string.IsNullOrEmpty(livePath) || !File.Exists(livePath))
				{
					continue;
				}
				string snapshotPath = GetSnapshotPath("-saved" + suffix);
				File.Copy(livePath, snapshotPath, true);
				wroteAny = true;
			}
			Log(wroteAny ? ("Saved profile snapshot for '" + _config?.CharacterName + "'") : "Save profile snapshot: no files to snapshot");
		}
		catch (Exception ex)
		{
			Log("Save profile snapshot error: " + ex.Message);
		}
	}

	internal bool LoadProfileSnapshot()
	{
		try
		{
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				WriteIndented = true
			};
			string rotationSnapshot = GetSnapshotPath("-saved-rotation.json");
			if (string.IsNullOrEmpty(rotationSnapshot) || !File.Exists(rotationSnapshot))
			{
				return false;
			}
			Rotation = JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(rotationSnapshot), options) ?? new List<RotationAbility>();
			string buffSnapshot = GetSnapshotPath("-saved-bufforder.json");
			BuffOrder = (File.Exists(buffSnapshot) ? JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(buffSnapshot), options) : null) ?? new List<RotationAbility>();
			string emergencySnapshot = GetSnapshotPath("-saved-emergency.json");
			Emergency = (File.Exists(emergencySnapshot) ? JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(emergencySnapshot), options) : null) ?? new List<RotationAbility>();
			string togglesSnapshot = GetSnapshotPath("-saved-toggles.json");
			ToggleBuffs = (File.Exists(togglesSnapshot) ? JsonSerializer.Deserialize<List<RotationAbility>>(File.ReadAllText(togglesSnapshot), options) : null) ?? new List<RotationAbility>();
			string settingsSnapshot = GetSnapshotPath("-saved-settings.json");
			if (File.Exists(settingsSnapshot))
			{
				RotationConfig rotationConfig = JsonSerializer.Deserialize<RotationConfig>(File.ReadAllText(settingsSnapshot), options);
				if (rotationConfig != null)
				{
					string characterName = _config?.CharacterName;
					_config = rotationConfig;
					_config.CharacterName = characterName;
				}
			}
			SaveRotation();
			SaveBuffOrder();
			SaveEmergency();
			SaveToggleBuffs();
			SaveConfig();
			Log("Reverted to saved profile snapshot for '" + _config?.CharacterName + "'");
			return true;
		}
		catch (Exception ex)
		{
			Log("Load profile snapshot error: " + ex.Message);
			return false;
		}
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
		return _gameDataProvider?.GetCurrentCharacterNameAsync().GetAwaiter().GetResult() ?? "Unknown";
	}

	private string GetRotationPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, GetFileStem() + "-rotation.json");
		}
		return Path.Combine(_pluginFolder, "rotation.json");
	}

	private string GetBuffPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, GetFileStem() + "-bufforder.json");
		}
		return Path.Combine(_pluginFolder, "bufforder.json");
	}

	private string GetEmergencyPath()
	{
		string profileDir = GetProfileDir();
		if (profileDir != null)
		{
			return Path.Combine(profileDir, GetFileStem() + "-emergency.json");
		}
		return Path.Combine(_pluginFolder, "emergency.json");
	}

	private void MigrateFromLegacyIfNeeded()
	{
		string text = _config?.CharacterName;
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		string text2 = string.Join("_", text.Split(Path.GetInvalidFileNameChars()));
		string targetDir = GetProfileDir();
		if (string.IsNullOrEmpty(targetDir))
		{
			return;
		}
		if (!Directory.Exists(targetDir))
		{
			Directory.CreateDirectory(targetDir);
		}
		string stem = GetFileStem();
		string[] array = new string[4] { "rotation", "bufforder", "emergency", "settings" };
		string[] array2 = new string[4] { "rotation", "bufforder", "emergency", "RotationHelper" };
		for (int i = 0; i < array.Length; i++)
		{
			string text3 = Path.Combine(_pluginFolder, array2[i] + "_" + text2 + ".json");
			if (!File.Exists(text3))
			{
				text3 = Path.Combine(_pluginFolder, "Character profiles", text2, array[i] + ".json");
			}
			string text4 = Path.Combine(targetDir, stem + "-" + array[i] + ".json");
			if (File.Exists(text3) && !File.Exists(text4))
			{
				try
				{
					File.Move(text3, text4);
				}
				catch
				{
				}
			}
		}
		LoadRotation();
		LoadBuffOrder();
		LoadEmergency();
		LoadConfig();
	}

	internal void DumpCharacterProperties(string outputPath)
	{
		try
		{
			IEntity entity = _currentEntity;
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
			DumpPropertyCollection(streamWriter, "PropertyCollection (Instance)", entity.GetCombinedPropertyCollection());
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
		foreach (IProperty property in collection.Properties)
		{
			IProperty value = property;
			if (value != null)
			{
				try
				{
					sw.WriteLine($"0x{value.PropertyId:X8} | {value.PropertyName ?? "?"} | {value.Value ?? "(null)"} | {value.Type ?? "?"}");
				}
				catch
				{
					sw.WriteLine($"0x{property.PropertyId:X8} | ? | (error reading) | ?");
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
			return Path.Combine(profileDir, GetFileStem() + "-toggles.json");
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
			foreach (IProperty property5 in props.Properties)
			{
				IProperty value = property5;
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
					foreach (IProperty ep in entryProps.Properties)
					{
						value = ep;
						IUInt32Property u32 = value as IUInt32Property;
						if (u32 != null && u32.UInt32Value > 1879048192 && u32.UInt32Value < 1895825407)
						{
							spellDid = u32.UInt32Value;
							break;
						}
						value = ep;
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
					foreach (IProperty property6 in effectProps.Properties)
					{
						value = property6;
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
							foreach (IProperty eep in entryProps2.Properties)
							{
								uint? modSpellDid = null;
								value = eep;
								IUInt32Property u33 = value as IUInt32Property;
								if (u33 != null && u33.UInt32Value == spellDid)
								{
									modSpellDid = u33.UInt32Value;
								}
								else
								{
									value = eep;
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
