using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CounterStrikeSharp.API;
using static CounterStrikeSharp.API.Server;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules;
using CounterStrikeSharp.API.Modules.Listeners;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;
using static CounterStrikeSharp.API.Core.Listeners;

namespace TeamAssignmentPlugin
{
    public class TeamAssignmentPlugin : BasePlugin
    {
        public override string ModuleName => "Team Assignment Plugin";
        public override string ModuleVersion => "1.3";
        public override string ModuleAuthor => "Developer";
        public override string ModuleDescription => "Auto-assign players to T/CT from JSON; infinite warmup until all join, then 5 min warmup.";

        private const string CURRENT_MATCH_FILE = @"C:\AVAstuf\currentMatch.json";

        // Cписки SteamID
        private HashSet<ulong> _teamAIDs = new();
        private HashSet<ulong> _teamBIDs = new();

        // Текущая сторона (2 = T, 3 = CT)
        private byte _teamANum;
        private byte _teamBNum;

        // playerSlot => steamId
        private Dictionary<int, ulong> _slotToSteamId = new();

        // Список "уже зашли" игроков (из команды)
        private HashSet<ulong> _connectedPlayersInMatch = new();

        // Флаг, чтобы “5-минутная разминка” запускалась один раз
        private bool _hasStartedFinalWarmup = false;

        public override void Load(bool hotReload)
        {
            RegisterListener<OnClientAuthorized>(OnClientAuthorized);
            RegisterListener<OnClientPutInServer>(OnClientPutInServer);

            LoadMatchData();
            Logger.LogInformation("[TeamAssignmentPlugin] Loaded. Waiting for players...");

            // Ставим "вечную разминку":
            SetInfiniteWarmup();
        }

        public override void Unload(bool hotReload)
        {
            RemoveListener<OnClientAuthorized>(OnClientAuthorized);
            RemoveListener<OnClientPutInServer>(OnClientPutInServer);
        }

        private void OnClientAuthorized(int playerSlot, [CastFrom(typeof(ulong))] SteamID steamId)
        {
            ulong sid = (ulong)steamId.AccountId;
            _slotToSteamId[playerSlot] = sid;
            Logger.LogInformation($"[TeamAssignmentPlugin] PlayerSlot={playerSlot}, SteamID={sid} authorized.");
        }

        private void OnClientPutInServer(int playerSlot)
        {
            if (!_slotToSteamId.TryGetValue(playerSlot, out ulong sid))
            {
                Logger.LogInformation($"[TeamAssignmentPlugin] slot={playerSlot}, no steamId -> Spect.");
                PutPlayerToSpectator(playerSlot);
                return;
            }

            if (_teamAIDs.Contains(sid))
            {
                Logger.LogInformation($"[TeamAssignmentPlugin] slot={playerSlot}, SID={sid} -> Team A={_teamANum}");
                SetPlayerTeam(playerSlot, _teamANum);

                // Добавляем в список "зашедших"
                _connectedPlayersInMatch.Add(sid);
                CheckAllPlayersJoined();
            }
            else if (_teamBIDs.Contains(sid))
            {
                Logger.LogInformation($"[TeamAssignmentPlugin] slot={playerSlot}, SID={sid} -> Team B={_teamBNum}");
                SetPlayerTeam(playerSlot, _teamBNum);

                _connectedPlayersInMatch.Add(sid);
                CheckAllPlayersJoined();
            }
            else
            {
                Logger.LogInformation($"[TeamAssignmentPlugin] slot={playerSlot}, SID={sid} => Spect");
                PutPlayerToSpectator(playerSlot);
            }
        }

        // Проверяем, не зашли ли все
        private void CheckAllPlayersJoined()
        {
            int needed = _teamAIDs.Count + _teamBIDs.Count;
            int have = _connectedPlayersInMatch.Count;
            Logger.LogInformation($"[CheckAllPlayersJoined] {have}/{needed} have joined.");

            if (!_hasStartedFinalWarmup && have == needed)
            {
                // Все зашли — запускаем 5-минутную разминку
                StartFinalWarmup();
            }
        }

        // == Установка команд ==

        private void SetPlayerTeam(int playerSlot, int team)
        {
            var entity = NativeAPI.GetEntityFromIndex(playerSlot + 1);
            if (entity == null) return;

            var player = new CCSPlayerController(entity);
            player.TeamNum = (byte)team;
        }

        private void PutPlayerToSpectator(int playerSlot)
        {
            SetPlayerTeam(playerSlot, 1); // 1 = Spect
        }

        // == Логика Warmup ==

        private void SetInfiniteWarmup()
        {
            Logger.LogInformation("[TeamAssignmentPlugin] Setting infinite warmup...");

            // Команды, которые вам подходят в CS2 (может отличаться от CS:GO)
            // Пример (если сработает):
            Server.ExecuteCommand("mp_do_warmup_period 1");
            Server.ExecuteCommand("mp_warmuptime 999999");
            Server.ExecuteCommand("mp_warmup_pausetimer 1");
            Server.ExecuteCommand("mp_restartgame 1");
        }

        private void StartFinalWarmup()
        {
            _hasStartedFinalWarmup = true;
            Logger.LogInformation("[TeamAssignmentPlugin] All players joined. Starting 5-min warmup...");

            // Отключаем "паузу" таймера, ставим 5 минут
            Server.ExecuteCommand("mp_warmuptime 300");   // 5 минут
            Server.ExecuteCommand("mp_warmup_pausetimer 0");
            // Рестарт, чтобы разминка начала тикать
            Server.ExecuteCommand("mp_restartgame 1");
        }

        // == Загрузка JSON ==

        private void LoadMatchData()
        {
            if (!File.Exists(CURRENT_MATCH_FILE))
            {
                Logger.LogInformation($"[TeamAssignmentPlugin] {CURRENT_MATCH_FILE} not found. No assignment will happen.");
                return;
            }

            try
            {
                var json = File.ReadAllText(CURRENT_MATCH_FILE);
                var data = JsonSerializer.Deserialize<CurrentMatchModel>(json);

                if (data == null)
                {
                    Logger.LogWarning("[TeamAssignmentPlugin] JSON is null/invalid.");
                    return;
                }

                _teamAIDs.Clear();
                _teamBIDs.Clear();
                _connectedPlayersInMatch.Clear();
                _hasStartedFinalWarmup = false;

                if (data.TeamA?.SteamIds != null)
                {
                    foreach (var s in data.TeamA.SteamIds)
                        if (ulong.TryParse(s, out var sid))
                            _teamAIDs.Add(sid);
                }
                if (data.TeamB?.SteamIds != null)
                {
                    foreach (var s in data.TeamB.SteamIds)
                        if (ulong.TryParse(s, out var sid))
                            _teamBIDs.Add(sid);
                }

                _teamANum = data.TeamASide == "T" ? (byte)2 : (byte)3;
                _teamBNum = data.TeamBSide == "T" ? (byte)2 : (byte)3;

                Logger.LogInformation($"[TeamAssignmentPlugin] Match loaded. TeamA side={data.TeamASide}, TeamB side={data.TeamBSide}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[TeamAssignmentPlugin] Ошибка при загрузке JSON: {ex}");
            }
        }
    }

    // ==== Модели ====
    public class CurrentMatchModel
    {
        public TeamModel TeamA { get; set; }
        public TeamModel TeamB { get; set; }
        public string TeamASide { get; set; }
        public string TeamBSide { get; set; }
    }

    public class TeamModel
    {
        public string TeamName { get; set; }
        public List<string> SteamIds { get; set; }
    }
}
