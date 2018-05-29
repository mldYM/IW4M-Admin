﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SharedLibraryCore;
using SharedLibraryCore.Helpers;
using SharedLibraryCore.Interfaces;
using SharedLibraryCore.Objects;
using SharedLibraryCore.Commands;
using IW4MAdmin.Plugins.Stats.Models;
using System.Text.RegularExpressions;
using IW4MAdmin.Plugins.Stats.Web.Dtos;
using SharedLibraryCore.Database;
using Microsoft.EntityFrameworkCore;

namespace IW4MAdmin.Plugins.Stats.Helpers
{
    public class StatManager
    {
        private ConcurrentDictionary<int, ServerStats> Servers;
        private ConcurrentDictionary<int, ThreadSafeStatsService> ContextThreads;
        private ILogger Log;
        private IManager Manager;

        public StatManager(IManager mgr)
        {
            Servers = new ConcurrentDictionary<int, ServerStats>();
            ContextThreads = new ConcurrentDictionary<int, ThreadSafeStatsService>();
            Log = mgr.GetLogger();
            Manager = mgr;
        }

        ~StatManager()
        {
            Servers.Clear();
            Log = null;
            Servers = null;
        }

        public EFClientStatistics GetClientStats(int clientId, int serverId) => Servers[serverId].PlayerStats[clientId];

        public async Task<List<TopStatsInfo>> GetTopStats(int start, int count)
        {
            using (var context = new DatabaseContext())
            {
                context.ChangeTracker.AutoDetectChangesEnabled = false;
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var thirtyDaysAgo = DateTime.UtcNow.AddMonths(-1);
                var iqClientIds = (from stat in context.Set<EFClientStatistics>()
                                   join client in context.Clients
                                   on stat.ClientId equals client.ClientId
#if !DEBUG
                                   where stat.TimePlayed >= 3600
                                   where client.Level != Player.Permission.Banned
                                   where client.LastConnection >= thirtyDaysAgo
                                   where stat.Performance > 60
#endif
                                   group stat by stat.ClientId into s
                                   orderby s.Average(cs => cs.Performance) descending
                                   select s.First().ClientId)
                              .Skip(start)
                              .Take(count);

                var clientIds = await iqClientIds.ToListAsync();

                var iqStats = (from stat in context.Set<EFClientStatistics>()
                               join client in context.Clients
                               on stat.ClientId equals client.ClientId
                               where clientIds.Contains(client.ClientId)
                               select new
                               {
                                   client.CurrentAlias.Name,
                                   client.ClientId,
                                   stat.Kills,
                                   stat.Deaths,
                                   stat.EloRating,
                                   stat.Skill,
                                   stat.TimePlayed,
                                   client.LastConnection,
                                   client.TotalConnectionTime
                               });

                var stats = await iqStats.ToListAsync();

                var groupedSelection = stats.GroupBy(s => s.ClientId).Select(s =>
                new TopStatsInfo()
                {
                    Name = s.Select(c => c.Name).FirstOrDefault(),
                    // weighted based on time played
                    Performance = Math.Round
                                   (s
                                        .Where(c => (c.Skill + c.EloRating) / 2.0 > 0)
                                        .Sum(c => (c.Skill + c.EloRating) / 2.0 * c.TimePlayed) /
                                    s.Where(c => (c.Skill + c.EloRating) / 2.0 > 0)
                                        .Sum(c => c.TimePlayed), 2),
                    // ditto
                    KDR = Math.Round(s
                                        .Where(c => c.Deaths > 0)
                                        .Sum(c => ((c.Kills / (double)c.Deaths) * c.TimePlayed) /
                                   s.Where(d => d.Deaths > 0)
                                        .Sum(d => d.TimePlayed)), 2),
                    ClientId = s.Select(c => c.ClientId).FirstOrDefault(),
                    Deaths = s.Sum(cs => cs.Deaths),
                    Kills = s.Sum(cs => cs.Kills),
                    LastSeen = Utilities.GetTimePassed(s.First().LastConnection, false),
                    TimePlayed = Math.Round(s.First().TotalConnectionTime / 3600.0, 1).ToString("#,##0"),
                });

                var statList = groupedSelection.OrderByDescending(s => s.Performance).ToList();

                // set the ranking numerically
                int i = start + 1;
                foreach (var stat in statList)
                {
                    stat.Ranking = i;
                    i++;
                }

                return statList;
            }
        }

