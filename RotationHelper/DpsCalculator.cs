using System;
using System.Collections.Generic;
using System.Linq;
using VoK.Sdk;

namespace RotationHelper;

public class DpsCalculator
{
    private readonly object _lock = new object();
    private readonly Queue<DamageEvent> _damageEvents = new Queue<DamageEvent>();
    private readonly int _maxWindowSeconds;
    
    // Track entity HP changes for damage detection
    private readonly Dictionary<ulong, EntityHpSnapshot> _entityHpSnapshots = new Dictionary<ulong, EntityHpSnapshot>();
    
    // Combat session tracking
    private DateTime _combatStartTime = DateTime.MinValue;
    private DateTime _lastDamageTime = DateTime.MinValue;
    private bool _inCombat;
    private int _totalDamageDealt;
    private int _highestDamage;
    private int _damageEventsCount;
    
    // Current session info
    public TimeSpan CombatDuration => _combatStartTime == DateTime.MinValue ? TimeSpan.Zero : DateTime.UtcNow - _combatStartTime;
    public int TotalDamageDealt => _totalDamageDealt;
    public int HighestDamage => _highestDamage;
    public int DamageEventsCount => _damageEventsCount;
    public bool InCombat => _inCombat;
    
    public DpsCalculator(int maxWindowSeconds = 10)
    {
        _maxWindowSeconds = maxWindowSeconds;
    }

    /// <summary>
    /// Call this periodically with the current game state to detect damage.
    /// We track HP changes on hostile entities to calculate damage.
    /// </summary>
    public void UpdateFromGameState(IGameDataProvider gameDataProvider)
    {
        if (gameDataProvider == null) return;
        
        try
        {
            // Get selected target first (primary damage target)
            var target = gameDataProvider.GetSelectedObject();
            if (target != null && target.InstanceId != 0)
            {
                ProcessEntityHp(target);
            }
            
            // Also check nearby entities for AOE damage
            var entities = gameDataProvider.GetEntities();
            if (entities != null)
            {
                foreach (var entity in entities)
                {
                    if (entity == null || entity.InstanceId == 0) continue;
                    // Skip our own character
                    var currentChar = gameDataProvider.GetCurrentCharacter();
                    if (currentChar != null && entity.InstanceId == currentChar.InstanceId)
                        continue;
                    
                    ProcessEntityHp(entity);
                }
            }
            
            // Check combat timeout (5 seconds without damage = out of combat)
            if (_inCombat && (DateTime.UtcNow - _lastDamageTime).TotalSeconds > 5)
            {
                _inCombat = false;
            }
        }
        catch
        {
            // Silently handle errors to not disrupt the plugin
        }
    }
    
    private void ProcessEntityHp(IEntity entity)
    {
        if (entity == null || !entity.HitPoints_Current.HasValue || !entity.HitPoints_Max.HasValue)
            return;
        
        var instanceId = entity.InstanceId;
        var currentHp = entity.HitPoints_Current.Value;
        var now = DateTime.UtcNow;
        
        lock (_lock)
        {
            if (_entityHpSnapshots.TryGetValue(instanceId, out var snapshot))
            {
                // If HP went down, that's damage
                if (currentHp < snapshot.LastHp && snapshot.LastHp > 0)
                {
                    var damage = snapshot.LastHp - currentHp;
                    RecordDamage(damage, now);
                }
            }
            
            _entityHpSnapshots[instanceId] = new EntityHpSnapshot
            {
                LastHp = currentHp,
                LastSeen = now
            };
            
            // Cleanup old snapshots (entities we haven't seen in 30 seconds)
            var cutoff = now.AddSeconds(-30);
            var oldKeys = _entityHpSnapshots
                .Where(kvp => kvp.Value.LastSeen < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in oldKeys)
            {
                _entityHpSnapshots.Remove(key);
            }
        }
    }
    
    private void RecordDamage(int damage, DateTime timestamp)
    {
        if (damage <= 0) return;
        
        lock (_lock)
        {
            _damageEvents.Enqueue(new DamageEvent
            {
                Amount = damage,
                Timestamp = timestamp
            });
            
            _totalDamageDealt += damage;
            _damageEventsCount++;
            _lastDamageTime = timestamp;
            
            if (damage > _highestDamage)
                _highestDamage = damage;
            
            if (!_inCombat)
            {
                _inCombat = true;
                _combatStartTime = timestamp;
            }
            
            // Remove events outside the window
            var windowStart = timestamp.AddSeconds(-_maxWindowSeconds);
            while (_damageEvents.Count > 0 && _damageEvents.Peek().Timestamp < windowStart)
            {
                _damageEvents.Dequeue();
            }
        }
    }
    
    /// <summary>
    /// Gets the current DPS (damage per second) over the configured window.
    /// </summary>
    public double GetCurrentDps()
    {
        lock (_lock)
        {
            if (_damageEvents.Count == 0) return 0;
            
            var now = DateTime.UtcNow;
            var windowStart = now.AddSeconds(-_maxWindowSeconds);
            
            // Remove stale events
            while (_damageEvents.Count > 0 && _damageEvents.Peek().Timestamp < windowStart)
            {
                _damageEvents.Dequeue();
            }
            
            if (_damageEvents.Count == 0) return 0;
            
            var totalDamage = _damageEvents.Sum(e => e.Amount);
            var actualWindow = (now - _damageEvents.Min(e => e.Timestamp)).TotalSeconds;
            
            // If we only have very recent events, use a minimum window of 1 second
            // to avoid showing inflated DPS from a single big hit
            actualWindow = Math.Max(actualWindow, 1.0);
            
            return totalDamage / actualWindow;
        }
    }
    
    /// <summary>
    /// Gets the DPS over the entire combat session.
    /// </summary>
    public double GetSessionDps()
    {
        lock (_lock)
        {
            if (_totalDamageDealt == 0) return 0;
            var duration = CombatDuration.TotalSeconds;
            if (duration < 1) return _totalDamageDealt; // If less than 1 second, show total as DPS
            return _totalDamageDealt / duration;
        }
    }
    
    public void Reset()
    {
        lock (_lock)
        {
            _damageEvents.Clear();
            _entityHpSnapshots.Clear();
            _combatStartTime = DateTime.MinValue;
            _lastDamageTime = DateTime.MinValue;
            _inCombat = false;
            _totalDamageDealt = 0;
            _highestDamage = 0;
            _damageEventsCount = 0;
        }
    }
    
    private class DamageEvent
    {
        public int Amount { get; set; }
        public DateTime Timestamp { get; set; }
    }
    
    private class EntityHpSnapshot
    {
        public int LastHp { get; set; }
        public DateTime LastSeen { get; set; }
    }
}
