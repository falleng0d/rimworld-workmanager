﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using RimWorld;
using Verse;

namespace WorkManager
{
    [UsedImplicitly]
    public class WorkPriorityUpdater : MapComponent
    {
        private readonly HashSet<Pawn> _allPawns = new HashSet<Pawn>();
        private readonly HashSet<WorkTypeDef> _allWorkTypes = new HashSet<WorkTypeDef>();
        private readonly List<string> _logMessages = new List<string>();
        private readonly HashSet<WorkTypeDef> _managedWorkTypes = new HashSet<WorkTypeDef>();
        private readonly Dictionary<Pawn, PawnCache> _pawnCache = new Dictionary<Pawn, PawnCache>();

        private int _currentDay = -1;
        private float _currentTime = -1;

        private Task _updaterTask;

        public WorkPriorityUpdater(Map map) : base(map) { }

        private static WorkManagerGameComponent WorkManager => Current.Game.GetComponent<WorkManagerGameComponent>();

        private void ApplyWorkPriorities()
        {
            foreach (var pawnCache in _pawnCache.Values.Where(pc => pc.IsManaged))
            {
                foreach (var workType in pawnCache.ManagedWorkTypes)
                {
                    pawnCache.Pawn.workSettings.SetPriority(workType, pawnCache.WorkPriorities[workType]);
                }
            }
        }

        private void AssignCommonWork()
        {
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add(
                    $"-- Work Manager: Assigning common work types ({string.Join(", ", Settings.AssignEveryoneWorkTypes.Select(workType => $"{workType.Label}[{workType.Priority}]"))}) --");
            }
            var relevantWorkTypes = Settings.AssignEveryoneWorkTypes.Where(workType => workType.IsWorkTypeLoaded)
                .Select(wt => wt.WorkTypeDef).Intersect(_managedWorkTypes);
            foreach (var workType in relevantWorkTypes)
            {
                foreach (var pawnCache in _pawnCache.Values.Where(pc =>
                    pc.IsManaged && pc.IsCapable && !pc.IsRecovering && pc.IsManagedWork(workType) &&
                    !pc.IsDisabledWork(workType) && !pc.IsBadWork(workType)))
                {
                    pawnCache.WorkPriorities[workType] = Settings.AssignEveryoneWorkTypes
                        .First(wt => wt.WorkTypeDef == workType).Priority;
                }
            }
        }