        /// <summary>
        /// Add a server to the StatManager server pool
        /// </summary>
        /// <param name="sv"></param>
        public void AddServer(Server sv)
        {
            try
            {
                int serverId = sv.GetHashCode();
                var statsSvc = new ThreadSafeStatsService();
                ContextThreads.TryAdd(serverId, statsSvc);

                // get the server from the database if it exists, otherwise create and insert a new one
                var server = statsSvc.ServerSvc.Find(c => c.ServerId == serverId).FirstOrDefault();
                if (server == null)
                {
                    server = new EFServer()
                    {
                        Port = sv.GetPort(),
                        Active = true,
                        ServerId = serverId
                    };

                    statsSvc.ServerSvc.Insert(server);
                }

                // this doesn't need to be async as it's during initialization
                statsSvc.ServerSvc.SaveChanges();
                // check to see if the stats have ever been initialized
                InitializeServerStats(sv);
                statsSvc.ServerStatsSvc.SaveChanges();

                var serverStats = statsSvc.ServerStatsSvc.Find(c => c.ServerId == serverId).FirstOrDefault();
                Servers.TryAdd(serverId, new ServerStats(server, serverStats)
                {
                    IsTeamBased = sv.Gametype != "dm"
                });
            }

            catch (Exception e)
            {
                Log.WriteError($"{Utilities.CurrentLocalization.LocalizationIndex["PLUGIN_STATS_ERROR_ADD"]} - {e.Message}");
            }
        }

        /// <summary>
        /// Add Player to the player stats 
        /// </summary>
        /// <param name="pl">Player to add/retrieve stats for</param>
        /// <returns>EFClientStatistic of specified player</returns>
        public async Task<EFClientStatistics> AddPlayer(Player pl)
        {
            int serverId = pl.CurrentServer.GetHashCode();

            if (!Servers.ContainsKey(serverId))
            {
                Log.WriteError($"[Stats::AddPlayer] Server with id {serverId} could not be found");
                return null;
            }

            var playerStats = Servers[serverId].PlayerStats;
            var statsSvc = ContextThreads[serverId];
            var detectionStats = Servers[serverId].PlayerDetections;

            if (playerStats.ContainsKey(pl.ClientId))
            {
                Log.WriteWarning($"Duplicate ClientId in stats {pl.ClientId}");
                return null;
            }

            // get the client's stats from the database if it exists, otherwise create and attach a new one
            // if this fails we want to throw an exception
            var clientStatsSvc = statsSvc.ClientStatSvc;
            var clientStats = clientStatsSvc.Find(c => c.ClientId == pl.ClientId && c.ServerId == serverId).FirstOrDefault();

            if (clientStats == null)
            {
                clientStats = new EFClientStatistics()
                {
                    Active = true,
                    ClientId = pl.ClientId,
                    Deaths = 0,
                    Kills = 0,
                    ServerId = serverId,
                    Skill = 0.0,
                    SPM = 0.0,
                    EloRating = 200.0,
                    HitLocations = Enum.GetValues(typeof(IW4Info.HitLocation)).OfType<IW4Info.HitLocation>().Select(hl => new EFHitLocationCount()
                    {
                        Active = true,
                        HitCount = 0,
                        Location = hl
                    })
                    .ToList()
                };

                // insert if they've not been added
                clientStats = clientStatsSvc.Insert(clientStats);
                await clientStatsSvc.SaveChangesAsync();
            }

            // migration for previous existing stats
            if (clientStats.HitLocations.Count == 0)
            {
                clientStats.HitLocations = Enum.GetValues(typeof(IW4Info.HitLocation)).OfType<IW4Info.HitLocation>().Select(hl => new EFHitLocationCount()
                {
                    Active = true,
                    HitCount = 0,
                    Location = hl
                })
                .ToList();
                //await statsSvc.ClientStatSvc.SaveChangesAsync();
            }

            // for stats before rating
            if (clientStats.EloRating == 0.0)
            {
                clientStats.EloRating = clientStats.Skill;
            }

            if (clientStats.RollingWeightedKDR == 0)
            {
                clientStats.RollingWeightedKDR = clientStats.KDR;
            }

            // set these on connecting
            clientStats.LastActive = DateTime.UtcNow;
            clientStats.LastStatCalculation = DateTime.UtcNow;
            clientStats.SessionScore = pl.Score;

            Log.WriteInfo($"Adding {pl} to stats");

            if (!playerStats.TryAdd(pl.ClientId, clientStats))
                Log.WriteDebug($"Could not add client to stats {pl}");

            if (!detectionStats.TryAdd(pl.ClientId, new Cheat.Detection(Log, clientStats)))
                Log.WriteDebug("Could not add client to detection");

            return clientStats;
        }

