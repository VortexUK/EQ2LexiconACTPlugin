using System;
using System.Collections.Generic;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Plain DTOs that mirror the slice of ACT's data model we read at
    /// capture time. PayloadBuilder consumes these — keeping the builder
    /// pure (no ACT types) means it can be unit-tested without
    /// instantiating ACT's sealed classes.
    /// </summary>
    public sealed class EncounterSnapshot
    {
        public string EncId { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Zone { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public long Damage { get; set; }
        public double DPS { get; set; }
        public int AlliedKills { get; set; }
        public int AlliedDeaths { get; set; }

        /// <summary>ACT's GetEncounterSuccessLevel(): 0=unknown, 1=win, 2=loss, 3=mixed.</summary>
        public int SuccessLevel { get; set; }

        public List<CombatantSnapshot> Combatants { get; set; } = new List<CombatantSnapshot>();

        /// <summary>
        /// Names of combatants ACT considers allies. The EQ2 ACT parser
        /// already attributes pets to their owners, so this set is the
        /// authoritative ally/enemy split — no name-shape heuristic needed.
        /// </summary>
        public HashSet<string> AllyNames { get; set; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class CombatantSnapshot
    {
        public string Name { get; set; } = "";
        public DateTime EncStartTime { get; set; }
        public DateTime EncEndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public long Damage { get; set; }
        public string DamagePercent { get; set; } = "";
        public int Kills { get; set; }
        public long Healed { get; set; }
        public string HealedPercent { get; set; } = "";
        public int CritHeals { get; set; }
        public int Heals { get; set; }
        public int CureDispels { get; set; }
        public long PowerDamage { get; set; }
        public long PowerReplenish { get; set; }
        public double DPS { get; set; }
        public double EncDPS { get; set; }
        public double EncHPS { get; set; }
        public int Hits { get; set; }
        public int CritHits { get; set; }
        public int Blocked { get; set; }
        public int Misses { get; set; }
        public int Swings { get; set; }
        public long HealsTaken { get; set; }
        public long DamageTaken { get; set; }
        public int Deaths { get; set; }
        public float ToHit { get; set; }
        public float CritDamPerc { get; set; }
        public float CritHealPerc { get; set; }

        /// <summary>
        /// Per-category aggregates keyed by category name (e.g. "Skill/Ability (Out)").
        /// Each entry carries both the rollup stats and the per-ability list.
        /// </summary>
        public Dictionary<string, DamageTypeAggregate> DamageTypeAggregates { get; set; }
            = new Dictionary<string, DamageTypeAggregate>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mirrors ACT's DamageTypeData — a per-category rollup (e.g.
    /// "Skill/Ability (Out)") that also contains the per-ability list.
    /// </summary>
    public sealed class DamageTypeAggregate
    {
        public string Type { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public long Damage { get; set; }
        public double EncDPS { get; set; }
        public double CharDPS { get; set; }
        public double DPS { get; set; }
        public double Average { get; set; }
        public long Median { get; set; }
        public long MinHit { get; set; }
        public long MaxHit { get; set; }
        public int Hits { get; set; }
        public int CritHits { get; set; }
        public int Blocked { get; set; }
        public int Misses { get; set; }
        public int Swings { get; set; }
        public float ToHit { get; set; }
        public float AverageDelay { get; set; }
        public float CritPerc { get; set; }

        public List<AttackTypeSnapshot> Attacks { get; set; } = new List<AttackTypeSnapshot>();
    }

    /// <summary>One ability row (e.g. "Cleanse") under a damage-type aggregate.</summary>
    public sealed class AttackTypeSnapshot
    {
        public string Type { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public long Damage { get; set; }
        public double EncDPS { get; set; }
        public double CharDPS { get; set; }
        public double DPS { get; set; }
        public double Average { get; set; }
        public long Median { get; set; }
        public long MinHit { get; set; }
        public long MaxHit { get; set; }
        public string Resist { get; set; } = "";
        public int Hits { get; set; }
        public int CritHits { get; set; }
        public int Blocked { get; set; }
        public int Misses { get; set; }
        public int Swings { get; set; }
        public float ToHit { get; set; }
        public float AverageDelay { get; set; }
        public float CritPerc { get; set; }
    }
}