        private void AssignDedicatedWorkers()
        {
            if (!Settings.UseDedicatedWorkers) { return; }
            var capablePawns = _pawnCache.Values.Where(pc => pc.IsCapable).ToList();
            if (!capablePawns.Any()) { return; }
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("-- Work Manager: Assigning dedicated workers --");
            }
            var workTypes = _allWorkTypes.Intersect(_managedWorkTypes).Where(wt =>
                    Settings.AssignEveryoneWorkTypes.FirstOrDefault(a => a.WorkTypeDef == wt)?.AllowDedicated ?? true)
                .ToList();
            if (Settings.SpecialRulesForDoctors)
            {
                workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                    "Doctor".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
            }
            if (Settings.SpecialRulesForHunters)
            {
                workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                    "Hunting".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
            }
            if (!workTypes.Any()) { return; }
            var targetWorkers = (int) Math.Ceiling((float) capablePawns.Count / workTypes.Count);
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add($"-- Work Manager: Target dedicated workers by work type = {targetWorkers} --");
            }
            foreach (var workType in workTypes.OrderByDescending(wt => wt.relevantSkills.Count)
                .ThenByDescending(wt => wt.naturalPriority))
            {
                var relevantPawns = capablePawns.Where(pc =>
                    !pc.IsRecovering && !pc.IsDisabledWork(workType) && !pc.IsBadWork(workType)).ToList();
                if (!relevantPawns.Any()) { continue; }
                var pawnSkills = relevantPawns.ToDictionary(pc => pc, pc => pc.WorkSkillLevels[workType]);
                var skillRange = pawnSkills.Max(pair => pair.Value) - pawnSkills.Min(pair => pair.Value);
                var pawnLearnRates = relevantPawns.ToDictionary(pc => pc, pc => pc.WorkSkillLearningRates[workType]);
                var learnRateRange = pawnLearnRates.Max(pair => pair.Value) - pawnLearnRates.Min(pair => pair.Value);
                var pawnDedicationsCounts = relevantPawns.ToDictionary(pc => pc,
                    pc => workTypes.Count(wt => pc.WorkPriorities[wt] == 1));
                var dedicationsCountRange = pawnDedicationsCounts.Max(pair => pair.Value) -
                                            pawnDedicationsCounts.Min(pair => pair.Value);
                var pawnScores = new Dictionary<PawnCache, float>();
                foreach (var pawnCache in relevantPawns)
                {
                    var skill = pawnSkills[pawnCache];
                    var normalizedSkill = skillRange == 0 ? 0 : skill / skillRange;
                    var normalizedLearnRate = learnRateRange == 0 ? 0 : pawnLearnRates[pawnCache] / learnRateRange;
                    var normalizedDedications = dedicationsCountRange == 0
                        ? 0
                        : pawnDedicationsCounts[pawnCache] / dedicationsCountRange;
                    var score = (float) normalizedSkill - normalizedDedications;
                    score += skill < 20 ? 0.75f * normalizedLearnRate : 0.25f * normalizedLearnRate;
                    pawnScores.Add(pawnCache, score);
                }
                if (Prefs.DevMode && Settings.VerboseLogging)
                {
                    _logMessages.Add(
                        $"-- Work Manager: {string.Join(", ", pawnScores.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key.Pawn.LabelShort}({pair.Value:N2})"))} --");
                }
                while (capablePawns.Count(pc => pc.WorkPriorities[workType] == 1) < targetWorkers)
                {
                    var dedicatedWorker = pawnScores.Any()
                        ? pawnScores.OrderByDescending(pair => pair.Value).First().Key
                        : null;
                    if (dedicatedWorker == null) { break; }
                    if (Prefs.DevMode && Settings.VerboseLogging)
                    {
                        _logMessages.Add(
                            $"Work Manager: Assigning '{dedicatedWorker.Pawn.LabelShort}' as dedicated worker for '{workType.labelShort}'");
                    }
                    dedicatedWorker.WorkPriorities[workType] = 1;
                    pawnScores.Remove(dedicatedWorker);
                }
            }
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("----------------------------------------------------");
            }
        }

        private void AssignDoctors()
        {
            if (!Settings.SpecialRulesForDoctors) { return; }
            var workType = _allWorkTypes.FirstOrDefault(workTypeDef =>
                "Doctor".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase));
            if (workType == null) { return; }
            if (!WorkManager.GetWorkTypeEnabled(workType)) { return; }
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("-- Work Manager: Assigning doctors... --");
            }
            var doctors = _pawnCache.Values.Where(pc => pc.IsCapable && !pc.IsDisabledWork(workType)).ToList();
            if (!doctors.Any()) { return; }
            var doctorsCount = doctors.Count(pc => pc.IsActiveWork(workType));
            var maxSkillValue = doctors.Max(pc => pc.WorkSkillLevels[workType]);
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add($"Work Manager: Max doctoring skill value = '{maxSkillValue}'");
            }
            var assignEveryone = Settings.AssignEveryoneWorkTypes.FirstOrDefault(wt => wt.WorkTypeDef == workType);
            var managedDoctors = doctors.Where(pc => pc.IsManaged && pc.IsManagedWork(workType))
                .OrderBy(pc => pc.IsBadWork(workType)).ThenByDescending(pc => pc.WorkSkillLevels[workType]).ToList();
            if (assignEveryone == null || assignEveryone.AllowDedicated)
            {
                foreach (var pawnCache in managedDoctors.Where(pc => !pc.IsRecovering))
                {
                    if (pawnCache.WorkSkillLevels[workType] >= maxSkillValue)
                    {
                        if (doctorsCount == 0 || !pawnCache.IsBadWork(workType))
                        {
                            if (Prefs.DevMode && Settings.VerboseLogging)
                            {
                                _logMessages.Add(
                                    $"Work Manager: Assigning '{pawnCache.Pawn.LabelShort}' as primary doctor (highest skill value)");
                            }
                            pawnCache.WorkPriorities[workType] = 1;
                            doctorsCount++;
                            continue;
                        }
                    }
                    if (doctorsCount == 0)
                    {
                        if (Prefs.DevMode && Settings.VerboseLogging)
                        {
                            _logMessages.Add(
                                $"Work Manager: Assigning '{pawnCache.Pawn.LabelShort}' as primary doctor (highest skill value)");
                        }
                        pawnCache.WorkPriorities[workType] = 1;
                        doctorsCount++;
                        break;
                    }
                }
            }
            if (doctorsCount == 0)
            {
                var pawnCache = managedDoctors.FirstOrDefault();
                if (pawnCache != null)
                {
                    if (Prefs.DevMode && Settings.VerboseLogging)
                    {
                        _logMessages.Add(
                            $"Work Manager: Assigning '{pawnCache.Pawn.LabelShort}' as primary doctor (fail-safe)");
                    }
                    pawnCache.WorkPriorities[workType] = assignEveryone == null || assignEveryone.AllowDedicated
                        ? 1
                        : assignEveryone.Priority;
                    doctorsCount++;
                }
            }
            if (doctorsCount == 1)
            {
                var doctor = doctors.First(pc => pc.IsActiveWork(workType));
                if (doctor.Pawn.health.HasHediffsNeedingTend() || doctor.Pawn.health.hediffSet.HasTendableInjury() ||
                    doctor.Pawn.health.hediffSet.HasTendableHediff())
                {
                    foreach (var pawnCache in doctors
                        .Where(pc =>
                            pc.IsManaged && !pc.IsRecovering && pc.IsManagedWork(workType) &&
                            !pc.IsActiveWork(workType)).OrderByDescending(pc => pc.WorkSkillLevels[workType])
                        .ThenBy(pc => pc.IsBadWork(workType)))
                    {
                        if (Prefs.DevMode && Settings.VerboseLogging)
                        {
                            _logMessages.Add(
                                $"Work Manager: Assigning '{pawnCache.Pawn.LabelShort}' as secondary doctor (primary doctor needs tending)");
                        }
                        pawnCache.WorkPriorities[workType] = assignEveryone == null || assignEveryone.AllowDedicated
                            ? 1
                            : assignEveryone.Priority;
                        doctorsCount++;
                        break;
                    }
                }
            }
            if (Settings.AssignMultipleDoctors && (assignEveryone == null || assignEveryone.AllowDedicated))
            {
                var patientCount = 0;
                if (Settings.CountDownedColonists) { patientCount += _allPawns.Count(pawn => pawn.Downed); }
                if (Settings.CountDownedGuests && (map?.IsPlayerHome ?? false))
                {
                    patientCount += map?.mapPawns?.AllPawnsSpawned?.Count(pawn =>
                        pawn?.guest != null && !pawn.IsColonist && !pawn.guest.IsPrisoner && !pawn.IsPrisoner &&
                        pawn.Downed) ?? 0;
                }
                if (Settings.CountDownedPrisoners && (map?.IsPlayerHome ?? false))
                {
                    patientCount += map?.mapPawns?.PrisonersOfColonySpawned?.Count(pawn => pawn.Downed) ?? 0;
                }
                if (Settings.CountDownedAnimals && (map?.IsPlayerHome ?? false))
                {
                    patientCount += map?.mapPawns?.PawnsInFaction(Faction.OfPlayer)?.Where(p => p.RaceProps.Animal)
                        .Count(pawn => pawn.Downed) ?? 0;
                }
                if (Prefs.DevMode && Settings.VerboseLogging)
                {
                    _logMessages.Add($"Work Manager: Patient count = '{patientCount}'");
                }
                while (doctorsCount < patientCount)
                {
                    var pawnCache = doctors
                        .Where(pc =>
                            pc.IsManaged && !pc.IsRecovering && pc.IsManagedWork(workType) &&
                            !pc.IsActiveWork(workType)).OrderByDescending(pc => pc.WorkSkillLevels[workType])
                        .ThenBy(pc => pc.IsBadWork(workType)).FirstOrDefault();
                    if (pawnCache == null) { break; }
                    if (Prefs.DevMode && Settings.VerboseLogging)
                    {
                        _logMessages.Add(
                            $"Work Manager: Assigning '{pawnCache.Pawn.LabelShort}' as backup doctor (multiple patients)");
                    }
                    pawnCache.WorkPriorities[workType] = 1;
                    doctorsCount++;
                }
            }
            if (Prefs.DevMode && Settings.VerboseLogging) { _logMessages.Add("---------------------"); }
        }

        private void AssignHunters()
        {
            if (!Settings.SpecialRulesForHunters) { return; }
            var workType = _allWorkTypes.FirstOrDefault(workTypeDef =>
                "Hunting".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase));
            if (workType == null) { return; }
            if (!_managedWorkTypes.Contains(workType)) { return; }
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("-- Work Manager: Assigning hunters... --");
            }
            var assignEveryone = Settings.AssignEveryoneWorkTypes.FirstOrDefault(wt => wt.WorkTypeDef == workType);
            if (assignEveryone != null)
            {
                foreach (var pawnCache in _pawnCache.Values.Where(pc =>
                    pc.IsCapable && pc.IsManaged && pc.IsManagedWork(workType) && pc.IsActiveWork(workType) &&
                    !pc.IsHunter()))
                {
                    if (Prefs.DevMode && Settings.VerboseLogging)
                    {
                        _logMessages.Add(
                            $"Work Manager: Removing hunting assignment from '{pawnCache.Pawn.LabelShort}' (not a hunter)");
                    }
                    pawnCache.WorkPriorities[workType] = 0;
                }
                if (Prefs.DevMode && Settings.VerboseLogging) { _logMessages.Add("---------------------"); }
            }
            var hunters = _pawnCache.Values.Where(pc => pc.IsCapable && (pc.IsHunter() || pc.IsActiveWork(workType)))
                .ToList();
            var maxSkillValue = hunters.Any() ? hunters.Max(pc => pc.WorkSkillLevels[workType]) : 0;
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add(
                    $"Work Manager: Hunters are {string.Join(", ", hunters.Select(pc => $"{pc.Pawn.LabelShortCap} ({pc.WorkSkillLevels[workType]:N2})"))}");
                _logMessages.Add($"Work Manager: Max hunting skill value = '{maxSkillValue}'");
            }
            if (assignEveryone == null || assignEveryone.AllowDedicated)
            {
                foreach (var pawnCache in hunters
                    .Where(pc =>
                        pc.IsManaged && !pc.IsRecovering && pc.IsManagedWork(workType) && !pc.IsBadWork(workType))
                    .OrderByDescending(pc => pc.WorkSkillLevels[workType]))
                {
                    if (pawnCache.WorkSkillLevels[workType] >= maxSkillValue ||
                        _pawnCache.Values.Count(pc => pc.IsCapable && pc.IsActiveWork(workType)) == 0)
                    {
                        if (Prefs.DevMode && Settings.VerboseLogging)
                        {
                            _logMessages.Add(
                                $"Work Manager: Assigning '{pawnCache.Pawn.LabelShort}' as a hunter with priority 1 (highest skill value)");
                        }
                        pawnCache.WorkPriorities[workType] = 1;
                    }
                    else
                    {
                        if (pawnCache.IsLearningRateAboveThreshold(workType, true))
                        {
                            if (Prefs.DevMode && Settings.VerboseLogging)
                            {
                                _logMessages.Add(
                                    $"Work Manager: Assigning '{pawnCache.Pawn.LabelShort}' as a hunter with priority 2 (major learning rate)");
                            }
                            pawnCache.WorkPriorities[workType] = 2;
                        }
                        else if (pawnCache.IsLearningRateAboveThreshold(workType, false))
                        {
                            if (Prefs.DevMode && Settings.VerboseLogging)
                            {
                                _logMessages.Add(
                                    $"Work Manager: Assigning '{pawnCache.Pawn.LabelShort}' as a hunter with priority 3 (minor learning rate)");
                            }
                            pawnCache.WorkPriorities[workType] = 3;
                        }
                    }
                }
            }
            if (_pawnCache.Values.Count(pc => pc.IsCapable && pc.IsActiveWork(workType)) == 0)
            {
                var pawnCache = _pawnCache.Values
                    .Where(pc =>
                        pc.IsCapable && pc.IsManaged && !pc.IsRecovering && pc.IsManagedWork(workType) &&
                        !pc.IsDisabledWork(workType) && !pc.IsBadWork(workType))
                    .OrderByDescending(pc => pc.WorkSkillLevels[workType]).FirstOrDefault();
                {
                    if (pawnCache != null)
                    {
                        if (Prefs.DevMode && Settings.VerboseLogging)
                        {
                            _logMessages.Add(
                                $"Work Manager: Setting {pawnCache.Pawn.LabelShort}'s priority of '{workType.labelShort}' to {(assignEveryone == null || assignEveryone.AllowDedicated ? 1 : assignEveryone.Priority)} (fail-safe)");
                        }
                        pawnCache.WorkPriorities[workType] = assignEveryone == null || assignEveryone.AllowDedicated
                            ? 1
                            : assignEveryone.Priority;
                    }
                }
            }
            if (Prefs.DevMode && Settings.VerboseLogging) { _logMessages.Add("---------------------"); }
        }

        private void AssignLeftoverWorkTypes()
        {
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("-- Work Manager: Assigning leftover work types... --");
            }
            if (!_pawnCache.Values.Any(pc => pc.IsCapable)) { return; }
            var workTypes = _managedWorkTypes.Where(workType =>
                !Settings.AssignEveryoneWorkTypes.Any(a => a.WorkTypeDef == workType)).ToList();
            if (Settings.SpecialRulesForDoctors)
            {
                workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                    "Doctor".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
            }
            if (Settings.SpecialRulesForHunters)
            {
                workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                    "Hunting".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
            }
            if (!Settings.UseDedicatedWorkers)
            {
                foreach (var workType in workTypes.Where(workType =>
                    !_pawnCache.Values.Where(pc => pc.IsCapable).Any(pc => pc.IsActiveWork(workType))))
                {
                    foreach (var pawnCache in _pawnCache.Values
                        .Where(pc =>
                            pc.IsCapable && pc.IsManaged && !pc.IsRecovering && pc.IsManagedWork(workType) &&
                            !pc.IsDisabledWork(workType) && !pc.IsBadWork(workType))
                        .OrderBy(pc => workTypes.Count(pc.IsActiveWork)))
                    {
                        if (Prefs.DevMode && Settings.VerboseLogging)
                        {
                            _logMessages.Add(
                                $"Work Manager: Setting {pawnCache.Pawn.LabelShort}'s priority of '{workType.labelShort}' to 1");
                        }
                        pawnCache.WorkPriorities[workType] = 1;
                        break;
                    }
                }
                foreach (var pawnCache in _pawnCache.Values.Where(pc =>
                    pc.IsCapable && pc.IsManaged && !pc.IsRecovering && workTypes.Count(pc.IsActiveWork) == 0))
                {
                    var workType = workTypes
                        .Where(wt =>
                            pawnCache.IsManagedWork(wt) && !pawnCache.IsDisabledWork(wt) && !pawnCache.IsBadWork(wt))
                        .OrderBy(wt => _pawnCache.Values.Where(pc => pc.IsCapable).Count(pc => pc.IsActiveWork(wt)))
                        .FirstOrDefault();
                    if (workType != null)
                    {
                        if (Prefs.DevMode && Settings.VerboseLogging)
                        {
                            _logMessages.Add(
                                $"Work Manager: Setting {pawnCache.Pawn.LabelShort}'s priority of '{workType.labelShort}' to 1");
                        }
                        pawnCache.WorkPriorities[workType] = 1;
                    }
                }
            }
            if (Settings.AssignAllWorkTypes)
            {
                foreach (var pawnCache in _pawnCache.Values.Where(
                    pc => pc.IsCapable && pc.IsManaged && !pc.IsRecovering))
                {
                    foreach (var workType in workTypes.Where(wt =>
                        pawnCache.IsManagedWork(wt) && !pawnCache.IsBadWork(wt) && !pawnCache.IsDisabledWork(wt) &&
                        !pawnCache.IsActiveWork(wt)))
                    {
                        if (Prefs.DevMode && Settings.VerboseLogging)
                        {
                            _logMessages.Add(
                                $"Work Manager: Setting {pawnCache.Pawn.LabelShort}'s priority of '{workType.labelShort}' to 4");
                        }
                        pawnCache.WorkPriorities[workType] = 4;
                    }
                }
            }
            if (Prefs.DevMode && Settings.VerboseLogging) { _logMessages.Add("---------------------"); }
        }

        private void AssignWorkersByLearningRate()
        {
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("-- Work Manager: Assigning workers by learning rate... --");
            }
            if (!_pawnCache.Values.Any(pc => pc.IsCapable)) { return; }
            foreach (var pawnCache in _pawnCache.Values.Where(pc => pc.IsCapable && pc.IsManaged && !pc.IsRecovering))
            {
                var workTypes = _managedWorkTypes.Except(Settings.AssignEveryoneWorkTypes.Select(wt => wt.WorkTypeDef))
                    .Where(workType => pawnCache.IsManagedWork(workType) && !pawnCache.IsDisabledWork(workType) &&
                                       !pawnCache.IsBadWork(workType) && !pawnCache.IsActiveWork(workType)).ToList();
                if (Settings.SpecialRulesForDoctors)
                {
                    workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                        "Doctor".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
                }
                if (Settings.SpecialRulesForHunters)
                {
                    workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                        "Hunting".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
                }
                foreach (var workType in workTypes)
                {
                    if (pawnCache.IsLearningRateAboveThreshold(workType, true))
                    {
                        pawnCache.WorkPriorities[workType] = 2;
                        continue;
                    }
                    if (pawnCache.IsLearningRateAboveThreshold(workType, false))
                    {
                        pawnCache.WorkPriorities[workType] = 3;
                    }
                }
            }
        }

        private void AssignWorkersBySkill()
        {
            if (Settings.UseDedicatedWorkers) { return; }
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("-- Work Manager: Assigning workers by skill... --");
            }
            if (!_pawnCache.Values.Any(pc => pc.IsCapable)) { return; }
            var workTypes = _managedWorkTypes.Where(w =>
                !Settings.AssignEveryoneWorkTypes.Any(wt => wt.WorkTypeDef == w) && w.relevantSkills.Any()).ToList();
            if (Settings.SpecialRulesForDoctors)
            {
                workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                    "Doctor".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
            }
            if (Settings.SpecialRulesForHunters)
            {
                workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                    "Hunting".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
            }
            foreach (var workType in workTypes)
            {
                var relevantPawns = _pawnCache.Values.Where(pc => pc.IsCapable && !pc.IsDisabledWork(workType))
                    .ToList();
                if (!relevantPawns.Any()) { continue; }
                var maxSkillValue = relevantPawns.Max(pc => pc.WorkSkillLevels[workType]);
                foreach (var pawnCache in relevantPawns
                    .Where(pc =>
                        pc.IsManaged && !pc.IsRecovering && pc.IsManagedWork(workType) && !pc.IsBadWork(workType))
                    .OrderByDescending(pc => pc.WorkSkillLevels[workType]))
                {
                    if (pawnCache.WorkSkillLevels[workType] >= maxSkillValue || _pawnCache.Values
                        .Where(pc => pc.IsCapable).Count(pc => pc.IsActiveWork(workType)) == 0)
                    {
                        if (Prefs.DevMode && Settings.VerboseLogging)
                        {
                            _logMessages.Add(
                                $"Work Manager: Setting {pawnCache.Pawn.LabelShort}'s priority of '{workType.labelShort}' to 1 (skill = {pawnCache.WorkSkillLevels[workType]}, max = {maxSkillValue})");
                        }
                        pawnCache.WorkPriorities[workType] = 1;
                    }
                }
            }
            if (Prefs.DevMode && Settings.VerboseLogging) { _logMessages.Add("---------------------"); }
        }

        private void AssignWorkForRecoveringPawns()
        {
            if (!Settings.RecoveringPawnsUnfitForWork) { return; }
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("-- Work Manager: Assigning work for recovering pawns --");
            }
            var relevantWorkTypes = _allWorkTypes.Where(wt => new[] {"Patient", "PatientBedRest"}.Contains(wt.defName))
                .Intersect(_managedWorkTypes);
            foreach (var workType in relevantWorkTypes)
            {
                foreach (var pawnCache in _pawnCache.Values.Where(pc =>
                    pc.IsManaged && pc.IsCapable && !pc.IsDisabledWork(workType) && !pc.IsBadWork(workType)))
                {
                    pawnCache.WorkPriorities[workType] = 1;
                }
            }
        }

        private void AssignWorkToIdlePawns()
        {
            if (!Settings.AssignWorkToIdlePawns) { return; }
            if (Prefs.DevMode && Settings.VerboseLogging)
            {
                _logMessages.Add("-- Work Manager: Assigning work for idle pawns... --");
                foreach (var idlePawn in _pawnCache.Values.Where(pc => pc.IdleSince != null))
                {
                    _logMessages.Add(
                        $"{idlePawn.Pawn.LabelShort} is registered as idle ({idlePawn.IdleSince.Day}, {idlePawn.IdleSince.Hour:N1})");
                }
            }
            var noLongerIdlePawns = (from idlePawn in _pawnCache.Values.Where(pc => pc.IdleSince != null)
                let hoursPassed =
                    _currentDay != idlePawn.IdleSince.Day
                        ? 24 + (_currentTime - idlePawn.IdleSince.Hour)
                        : _currentTime - idlePawn.IdleSince.Hour
                where hoursPassed > 12
                select idlePawn).ToList();
            foreach (var pawnCache in noLongerIdlePawns) { pawnCache.IdleSince = null; }
            var idlePawns = _pawnCache.Values.Where(pc =>
                pc.IsCapable && pc.IsManaged && !pc.IsRecovering &&
                (pc.IdleSince != null || !pc.Pawn.Drafted && pc.Pawn.mindState.IsIdle)).ToList();
            if (!idlePawns.Any()) { return; }
            var workTypes = _managedWorkTypes.Where(o =>
                !Settings.AssignEveryoneWorkTypes.Any(wt => wt.WorkTypeDef == o)).ToList();
            if (Settings.SpecialRulesForDoctors)
            {
                workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                    "Doctor".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
            }
            if (Settings.SpecialRulesForHunters)
            {
                workTypes.Remove(_allWorkTypes.FirstOrDefault(workTypeDef =>
                    "Hunting".Equals(workTypeDef.defName, StringComparison.OrdinalIgnoreCase)));
            }
            foreach (var pawnCache in idlePawns)
            {
                foreach (var workType in workTypes.Where(wt =>
                    pawnCache.IsManagedWork(wt) && !pawnCache.IsDisabledWork(wt) && !pawnCache.IsBadWork(wt) &&
                    !pawnCache.IsActiveWork(wt))) { pawnCache.WorkPriorities[workType] = 4; }
                if (pawnCache.IdleSince == null) { pawnCache.IdleSince = new DayTime(_currentDay, _currentTime); }
            }
            if (Prefs.DevMode && Settings.VerboseLogging) { _logMessages.Add("---------------------"); }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (!WorkManager.Enabled) { return; }
            if (_updaterTask != null && _updaterTask.IsCompleted)
            {
                if (Prefs.DevMode && Settings.VerboseLogging)
                {
                    foreach (var message in _logMessages) { Log.Message(message); }
                    Log.Message("----- Work Manager: Applying work priorities... -----");
                }
                _logMessages.Clear();
                ApplyWorkPriorities();
                _updaterTask.Dispose();
                _updaterTask = null;
            }
            if (Find.TickManager.CurTimeSpeed == TimeSpeed.Paused) { return; }
            if ((Find.TickManager.TicksGame + GetHashCode()) % 60 != 0) { return; }
            var day = GenLocalDate.DayOfYear(map);
            var hourFloat = GenLocalDate.HourFloat(map);
            if (Settings.UpdateFrequency != 0 && Math.Abs(day - _currentDay) * 24 + Math.Abs(hourFloat - _currentTime) <
                24f / Settings.UpdateFrequency) { return; }
            if (!Current.Game.playSettings.useWorkPriorities)
            {
                Current.Game.playSettings.useWorkPriorities = true;
                foreach (var pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive.Where(pawn =>
                    pawn.Faction == Faction.OfPlayer)) { pawn.workSettings?.Notify_UseWorkPrioritiesChanged(); }
            }
            if (Settings.AssignEveryoneWorkTypes == null)
            {
                Settings.AssignEveryoneWorkTypes =
                    new List<AssignEveryoneWorkType>(Settings.DefaultAssignEveryoneWorkTypes);
            }
            if (_updaterTask == null)
            {
                if (Prefs.DevMode && Settings.VerboseLogging)
                {
                    Log.Message(
                        $"----- Work Manager: Updating work priorities... (day = {day}, hour = {hourFloat}) -----");
                }
                _currentDay = day;
                _currentTime = hourFloat;
                _updaterTask = Task.Run(UpdateWorkPriorities);
                if (Prefs.DevMode && Settings.VerboseLogging)
                {
                    Log.Message("----------------------------------------------------");
                }
            }
        }

        private void UpdateCache()
        {
            if (!_allWorkTypes.Any())
            {
                _allWorkTypes.AddRange(DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible));
            }
            _managedWorkTypes.Clear();
            _managedWorkTypes.AddRange(_allWorkTypes.Where(w => WorkManager.GetWorkTypeEnabled(w)));
            _allPawns.Clear();
            _allPawns.AddRange(map.mapPawns.FreeColonistsSpawned);
            foreach (var pawn in _pawnCache.Keys.Where(pawn => !_allPawns.Contains(pawn)).ToList())
            {
                _pawnCache.Remove(pawn);
            }
            foreach (var pawn in _allPawns)
            {
                if (!_pawnCache.ContainsKey(pawn)) { _pawnCache.Add(pawn, new PawnCache(pawn)); }
                UpdatePawnCache(pawn);
            }
        }

        private void UpdatePawnCache(Pawn pawn)
        {
            var cache = _pawnCache[pawn];
            cache.IsCapable = !pawn.Dead && !pawn.Downed && !pawn.InMentalState;
            cache.IsRecovering = Settings.RecoveringPawnsUnfitForWork && HealthAIUtility.ShouldSeekMedicalRest(pawn);
            cache.IsManaged = WorkManager.GetPawnEnabled(pawn);
            cache.DisabledWorkTypes.Clear();
            cache.DisabledWorkTypes.AddRange(pawn.GetDisabledWorkTypes());
            if (Settings.IsBadWorkMethod != null)
            {
                cache.BadWorkTypes.Clear();
                cache.BadWorkTypes.AddRange(_allWorkTypes.Where(workType =>
                    (bool) Settings.IsBadWorkMethod.Invoke(null, new object[] {pawn, workType})));
            }
            cache.SkillLearningRates.Clear();
            foreach (var skill in DefDatabase<SkillDef>.AllDefsListForReading)
            {
                cache.SkillLearningRates.Add(skill, cache.Pawn.skills.GetSkill(skill).LearnRateFactor());
            }
            cache.WorkSkillLevels.Clear();
            cache.WorkSkillLearningRates.Clear();
            foreach (var workType in _allWorkTypes)
            {
                if (workType.relevantSkills.Any())
                {
                    cache.WorkSkillLevels.Add(workType,
                        (int) Math.Floor(workType.relevantSkills.Select(skill => pawn.skills.GetSkill(skill).Level)
                            .Average()));
                    cache.WorkSkillLearningRates.Add(workType,
                        workType.relevantSkills.Select(skill => cache.SkillLearningRates[skill]).Average());
                }
                else
                {
                    cache.WorkSkillLevels.Add(workType, 0);
                    cache.WorkSkillLearningRates.Add(workType, 0);
                }
            }
            cache.WorkPriorities.Clear();
            cache.ManagedWorkTypes.Clear();
            foreach (var workType in _allWorkTypes)
            {
                if (cache.IsManaged && _managedWorkTypes.Contains(workType) &&
                    WorkManager.GetPawnWorkTypeEnabled(pawn, workType))
                {
                    cache.ManagedWorkTypes.Add(workType);
                    cache.WorkPriorities.Add(workType, 0);
                }
                else { cache.WorkPriorities.Add(workType, pawn.workSettings.GetPriority(workType)); }
            }
        }

        private void UpdateWorkPriorities()
        {
            try
            {
                UpdateCache();
                AssignWorkForRecoveringPawns();
                AssignCommonWork();
                AssignDoctors();
                AssignHunters();
                AssignDedicatedWorkers();
                AssignWorkersBySkill();
                AssignWorkersByLearningRate();
                AssignLeftoverWorkTypes();
                AssignWorkToIdlePawns();
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"-- Work Manager: An error has occurred: {exception.Message} --\n{exception.StackTrace}\n{string.Join("\n", _logMessages)}",
                    true);
                _logMessages.Clear();
            }
        }
    }
}