        /// <summary>
        /// Perform stat updates for disconnecting client
        /// </summary>
        /// <param name="pl">Disconnecting client</param>
        /// <returns></returns>
        public async Task RemovePlayer(Player pl)
        {
            Log.WriteInfo($"Removing {pl} from stats");

            int serverId = pl.CurrentServer.GetHashCode();
            var playerStats = Servers[serverId].PlayerStats;
            var detectionStats = Servers[serverId].PlayerDetections;
            var serverStats = Servers[serverId].ServerStatistics;
            var statsSvc = ContextThreads[serverId];

            if (!playerStats.ContainsKey(pl.ClientId))
            {
                Log.WriteWarning($"Client disconnecting not in stats {pl}");
                // remove the client from the stats dictionary as they're leaving
                playerStats.TryRemove(pl.ClientId, out EFClientStatistics removedValue1);
                detectionStats.TryRemove(pl.ClientId, out Cheat.Detection removedValue2);
                return;
            }

            // get individual client's stats
            var clientStats = playerStats[pl.ClientId];

            // remove the client from the stats dictionary as they're leaving
            playerStats.TryRemove(pl.ClientId, out EFClientStatistics removedValue3);
            detectionStats.TryRemove(pl.ClientId, out Cheat.Detection removedValue4);

            // sync their stats before they leave
            var clientStatsSvc = statsSvc.ClientStatSvc;
            clientStats = UpdateStats(clientStats);
            clientStatsSvc.Update(clientStats);
            await clientStatsSvc.SaveChangesAsync();

            // increment the total play time
            serverStats.TotalPlayTime += (int)(DateTime.UtcNow - pl.LastConnection).TotalSeconds;
        }

        public void AddDamageEvent(string eventLine, int attackerClientId, int victimClientId, int serverId)
        {
            string regex = @"^(D);(.+);([0-9]+);(allies|axis);(.+);([0-9]+);(allies|axis);(.+);(.+);([0-9]+);(.+);(.+)$";
            var match = Regex.Match(eventLine, regex, RegexOptions.IgnoreCase);

            if (match.Success)
            {
                // this gives us what time the player is on
                var attackerStats = Servers[serverId].PlayerStats[attackerClientId];
                var victimStats = Servers[serverId].PlayerStats[victimClientId];
                IW4Info.Team victimTeam = (IW4Info.Team)Enum.Parse(typeof(IW4Info.Team), match.Groups[4].ToString());
                IW4Info.Team attackerTeam = (IW4Info.Team)Enum.Parse(typeof(IW4Info.Team), match.Groups[7].ToString());
                attackerStats.Team = attackerTeam;
                victimStats.Team = victimTeam;
            }
        }

        /// <summary>
        /// Process stats for kill event
        /// </summary>
        /// <returns></returns>
        public async Task AddScriptHit(bool isDamage, DateTime time, Player attacker, Player victim, int serverId, string map, string hitLoc, string type,
            string damage, string weapon, string killOrigin, string deathOrigin, string viewAngles, string offset, string isKillstreakKill, string Ads, string snapAngles)
        {
            var statsSvc = ContextThreads[serverId];
            Vector3 vDeathOrigin = null;
            Vector3 vKillOrigin = null;
            Vector3 vViewAngles = null;

            try
            {
                vDeathOrigin = Vector3.Parse(deathOrigin);
                vKillOrigin = Vector3.Parse(killOrigin);
                vViewAngles = Vector3.Parse(viewAngles).FixIW4Angles();
            }

            catch (FormatException)
            {
                Log.WriteWarning("Could not parse kill or death origin or viewangle vectors");
                Log.WriteDebug($"Kill - {killOrigin} Death - {deathOrigin} ViewAngle - {viewAngles}");
                await AddStandardKill(attacker, victim);
                return;
            }

            var snapshotAngles = new List<Vector3>();

            try
            {
                foreach (string angle in snapAngles.Split(':', StringSplitOptions.RemoveEmptyEntries))
                {
                    snapshotAngles.Add(Vector3.Parse(angle).FixIW4Angles());
                }
            }

            catch (FormatException)
            {
                Log.WriteWarning("Could not parse snapshot angles");
                return;
            }

            var kill = new EFClientKill()
            {
                Active = true,
                AttackerId = attacker.ClientId,
                VictimId = victim.ClientId,
                ServerId = serverId,
                Map = ParseEnum<IW4Info.MapName>.Get(map, typeof(IW4Info.MapName)),
                DeathOrigin = vDeathOrigin,
                KillOrigin = vKillOrigin,
                DeathType = ParseEnum<IW4Info.MeansOfDeath>.Get(type, typeof(IW4Info.MeansOfDeath)),
                Damage = Int32.Parse(damage),
                HitLoc = ParseEnum<IW4Info.HitLocation>.Get(hitLoc, typeof(IW4Info.HitLocation)),
                Weapon = ParseEnum<IW4Info.WeaponName>.Get(weapon, typeof(IW4Info.WeaponName)),
                ViewAngles = vViewAngles,
                TimeOffset = Int64.Parse(offset),
                When = time,
                IsKillstreakKill = isKillstreakKill[0] != '0',
                AdsPercent = float.Parse(Ads),
                AnglesList = snapshotAngles
            };

            if (kill.DeathType == IW4Info.MeansOfDeath.MOD_SUICIDE &&
                kill.Damage == 100000)
            {
                // suicide by switching teams so let's not count it against them
                return;
            }

            if (!isDamage)
            {
                await AddStandardKill(attacker, victim);
            }

            if (kill.IsKillstreakKill)
            {
                return;
            }

            var clientDetection = Servers[serverId].PlayerDetections[attacker.ClientId];
            var clientStats = Servers[serverId].PlayerStats[attacker.ClientId];
            var clientStatsSvc = statsSvc.ClientStatSvc;
            clientStatsSvc.Update(clientStats);

            // increment their hit count
            if (kill.DeathType == IW4Info.MeansOfDeath.MOD_PISTOL_BULLET ||
                kill.DeathType == IW4Info.MeansOfDeath.MOD_RIFLE_BULLET ||
                kill.DeathType == IW4Info.MeansOfDeath.MOD_HEAD_SHOT)
            {
                clientStats.HitLocations.Single(hl => hl.Location == kill.HitLoc).HitCount += 1;

                //statsSvc.ClientStatSvc.Update(clientStats);
                // await statsSvc.ClientStatSvc.SaveChangesAsync();
            }

            //statsSvc.KillStatsSvc.Insert(kill);
            //await statsSvc.KillStatsSvc.SaveChangesAsync();

            if (Plugin.Config.Configuration().EnableAntiCheat)
            {
                async Task executePenalty(Cheat.DetectionPenaltyResult penalty)
                {
                    // prevent multiple bans from occuring
                    if (attacker.Level == Player.Permission.Banned)
                    {
                        return;
                    }

                    switch (penalty.ClientPenalty)
                    {
                        case Penalty.PenaltyType.Ban:
                            await attacker.Ban(Utilities.CurrentLocalization.LocalizationIndex["PLUGIN_STATS_CHEAT_DETECTED"], new Player()
                            {
                                ClientId = 1
                            });
                            break;
                        case Penalty.PenaltyType.Flag:
                            if (attacker.Level != Player.Permission.User)
                                break;
                            var e = new GameEvent()
                            {
                                Data = penalty.Type == Cheat.Detection.DetectionType.Bone ?
                                    $"{penalty.Type}-{(int)penalty.Location}-{Math.Round(penalty.Value, 2)}@{penalty.HitCount}" :
                                    $"{penalty.Type}-{Math.Round(penalty.Value, 2)}@{penalty.HitCount}",
                                Origin = new Player()
                                {
                                    ClientId = 1,
                                    Level = Player.Permission.Console,
                                    ClientNumber = -1,
                                    CurrentServer = attacker.CurrentServer
                                },
                                Target = attacker,
                                Owner = attacker.CurrentServer,
                                Type = GameEvent.EventType.Flag
                            };
                            await new CFlag().ExecuteAsync(e);
                            break;
                    }
                }

                await executePenalty(clientDetection.ProcessKill(kill, isDamage));
                await executePenalty(clientDetection.ProcessTotalRatio(clientStats));

                await clientStatsSvc.SaveChangesAsync();
            }
        }

        public async Task AddStandardKill(Player attacker, Player victim)
        {
            int serverId = attacker.CurrentServer.GetHashCode();
            EFClientStatistics attackerStats = null;
            try
            {
                attackerStats = Servers[serverId].PlayerStats[attacker.ClientId];
            }

            catch (KeyNotFoundException)
            {
                // happens when the client has disconnected before the last status update
                Log.WriteWarning($"[Stats::AddStandardKill] kill attacker ClientId is invalid {attacker.ClientId}-{attacker}");
                return;
            }

            EFClientStatistics victimStats = null;
            try
            {
                victimStats = Servers[serverId].PlayerStats[victim.ClientId];
            }

            catch (KeyNotFoundException)
            {
                Log.WriteWarning($"[Stats::AddStandardKill] kill victim ClientId is invalid {victim.ClientId}-{victim}");
                return;
            }

#if DEBUG
            Log.WriteDebug("Calculating standard kill");
#endif

            // update the total stats
            Servers[serverId].ServerStatistics.TotalKills += 1;

            // this happens when the round has changed
            if (attackerStats.SessionScore == 0)
                attackerStats.LastScore = 0;

            if (victimStats.SessionScore == 0)
                victimStats.LastScore = 0;

            attackerStats.SessionScore = attacker.Score;
            victimStats.SessionScore = victim.Score;

            // calculate for the clients
            CalculateKill(attackerStats, victimStats);
            // this should fix the negative SPM
            // updates their last score after being calculated
            attackerStats.LastScore = attacker.Score;
            victimStats.LastScore = victim.Score;

            // show encouragement/discouragement
            string streakMessage = (attackerStats.ClientId != victimStats.ClientId) ?
                StreakMessage.MessageOnStreak(attackerStats.KillStreak, attackerStats.DeathStreak) :
                StreakMessage.MessageOnStreak(-1, -1);

            if (streakMessage != string.Empty)
                await attacker.Tell(streakMessage);

            // fixme: why?
            if (double.IsNaN(victimStats.SPM) || double.IsNaN(victimStats.Skill))
            {
                Log.WriteDebug($"[StatManager::AddStandardKill] victim SPM/SKILL {victimStats.SPM} {victimStats.Skill}");
                victimStats.SPM = 0.0;
                victimStats.Skill = 0.0;
            }

            if (double.IsNaN(attackerStats.SPM) || double.IsNaN(attackerStats.Skill))
            {
                Log.WriteDebug($"[StatManager::AddStandardKill] attacker SPM/SKILL {victimStats.SPM} {victimStats.Skill}");
                attackerStats.SPM = 0.0;
                attackerStats.Skill = 0.0;
            }

            // todo: do we want to save this immediately?
            var clientStatsSvc = ContextThreads[serverId].ClientStatSvc;
            clientStatsSvc.Update(attackerStats);
            clientStatsSvc.Update(victimStats);
            await clientStatsSvc.SaveChangesAsync();
        }

        /// <summary>
        /// Performs the incrementation of kills and deaths for client statistics
        /// </summary>
        /// <param name="attackerStats">Stats of the attacker</param>
        /// <param name="victimStats">Stats of the victim</param>
        public void CalculateKill(EFClientStatistics attackerStats, EFClientStatistics victimStats)
        {
            bool suicide = attackerStats.ClientId == victimStats.ClientId;

            // only update their kills if they didn't kill themselves
            if (!suicide)
            {
                attackerStats.Kills += 1;
                attackerStats.SessionKills += 1;
                attackerStats.KillStreak += 1;
                attackerStats.DeathStreak = 0;
            }

            victimStats.Deaths += 1;
            victimStats.SessionDeaths += 1;
            victimStats.DeathStreak += 1;
            victimStats.KillStreak = 0;

            // process the attacker's stats after the kills
            attackerStats = UpdateStats(attackerStats);

            // calulate elo
            if (Servers[attackerStats.ServerId].PlayerStats.Count > 1)
            {
                /* var validAttackerLobbyRatings = Servers[attackerStats.ServerId].PlayerStats
                     .Where(cs => cs.Value.ClientId != attackerStats.ClientId)
                     .Where(cs =>
                         Servers[attackerStats.ServerId].IsTeamBased ?
                         cs.Value.Team != attackerStats.Team :
                         cs.Value.Team != IW4Info.Team.Spectator)
                     .Where(cs => cs.Value.Team != IW4Info.Team.Spectator);

                 double attackerLobbyRating = validAttackerLobbyRatings.Count() > 0 ?
                     validAttackerLobbyRatings.Average(cs => cs.Value.EloRating) :
                     attackerStats.EloRating;

                 var validVictimLobbyRatings = Servers[victimStats.ServerId].PlayerStats
                     .Where(cs => cs.Value.ClientId != victimStats.ClientId)
                     .Where(cs =>
                         Servers[attackerStats.ServerId].IsTeamBased ?
                         cs.Value.Team != victimStats.Team :
                         cs.Value.Team != IW4Info.Team.Spectator)
                      .Where(cs => cs.Value.Team != IW4Info.Team.Spectator);

                 double victimLobbyRating = validVictimLobbyRatings.Count() > 0 ?
                     validVictimLobbyRatings.Average(cs => cs.Value.EloRating) :
                     victimStats.EloRating;*/

                double attackerEloDifference = Math.Log(Math.Max(1, victimStats.EloRating)) - Math.Log(Math.Max(1, attackerStats.EloRating));
                double winPercentage = 1.0 / (1 + Math.Pow(10, attackerEloDifference / Math.E));

                // double victimEloDifference = Math.Log(Math.Max(1, attackerStats.EloRating)) - Math.Log(Math.Max(1, victimStats.EloRating));
                // double lossPercentage = 1.0 / (1 + Math.Pow(10, victimEloDifference/ Math.E));

                attackerStats.EloRating += 6.0 * (1 - winPercentage);
                victimStats.EloRating -= 6.0 * (1 - winPercentage);

                attackerStats.EloRating = Math.Max(0, Math.Round(attackerStats.EloRating, 2));
                victimStats.EloRating = Math.Max(0, Math.Round(victimStats.EloRating, 2));
            }

            // update after calculation
            attackerStats.TimePlayed += (int)(DateTime.UtcNow - attackerStats.LastActive).TotalSeconds;
            victimStats.TimePlayed += (int)(DateTime.UtcNow - victimStats.LastActive).TotalSeconds;
            attackerStats.LastActive = DateTime.UtcNow;
            victimStats.LastActive = DateTime.UtcNow;
        }

        /// <summary>
        /// Update the client stats (skill etc)
        /// </summary>
        /// <param name="clientStats">Client statistics</param>
        /// <returns></returns>
        private EFClientStatistics UpdateStats(EFClientStatistics clientStats)
        {
            // prevent NaN or inactive time lowering SPM
            if ((DateTime.UtcNow - clientStats.LastStatCalculation).TotalSeconds / 60.0 < 0.01 ||
                (DateTime.UtcNow - clientStats.LastActive).TotalSeconds / 60.0 > 3 ||
                clientStats.SessionScore == 0)
            {
                // prevents idle time counting
                clientStats.LastStatCalculation = DateTime.UtcNow;
                return clientStats;
            }

            double timeSinceLastCalc = (DateTime.UtcNow - clientStats.LastStatCalculation).TotalSeconds / 60.0;
            double timeSinceLastActive = (DateTime.UtcNow - clientStats.LastActive).TotalSeconds / 60.0;

            int scoreDifference = 0;
            // this means they've been tking or suicide and is the only time they can have a negative SPM
            if (clientStats.RoundScore < 0)
            {
                scoreDifference = clientStats.RoundScore + clientStats.LastScore;
            }

            else if (clientStats.RoundScore > 0 && clientStats.LastScore < clientStats.RoundScore)
            {
                scoreDifference = clientStats.RoundScore - clientStats.LastScore;
            }

            double killSPM = scoreDifference / timeSinceLastCalc;
            double spmMultiplier = 2.934 * Math.Pow(Servers[clientStats.ServerId].TeamCount(clientStats.Team == IW4Info.Team.Allies ? IW4Info.Team.Axis : IW4Info.Team.Allies), -0.454);
            killSPM *= Math.Max(1, spmMultiplier);

            // calculate how much the KDR should weigh
            // 1.637 is a Eddie-Generated number that weights the KDR nicely
            double currentKDR = clientStats.SessionDeaths == 0 ? clientStats.SessionKills : clientStats.SessionKills / clientStats.SessionDeaths;
            double alpha = Math.Sqrt(2) / Math.Min(600, clientStats.Kills + clientStats.Deaths);
            clientStats.RollingWeightedKDR = (alpha * currentKDR) + (1.0 - alpha) * clientStats.KDR;
            double KDRWeight = Math.Round(Math.Pow(clientStats.RollingWeightedKDR, 1.637 / Math.E), 3);

            // calculate the weight of the new play time against last 10 hours of gameplay
            int totalPlayTime = (clientStats.TimePlayed == 0) ?
                (int)(DateTime.UtcNow - clientStats.LastActive).TotalSeconds :
                clientStats.TimePlayed + (int)(DateTime.UtcNow - clientStats.LastActive).TotalSeconds;

            double SPMAgainstPlayWeight = timeSinceLastCalc / Math.Min(600, (totalPlayTime / 60.0));

            // calculate the new weight against average times the weight against play time
            clientStats.SPM = (killSPM * SPMAgainstPlayWeight) + (clientStats.SPM * (1 - SPMAgainstPlayWeight));

            if (clientStats.SPM < 0)
            {
                Log.WriteWarning("[StatManager:UpdateStats] clientStats SPM < 0");
                Log.WriteDebug($"{scoreDifference}-{clientStats.RoundScore} - {clientStats.LastScore} - {clientStats.SessionScore}");
                clientStats.SPM = 0;
            }

            clientStats.SPM = Math.Round(clientStats.SPM, 3);
            clientStats.Skill = Math.Round((clientStats.SPM * KDRWeight), 3);

            // fixme: how does this happen?
            if (double.IsNaN(clientStats.SPM) || double.IsNaN(clientStats.Skill))
            {
                Log.WriteWarning("[StatManager::UpdateStats] clientStats SPM/Skill NaN");
                Log.WriteDebug($"{killSPM}-{KDRWeight}-{totalPlayTime}-{SPMAgainstPlayWeight}-{clientStats.SPM}-{clientStats.Skill}-{scoreDifference}");
                clientStats.SPM = 0;
                clientStats.Skill = 0;
            }

            clientStats.LastStatCalculation = DateTime.UtcNow;
            //clientStats.LastScore = clientStats.SessionScore;

            return clientStats;
        }

        public void InitializeServerStats(Server sv)
        {
            int serverId = sv.GetHashCode();
            var statsSvc = ContextThreads[serverId];

            var serverStats = statsSvc.ServerStatsSvc.Find(s => s.ServerId == serverId).FirstOrDefault();
            if (serverStats == null)
            {
                Log.WriteDebug($"Initializing server stats for {sv}");
                // server stats have never been generated before
                serverStats = new EFServerStatistics()
                {
                    Active = true,
                    ServerId = serverId,
                    TotalKills = 0,
                    TotalPlayTime = 0,
                };

                var ieClientStats = statsSvc.ClientStatSvc.Find(cs => cs.ServerId == serverId);

                // set these incase we've imported settings 
                serverStats.TotalKills = ieClientStats.Sum(cs => cs.Kills);
                serverStats.TotalPlayTime = Manager.GetClientService().GetTotalPlayTime().Result;

                statsSvc.ServerStatsSvc.Insert(serverStats);
            }
        }

        public void ResetKillstreaks(int serverId)
        {
            var serverStats = Servers[serverId];
            foreach (var stat in serverStats.PlayerStats.Values)
                stat.StartNewSession();
        }

        public void ResetStats(int clientId, int serverId)
        {
            var stats = Servers[serverId].PlayerStats[clientId];
            stats.Kills = 0;
            stats.Deaths = 0;
            stats.SPM = 0;
            stats.Skill = 0;
            stats.TimePlayed = 0;
            stats.EloRating = 200;
        }

        public async Task AddMessageAsync(int clientId, int serverId, string message)
        {
            // the web users can have no account
            if (clientId < 1)
                return;

            var messageSvc = ContextThreads[serverId].MessageSvc;
            messageSvc.Insert(new EFClientMessage()
            {
                Active = true,
                ClientId = clientId,
                Message = message,
                ServerId = serverId,
                TimeSent = DateTime.UtcNow
            });
            await messageSvc.SaveChangesAsync();
        }

        public async Task Sync(Server sv)
        {
            int serverId = sv.GetHashCode();
            var statsSvc = ContextThreads[serverId];

            Log.WriteDebug("Syncing stats contexts");
            await statsSvc.ServerStatsSvc.SaveChangesAsync();
            //await statsSvc.ClientStatSvc.SaveChangesAsync();
            await statsSvc.KillStatsSvc.SaveChangesAsync();
            await statsSvc.ServerSvc.SaveChangesAsync();

            statsSvc = null;
            // this should prevent the gunk for having a long lasting context.
            ContextThreads[serverId] = new ThreadSafeStatsService();
        }

        public void SetTeamBased(int serverId, bool isTeamBased)
        {
            Servers[serverId].IsTeamBased = isTeamBased;
        }
    }
}