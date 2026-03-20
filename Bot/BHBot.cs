using BitHeroesClient.Config;
using BitHeroesClient.Logging;
using BitHeroesClient.Models;
using Sfs2X;
using Sfs2X.Core;
using Sfs2X.Entities;
using Sfs2X.Entities.Data;
using Sfs2X.Requests;
using Steamworks;
using System.Security.Cryptography;
using System.Text;

namespace BitHeroesClient.Bot;

// ── Error codes (observed from server traffic) ─────────────────────────────────

/// <summary>
/// Known server-side error codes returned in the <c>err0</c> field.
/// Non-exhaustive; additional values are handled generically.
/// </summary>
internal static class ServerError
{
    /// <summary>Generic "not available" / already done.</summary>
    public const int NOT_AVAILABLE   = 1;
    /// <summary>Insufficient energy to enter the dungeon.</summary>
    public const int NO_ENERGY       = 2;
    /// <summary>Zone/node not yet unlocked for this character.</summary>
    public const int ZONE_LOCKED     = 3;
    /// <summary>Character is currently in the tutorial; dungeon access blocked.</summary>
    public const int IN_TUTORIAL     = 10;
    /// <summary>Insufficient tickets to enter the dungeon.</summary>
    public const int NO_TICKETS      = 22;
}

// ── Automation state machine ───────────────────────────────────────────────────

internal enum LoginState { Disconnected, SfsLoggedIn, AwaitingPlatformResp, LoadingXmls, InGame }

public enum AutoState
{
    Idle,
    ClaimingDailyReward,
    CheckingDailyQuests,
    ClaimingDailyQuest,
    EnteringDungeon,
    WaitingForBattle,    // in dungeon session, no active battle yet
    AutoBattleActive,    // AUTO sent; server is fighting the wave
    WaitingForResults,   // RESULTS sent; awaiting loot packet
    CooldownBeforeRepeat,// resting between runs
    EnergyWait,          // waiting for energy to regenerate
    Stopped              // terminal state (MaxRuns reached, tutorial, etc.)
}

// ── Main bot ───────────────────────────────────────────────────────────────────

/// <summary>
/// Clientless Bit Heroes automation bot.
///
/// <para><b>Quick start:</b>
/// <code>
///   BHBot.OnInGame += () => Console.WriteLine("Online!");
///   BHBot.OnDungeonComplete += r => Console.WriteLine($"Run #{r.RunNumber}");
///   BHBot.Start(appConfig);
///   while (true) BHBot.Tick();
/// </code>
/// All public <c>Send*</c> methods throw <see cref="InvalidOperationException"/>
/// if called before <see cref="OnInGame"/> fires.
/// </para>
///
/// <para><b>Packet protocol summary:</b>
/// Every outgoing SFSObject carries the envelope fields
/// <c>dal0</c> (DALC router ID), <c>act0</c> (action within DALC),
/// <c>cli1</c> (app version), <c>cli2</c> (SFS2X lib version),
/// <c>cli3</c> (DLC/asset-bundle version).
/// See CLAUDE.md for the full DALC registry and field tables.
/// </para>
/// </summary>
public static class BHBot
{
    #region Protocol constants

    const string APP_VERSION   = "2.5.6";
    const string SFS_VERSION   = "1.7.5";
    const string DLC_VERSION   = "StandaloneWindows64_20260319T193332Z";
    const string SFS_ZONE           = "Server";
    const string SFS_EXTENSION      = "ServerExtension";
    const string DUNGEON_EXTENSION  = "DungeonExtension";
    const int    PLATFORM      = 7;   // Windows/Steam
    const int    CLI_PLATFORM  = 4;   // Desktop
    const string HASH_SALT     = "k5iw3la0";

    // DALC IDs (dal0 field) — see CLAUDE.md for full registry
    const int DALC_GAME      = 0;
    const int DALC_CHARACTER = 1;
    const int DALC_USER      = 4;
    const int DALC_CHAT      = 8;
    const int DALC_GUILD     = 9;
    const int DALC_BATTLE    = 10;
    const int DALC_PLAYER    = 21;

    /// <summary>
    /// Keep-alive interval. Server idles out after 120 s; send every 90 s for safety.
    /// GameDALC IDLE_RESPONSE (dal0=0, act0=12).
    /// </summary>
    const long HEARTBEAT_MS = 90_000;

    /// <summary>
    /// If no ENTER_BATTLE arrives within this many ms after the last wave response,
    /// assume all waves are done and fire BattleDALC RESULTS.
    /// </summary>
    const int BATTLE_DONE_TIMEOUT_MS = 15_000;

    #endregion

    #region Runtime state

    static SmartFox?    _sfs;
    static LoginState   _login  = LoginState.Disconnected;
    static AutoState    _auto   = AutoState.Idle;
    static AppConfig    _config = new();

    // Steam — initialised once; SteamAPI.Init() must only be called once per process
    static bool   _steamReady   = false;
    static string _steamId64   = "";
    static string _steamTicket = "";

    // Persisted across sessions
    static int    _playerId    = -1;
    static string _anonymousId = "";

    // Tutorial / account flags received from the server on login
    static bool _inTutorial = false;

    // Character ID assigned by the server on login (cha1). Used to distinguish
    // DungeonExtension act0=1 packets for our own character vs other players.
    static int _myCharId = -1;

    // PVE team auto-detected from server's cha62 character data.
    // Used when DungeonConfig.Teammates is empty (the default).
    static List<TeammateConfig> _savedTeammates = new();

    // Daily quest work queue
    static readonly Queue<DailyQuestEntry> _questsToLoot = new();

    // Dungeon run tracking (reset per run)
    static int      _runCount          = 0;
    static int      _dungeonQueueIndex = 0;   // current position in DungeonQueue
    static int      _waveCount         = 0;
    static DateTime _lastBattleEvent   = DateTime.MinValue;

    // Active dungeon room (set after joining; null when not in a dungeon)
    // Dungeon packets MUST be sent to this room via "DungeonExtension" cmd.
    static Room?    _dungeonRoom = null;

    // Dungeon grid state — parsed from ENTER_DUNGEON (act0=1).
    // Used to navigate to an enemy node via DoObjectActivate.
    static int  _playerRow = 0;
    static int  _playerCol = 0;
    static int  _navTargetRow = -1;   // current DoObjectActivate destination
    static int  _navTargetCol = -1;

    // True after SendDungeonActivate() fires until ENTER_BATTLE arrives for that activation.
    // Guards against double-activation from both OBJECT_REMOVE and ITEMS_ADDED firing back-to-back.
    static bool _pendingActivation = false;

    // True after all dungeon objects are defeated and we are waiting for DUNGEON_COMPLETE.
    // Suppresses the WaitingForBattle stall-timeout so it doesn't fire a spurious RESULTS.
    static bool _awaitingDungeonComplete = false;

    // dun28/dun32 are bool in the SFS2X packet.
    static List<DungeonObjectInfo> _dungeonObjects = new();

    // General cooldown timer (between-run delay, energy wait, error backoff)
    static DateTime _cooldownUntil = DateTime.MinValue;
    static AutoState _stateAfterCooldown = AutoState.Idle;  // state to resume after cooldown

    // Error retry tracking
    static int _retryCount = 0;

    // Keep-alive
    static DateTime _lastHeartbeat = DateTime.MinValue;

    #endregion

    #region Public events

    /// <summary>Fires once the login sequence completes and the session is fully in-game.</summary>
    public static event Action? OnInGame;

    /// <summary>Fires after each completed dungeon run (victory or defeat).</summary>
    public static event Action<DungeonResult>? OnDungeonComplete;

    /// <summary>
    /// Fires on every incoming chat message.
    /// Args: (senderName, messageText, isPrivate).
    /// </summary>
    public static event Action<string, string, bool>? OnChatMessage;

    /// <summary>Fires when the server reports an online player count.</summary>
    public static event Action<int>? OnPlayersOnline;

    /// <summary>
    /// Fires after ENTER_DUNGEON is parsed. The list contains all dungeon grid objects
    /// (enemies, treasures, shrines, etc.) with their row/col/type.
    /// </summary>
    public static event Action<IReadOnlyList<DungeonObjectInfo>>? OnDungeonLoaded;

    /// <summary>
    /// Fires when the server sends a server-side error (err0 field).
    /// Args: (dalcId, actionId, errorCode).
    /// </summary>
    public static event Action<int, int, int>? OnServerError;

    #endregion

    #region Public API — lifecycle

    /// <summary>
    /// Initialise Steam, connect to the game server, and begin the login sequence.
    /// After returning, call <see cref="Tick"/> in a tight loop.
    /// </summary>
    /// <param name="config">Full application configuration.</param>
    /// <exception cref="Exception">If SteamAPI.Init() fails.</exception>
    public static void Start(AppConfig config)
    {
        _config = config;

        // SteamAPI.Init() must only be called once per process lifetime.
        // Calling it a second time (Stop → Start) causes it to return false and
        // previously triggered Environment.Exit(1) — fixed with this guard.
        if (!_steamReady)
        {
            if (!SteamAPI.Init())
            {
                Logger.Error("SteamAPI.Init() failed.");
                Logger.Error("Ensure Steam is running, you own Bit Heroes (AppID 666860),");
                Logger.Error("and steam_appid.txt next to the exe contains: 666860");
                throw new Exception("SteamAPI.Init() failed — is Steam running?");
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) => SteamAPI.Shutdown();
            _steamReady = true;
        }

        _steamId64   = ((long)SteamUser.GetSteamID().m_SteamID).ToString();
        _steamTicket = GetSteamAuthTicket();   // fresh ticket on every Start()
        string name  = SteamFriends.GetPersonaName();
        Logger.Info($"Steam OK – SteamID={_steamId64}  Name={name}");
        Logger.Debug($"Ticket: {_steamTicket[..Math.Min(16, _steamTicket.Length)]}...");

        _anonymousId = LoadOrCreateAnonymousId();
        _playerId    = LoadPlayerId();
        if (_playerId != -1) Logger.Info($"Cached playerID={_playerId}");

        var host = config.Connection.Host;
        var port = config.Connection.Port;

        _sfs = new SmartFox();
        _sfs.ThreadSafeMode = true;
        _sfs.AddEventListener(SFSEvent.CONNECTION,         OnConnection);
        _sfs.AddEventListener(SFSEvent.CONNECTION_LOST,    OnConnectionLost);
        _sfs.AddEventListener(SFSEvent.LOGIN,              OnLogin);
        _sfs.AddEventListener(SFSEvent.LOGIN_ERROR,        OnLoginError);
        _sfs.AddEventListener(SFSEvent.EXTENSION_RESPONSE, OnExtensionResponse);
        _sfs.AddEventListener(SFSEvent.ROOM_JOIN,          OnRoomJoin);
        _sfs.AddEventListener(SFSEvent.ROOM_JOIN_ERROR,    OnRoomJoinError);

        Logger.Info($"Connecting to {host}:{port}...");
        _sfs.Connect(host, port);
    }

    /// <summary>
    /// Pump SFS2X events, Steam callbacks, and automation timers.
    /// Must be called on every iteration of the main loop.
    /// </summary>
    public static void Tick()
    {
        SteamAPI.RunCallbacks();
        _sfs?.ProcessEvents();
        CheckHeartbeat();
        CheckCooldowns();
    }

    /// <summary>Cleanly disconnect. Sends a logout packet and drops the TCP connection.</summary>
    public static void Stop()
    {
        if (_login == LoginState.InGame)
            SendLogoutPacket();
        _sfs?.Disconnect();
        _auto = AutoState.Stopped;
    }

    /// <summary>true while fully logged in and ready to send game packets.</summary>
    public static bool IsInGame => _login == LoginState.InGame;

    /// <summary>Player's row in the current dungeon grid (0 if not in a dungeon).</summary>
    public static int PlayerRow => _playerRow;

    /// <summary>Player's column in the current dungeon grid (0 if not in a dungeon).</summary>
    public static int PlayerCol => _playerCol;

    /// <summary>Current automation state — readable by the UI for button enable/disable logic.</summary>
    public static AutoState CurrentAutoState => _auto;

    /// <summary>
    /// true while the dungeon loop is actively running (entering, battling, cooldown, or energy wait).
    /// false when idle, doing daily tasks, or stopped.
    /// </summary>
    public static bool IsDungeonLoopRunning =>
        _auto is AutoState.EnteringDungeon or AutoState.WaitingForBattle or
                 AutoState.AutoBattleActive or AutoState.WaitingForResults or
                 AutoState.CooldownBeforeRepeat or AutoState.EnergyWait;

    /// <summary>
    /// Start the dungeon loop from the beginning of the queue.
    /// Safe to call any time while InGame and idle; no-op otherwise.
    /// </summary>
    public static void StartDungeonLoop()
    {
        if (_login != LoginState.InGame) return;
        if (IsDungeonLoopRunning) return;
        Logger.Info("Dungeon loop started by user.");
        _dungeonQueueIndex = 0;
        _runCount          = 0;
        _retryCount        = 0;
        SessionStats.SetRetryCount(0);
        StartDungeonLoopIfConfigured();
    }

    /// <summary>Stop the dungeon loop and return to idle. The current run is abandoned immediately.</summary>
    public static void StopDungeonLoop()
    {
        if (!IsDungeonLoopRunning) return;
        _auto = AutoState.Idle;
        SessionStats.SetState("Idle", "Loop stopped by user");
        Logger.Info("Dungeon loop stopped by user.");
    }

    #endregion

    #region Public API — daily rewards

    /// <summary>
    /// Claim the daily login-streak reward.
    /// CharacterDALC DAILY_REWARD (dal0=1, act0=5).
    /// </summary>
    public static void ClaimDailyReward()
    {
        AssertInGame();
        Send(Packet(DALC_CHARACTER, 5));
        Logger.Info("[>] CharacterDALC DAILY_REWARD");
    }

    /// <summary>
    /// Request daily quest progress. The bot auto-loots completed quests from the response.
    /// CharacterDALC DAILY_QUEST_CHECK (dal0=1, act0=13).
    /// </summary>
    public static void CheckDailyQuests()
    {
        AssertInGame();
        Send(Packet(DALC_CHARACTER, 0xD));
        Logger.Info("[>] CharacterDALC DAILY_QUEST_CHECK");
    }

    /// <summary>
    /// Loot a specific completed daily quest.
    /// CharacterDALC DAILY_QUEST_LOOT (dal0=1, act0=14, dail1=questId).
    /// </summary>
    public static void LootDailyQuest(int questId)
    {
        AssertInGame();
        var p = Packet(DALC_CHARACTER, 0xE);
        p.PutInt("dail1", questId);
        Send(p);
        Logger.Info($"[>] CharacterDALC DAILY_QUEST_LOOT questId={questId}");
    }

    /// <summary>
    /// Refresh the daily consumable rewards pool.
    /// CharacterDALC UPDATE_DAILY_REWARDS_CONSUMABLE_PERIODIC (dal0=1, act0=0x4E).
    /// </summary>
    public static void RefreshDailyConsumables()
    {
        AssertInGame();
        Send(Packet(DALC_CHARACTER, 0x4E));
        Logger.Info("[>] CharacterDALC DAILY_REWARDS_CONSUMABLE_UPDATE");
    }

    #endregion

    #region Public API — dungeon / battle

    /// <summary>
    /// Enter a zone node to start a dungeon session.
    /// GameDALC ENTER_ZONE_NODE (dal0=0, act0=5).
    /// Fields: zon0=zoneId, zon1=nodeId, zon2=difficultyId, tmts0=teammates.
    /// </summary>
    public static void EnterZoneNode(DungeonConfig cfg)
    {
        AssertInGame();

        // CharacterDALC act0=12 — lineup/team confirmation.
        // The real client always sends this immediately before ENTER_ZONE_NODE.
        // Without it the server kicks the client ~1 s after zone entry.
        SendCharacterLineup(cfg);

        var p = Packet(DALC_GAME, 5);
        p.PutInt("zon0", cfg.ZoneId);
        p.PutInt("zon1", cfg.NodeId);
        p.PutInt("zon2", cfg.DifficultyId);
        SerialiseTeammates(p, GetEffectiveTeammates(cfg));
        Send(p);
        Logger.Info($"[>] GameDALC ENTER_ZONE_NODE zone={cfg.ZoneId} node={cfg.NodeId} diff={cfg.DifficultyId}");
        SessionStats.SetZone(cfg.ZoneId, cfg.NodeId, 0);
    }

    /// <summary>
    /// Enable server-side AUTO battle for the current wave.
    /// BattleDALC AUTO (dal0=10, act0=5, bat57=useDamageGain).
    /// Call after receiving GameDALC ENTER_BATTLE (act0=0).
    /// </summary>
    public static void SendBattleAuto(bool useDamageGain = true)
    {
        AssertInGame();
        var p = Packet(DALC_BATTLE, 5);
        p.PutBool("bat57", useDamageGain);
        Send(p);
        Logger.Info($"[>] BattleDALC AUTO damageGain={useDamageGain}");
        SessionStats.SetState("AutoBattleActive", $"AUTO sent (dmgGain={useDamageGain})");
    }

    /// <summary>
    /// Request final dungeon loot after all waves are complete.
    /// BattleDALC RESULTS (dal0=10, act0=4).
    /// Call once per dungeon run. Server responds with loot in act1/act2 and bat47 (victory).
    /// </summary>
    public static void SendBattleResults()
    {
        AssertInGame();
        Send(Packet(DALC_BATTLE, 4));
        Logger.Info("[>] BattleDALC RESULTS");
        SessionStats.SetState("WaitingForResults", "RESULTS sent");
    }

    /// <summary>
    /// Immediately abandon the current battle.
    /// BattleDALC QUIT (dal0=10, act0=7).
    /// </summary>
    public static void QuitBattle()
    {
        AssertInGame();
        Send(Packet(DALC_BATTLE, 7));
        Logger.Info("[>] BattleDALC QUIT");
    }

    /// <summary>
    /// Decline a familiar-capture prompt during battle.
    /// BattleDALC CAPTURE_DECLINE (dal0=10, act0=9, bat7=entityIndex).
    /// </summary>
    public static void DeclineCapture(int entityIndex)
    {
        AssertInGame();
        var p = Packet(DALC_BATTLE, 9);
        p.PutInt("bat7", entityIndex);
        Send(p);
        Logger.Info($"[>] BattleDALC CAPTURE_DECLINE entity={entityIndex}");
    }

    /// <summary>
    /// Accept a familiar-capture prompt during battle.
    /// BattleDALC CAPTURE_ACCEPT (dal0=10, act0=8).
    /// </summary>
    public static void AcceptCapture(int entityIndex, int serviceItemId, int currencyId, int currencyCost)
    {
        AssertInGame();
        var p = Packet(DALC_BATTLE, 8);
        p.PutInt("bat7",  entityIndex);
        p.PutInt("ite0",  serviceItemId);
        p.PutInt("curr0", currencyId);
        p.PutInt("curr2", currencyCost);
        Send(p);
        Logger.Info($"[>] BattleDALC CAPTURE_ACCEPT entity={entityIndex}");
    }

    /// <summary>
    /// Manually activate a dungeon object at (row, col) — triggers a battle for ENEMY/BOSS nodes.
    /// Sends DungeonExtension DoObjectActivate (act0=5) and logs the full packet.
    /// Call after ENTER_DUNGEON; requires a dungeon session to be active.
    /// </summary>
    public static void ActivateDungeonObject(int row, int col)
    {
        AssertInGame();
        _navTargetRow = row;
        _navTargetCol = col;

        Logger.Info($"[>] DoObjectActivate  row={row}  col={col}");
        Logger.Info($"    act0=5  dun11=[{row}]  dun12=[{col}]" +
                    $"  cli1={APP_VERSION}  cli2={SFS_VERSION}  cli3={DLC_VERSION}");
        Logger.Info($"    _dungeonRoom={((_dungeonRoom != null) ? $"id={_dungeonRoom.Id} name={_dungeonRoom.Name}" : "null (→ ServerExtension fallback)")}");

        var pkt = DungeonPacket(5);
        pkt.PutIntArray("dun11", new[] { row });
        pkt.PutIntArray("dun12", new[] { col });
        SendDungeon(pkt);

        _auto = AutoState.WaitingForBattle;
        _lastBattleEvent = DateTime.UtcNow;
        SessionStats.SetState("WaitingForBattle", $"Activated ({row},{col})");
    }

    /// <summary>
    /// Reconnect to or abandon an interrupted dungeon session.
    /// GameDALC RECONNECT_DUNGEON (dal0=0, act0=14, act6=cancel).
    /// </summary>
    public static void ReconnectDungeon(bool cancel)
    {
        AssertInGame();
        var p = Packet(DALC_GAME, 0xE);
        p.PutBool("act6", cancel);
        Send(p);
        Logger.Info($"[>] GameDALC RECONNECT_DUNGEON cancel={cancel}");
    }

    #endregion

    #region Public API — social / misc

    /// <summary>Send a public chat message. ChatDALC CHAT_MESSAGE (dal0=8, act0=1).</summary>
    public static void SendChat(string message)
    {
        AssertInGame();
        var p = Packet(DALC_CHAT, 1);
        p.PutUtfString("chat0", message);
        Send(p);
    }

    /// <summary>
    /// Send a private message to another character.
    /// ChatDALC PRIVATE_MESSAGE (dal0=8, act0=2).
    /// </summary>
    public static void SendPrivateMessage(string message, int targetCharId)
    {
        AssertInGame();
        var p = Packet(DALC_CHAT, 2);
        p.PutUtfString("chat0", message);
        p.PutInt("cha1", targetCharId);
        Send(p);
    }

    /// <summary>
    /// Request the online player count.
    /// GameDALC PLAYERS_ONLINE (dal0=0, act0=7). Response fires <see cref="OnPlayersOnline"/>.
    /// </summary>
    public static void RequestPlayersOnline()
    {
        AssertInGame();
        var p = Packet(DALC_GAME, 7);
        p.PutBool("act4", false);
        Send(p);
    }

    /// <summary>Load guild data. GuildDALC LOAD_DATA (dal0=9, act0=5).</summary>
    public static void LoadGuildData()
    {
        AssertInGame();
        Send(Packet(DALC_GUILD, 5));
        Logger.Info("[>] GuildDALC LOAD_DATA");
    }

    /// <summary>
    /// Refresh daily bounty tile state.
    /// GameDALC DAILY_QUESTS_UPDATE (dal0=0, act0=11).
    /// </summary>
    public static void RefreshBounties()
    {
        AssertInGame();
        Send(Packet(DALC_GAME, 0xB));
        Logger.Info("[>] GameDALC DAILY_QUESTS_UPDATE");
    }

    /// <summary>
    /// Hash a password using the game's algorithm: MD5(plaintext + "k5iw3la0").
    /// Use when constructing email-login packets manually via UserDALC/PlayerDALC.
    /// </summary>
    public static string HashPassword(string plaintext)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(plaintext + HASH_SALT));
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    #endregion

    #region SFS2X connection callbacks

    static void OnConnection(BaseEvent evt)
    {
        if (evt.Params["success"] is true)
        {
            Logger.Info("TCP connected. Sending SFS2X guest login...");
            var lp = new SFSObject();
            lp.PutUtfString("use8", "en");
            _sfs!.Send(new LoginRequest("", "", SFS_ZONE, lp));
        }
        else
        {
            Logger.Error("TCP connection failed.");
            SessionStats.SetState("Disconnected", "Connection failed");
        }
    }

    static void OnConnectionLost(BaseEvent evt)
    {
        string reason = evt.Params.Contains("reason") ? evt.Params["reason"]?.ToString() ?? "unknown" : "unknown";
        _login       = LoginState.Disconnected;
        _auto        = AutoState.Idle;
        _dungeonRoom = null;
        Logger.Warn($"Connection lost. Reason: {reason}");
        SessionStats.SetState("Disconnected");
    }

    static void OnRoomJoin(BaseEvent evt)
    {
        if (evt.Params["room"] is not Room room) return;
        Logger.Info($"[ROOM] Joined room: id={room.Id} name={room.Name}");
        _dungeonRoom = room;
        // We are now in the dungeon room — ENTER_DUNGEON (act0=1) will arrive shortly
        _auto = AutoState.WaitingForBattle;
        _lastBattleEvent = DateTime.UtcNow;
        SessionStats.SetState("WaitingForBattle", $"Dungeon room joined (id={room.Id})");
    }

    static void OnRoomJoinError(BaseEvent evt)
    {
        string msg = evt.Params.Contains("errorMessage") ? evt.Params["errorMessage"]?.ToString() ?? "?" : "?";
        Logger.Error($"[ROOM] Failed to join dungeon room: {msg}");
        // Fall back to retry
        ScheduleCooldown(_config.Automation.RetryDelayMs, AutoState.EnteringDungeon);
    }

    static void OnLoginError(BaseEvent evt) =>
        Logger.Error($"SFS2X login error: {evt.Params["errorMessage"]}");

    static void OnLogin(BaseEvent evt)
    {
        Logger.Info("SFS2X guest login OK. Sending PlayerDALC LOGIN_PLATFORM...");
        _login = LoginState.SfsLoggedIn;
        SessionStats.SetState("Authenticating", "LOGIN_PLATFORM sent");
        SendLoginPlatformPacket();
    }

    #endregion

    #region Central response router

    static void OnExtensionResponse(BaseEvent evt)
    {
        if (evt.Params["params"] is not SFSObject resp) return;

        string cmd  = evt.Params.Contains("cmd") ? evt.Params["cmd"]?.ToString() ?? "" : "";
        int dalc = resp.ContainsKey("dal0") ? resp.GetInt("dal0") : -1;
        int act  = resp.ContainsKey("act0") ? resp.GetInt("act0") : -1;

        try
        {
            if (resp.ContainsKey("err0"))
            {
                int err = resp.GetInt("err0");
                Logger.Warn($"Server error – cmd={cmd} dal0={dalc} act0={act} err0={err}");
                OnServerError?.Invoke(dalc, act, err);
                HandleServerError(dalc, act, err);
                return;
            }

            if (dalc == -1)
            {
                // DungeonExtension sends packets without dal0 — route them to their own handler.
                if (cmd == DUNGEON_EXTENSION && act != -1)
                {
                    Logger.Debug($"[DUNGEON-EXT] act0={act} keys: {string.Join(", ", resp.GetKeys())}");
                    HandleDungeonExtension(resp, act);
                }
                else if (cmd == "InstanceExtension" && act != -1)
                {
                    // InstanceExtension handles player movement, presence, and fishing in instance rooms.
                    // These are separate from dungeon combat — no action needed here.
                    Logger.Debug($"[INST-EXT] act0={act} keys: {string.Join(", ", resp.GetKeys())}");
                }
                else
                {
                    bool inDungeon = _auto is AutoState.EnteringDungeon or AutoState.WaitingForBattle
                                              or AutoState.AutoBattleActive or AutoState.WaitingForResults;
                    if (inDungeon)
                        Logger.Debug($"[DUNGEON] Packet without dal0 cmd={cmd} act0={act} keys: {string.Join(", ", resp.GetKeys())}");
                    else
                        Logger.Debug($"Response without dal0 cmd={cmd} act0={act}");
                }
                return;
            }

            Logger.Debug($"[<] dal0={dalc} act0={act} cmd={cmd}");

            switch (dalc)
            {
                case DALC_PLAYER:    HandlePlayerDALC(resp, act);    break;
                case DALC_USER:      HandleUserDALC(resp, act);      break;
                case DALC_GAME:      HandleGameDALC(resp, act);      break;
                case DALC_CHARACTER: HandleCharacterDALC(resp, act); break;
                case DALC_BATTLE:    HandleBattleDALC(resp, act);    break;
                case DALC_CHAT:      HandleChatDALC(resp, act);      break;
                case DALC_GUILD:     HandleGuildDALC(resp, act);     break;
                default:
                    Logger.Debug($"Unhandled dal0={dalc} act0={act}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Handler exception dal0={dalc} act0={act}: {ex.GetType().Name}: {ex.Message}");
            Logger.Debug($"Packet dump:\n{resp.GetDump()}");
            // Do NOT rethrow — propagating through SFS2X event dispatch drops the connection.
        }
    }

    #endregion

    #region Error recovery

    static void HandleServerError(int dalc, int act, int err)
    {
        // ── Tutorial / locked content ──────────────────────────────────────────
        if (err == ServerError.IN_TUTORIAL)
        {
            _inTutorial = true;
            HandleTutorialDetected();
            return;
        }

        if (err == ServerError.ZONE_LOCKED && _auto == AutoState.EnteringDungeon)
        {
            var locked = GetCurrentDungeon();
            Logger.Warn($"Zone Z{locked.ZoneId}/N{locked.NodeId} is locked — skipping to next in queue.");
            int prevIdx = _dungeonQueueIndex;
            AdvanceQueueToNextEnabled();
            if (_dungeonQueueIndex == prevIdx)
            {
                Logger.Warn("No accessible dungeons remain in queue. Idling.");
                _auto = AutoState.Stopped;
                SessionStats.SetState("Stopped", "All zones locked");
            }
            else
            {
                ScheduleCooldown(_config.Automation.RetryDelayMs, AutoState.EnteringDungeon);
            }
            return;
        }

        // ── Out of energy ──────────────────────────────────────────────────────
        if (err == ServerError.NO_ENERGY &&
            (_auto == AutoState.EnteringDungeon || _auto == AutoState.WaitingForBattle))
        {
            var nextRegen = SessionStats.NextEnergyAt;
            int waitMs;
            if (nextRegen > DateTime.MinValue && nextRegen > DateTime.UtcNow)
            {
                waitMs = (int)(nextRegen - DateTime.UtcNow).TotalMilliseconds + 5_000;
                Logger.Warn($"Out of energy. Waiting until {nextRegen:HH:mm:ss} UTC for regen (+5s buffer).");
            }
            else
            {
                waitMs = _config.Automation.EnergyWaitMinutes * 60_000;
                Logger.Warn($"Out of energy. Waiting {_config.Automation.EnergyWaitMinutes} min (no regen data yet).");
            }
            _auto = AutoState.EnergyWait;
            SessionStats.SetState("EnergyWait", "Waiting for energy regen");
            ScheduleCooldown(waitMs, AutoState.EnteringDungeon);
            return;
        }

        // ── Out of tickets ─────────────────────────────────────────────────────
        if (err == ServerError.NO_TICKETS &&
            (_auto == AutoState.EnteringDungeon || _auto == AutoState.WaitingForBattle))
        {
            var nextRegen = SessionStats.NextTicketAt;
            int waitMs;
            if (nextRegen > DateTime.MinValue && nextRegen > DateTime.UtcNow)
            {
                waitMs = (int)(nextRegen - DateTime.UtcNow).TotalMilliseconds + 5_000;
                Logger.Warn($"Out of tickets. Waiting until {nextRegen:HH:mm:ss} UTC for regen (+5s buffer).");
            }
            else
            {
                waitMs = _config.Automation.EnergyWaitMinutes * 60_000;
                Logger.Warn($"Out of tickets. Waiting {_config.Automation.EnergyWaitMinutes} min (no regen data yet).");
            }
            _auto = AutoState.EnergyWait;
            SessionStats.SetState("EnergyWait", "Waiting for ticket regen");
            ScheduleCooldown(waitMs, AutoState.EnteringDungeon);
            return;
        }

        // ── Generic recovery by current auto state ────────────────────────────
        switch (_auto)
        {
            case AutoState.ClaimingDailyReward:
                Logger.Info("Daily reward not available (already claimed or server error).");
                AdvanceAutomationAfterDailyReward();
                break;

            case AutoState.CheckingDailyQuests:
            case AutoState.ClaimingDailyQuest:
                Logger.Warn("Daily quest error – skipping quests.");
                _questsToLoot.Clear();
                SetIdleAfterDailies();
                break;

            case AutoState.EnteringDungeon:
            case AutoState.WaitingForBattle:
                _retryCount++;
                SessionStats.SetRetryCount(_retryCount);
                if (_retryCount >= _config.Automation.MaxRetries)
                {
                    Logger.Error($"Dungeon entry failed {_retryCount} times. Idling.");
                    _auto = AutoState.Idle;
                    SessionStats.SetState("Idle", "Max retries reached");
                }
                else
                {
                    Logger.Warn($"Dungeon entry error (retry {_retryCount}/{_config.Automation.MaxRetries}).");
                    ScheduleCooldown(_config.Automation.RetryDelayMs, AutoState.EnteringDungeon);
                }
                break;

            case AutoState.AutoBattleActive:
                Logger.Warn("Battle error during AUTO – requesting results.");
                _auto = AutoState.WaitingForResults;
                SendBattleResults();
                break;

            case AutoState.WaitingForResults:
                Logger.Error("Results error – resetting dungeon state.");
                ResetDungeonState();
                ScheduleCooldown(_config.Automation.RetryDelayMs, AutoState.EnteringDungeon);
                break;
        }
    }

    static void HandleTutorialDetected()
    {
        switch (_config.Automation.TutorialHandling)
        {
            case TutorialHandling.Stop:
                Logger.Error("Tutorial detected. TutorialHandling=Stop → bot halted.");
                Logger.Error("Complete the tutorial in the game client first.");
                _auto = AutoState.Stopped;
                SessionStats.SetState("Stopped", "Tutorial not completed");
                break;

            case TutorialHandling.Warn:
                Logger.Warn("Tutorial detected. TutorialHandling=Warn → idling.");
                Logger.Warn("Complete the tutorial in the game client first.");
                Logger.Warn("Then restart the bot, or change tutorialHandling to \"Skip\".");
                _auto = AutoState.Idle;
                SessionStats.SetState("Idle", "Tutorial: complete manually");
                break;

            case TutorialHandling.Skip:
                Logger.Warn("Tutorial detected (TutorialHandling=Skip). Proceeding anyway.");
                // Attempt to continue normally; the server may still block us
                StartDungeonLoopIfConfigured();
                break;
        }
    }

    #endregion

    #region DALC response handlers

    // PlayerDALC (dal0=21)
    static void HandlePlayerDALC(SFSObject r, int act)
    {
        switch (act)
        {
            case 1: // LOGIN_PLATFORM — server confirms identity and auto-selects character
                Logger.Debug($"PlayerDALC LOGIN_PLATFORM keys: {string.Join(", ", r.GetKeys())}");
                TryParseCurrency(r);

                if (r.ContainsKey("pla3"))
                {
                    _playerId = r.GetInt("pla3");
                    Logger.Info($"PlayerDALC LOGIN_PLATFORM OK – playerID={_playerId}");
                    SavePlayerId(_playerId);
                }
                if (r.ContainsKey("cha1"))
                {
                    _myCharId = r.GetInt("cha1");
                    Logger.Info($"[LOGIN] charID={_myCharId}");
                }
                if (r.ContainsKey("act1")) try {
                    var a = r.GetSFSObject("act1");
                    if (a.ContainsKey("cha1"))
                    {
                        _myCharId = a.GetInt("cha1");
                        Logger.Info($"[LOGIN] charID(act1)={_myCharId}");
                    }
                } catch { }

                if (r.ContainsKey("cha11"))
                {
                    bool[] flags = r.GetBoolArray("cha11");
                    Logger.Debug($"cha11 feature flags ({flags.Length}): [{string.Join(", ", flags)}]");
                }

                // cha62 carries the character's saved teams (all types).
                // Extract the PVE team (team0=1) for use in dungeon entry packets.
                ParseAndStorePveTeam(r);

                if (_playerId != -1)
                {
                    _login = LoginState.LoadingXmls;
                    SendLoadXmlsPacket();
                }
                else
                {
                    Logger.Error("No playerID in LOGIN_PLATFORM response. Dump:");
                    Logger.Info(r.GetDump());
                }
                break;

            default:
                Logger.Debug($"PlayerDALC act0={act}");
                break;
        }
    }

    // UserDALC (dal0=4)
    static void HandleUserDALC(SFSObject r, int act)
    {
        switch (act)
        {
            case 5: // LOAD_XMLS — session fully active
                _login = LoginState.InGame;
                _lastHeartbeat = DateTime.UtcNow;
                Logger.Info("In-game. Beginning automation sequence.");
                SessionStats.SetState("InGame");
                TryParseCurrency(r);
                // Some server configurations embed character data in LOAD_XMLS.
                // Parse cha62 here as a fallback in case it wasn't in LOGIN_PLATFORM.
                if (_savedTeammates.Count == 0)
                    ParseAndStorePveTeam(r);
                OnInGame?.Invoke();
                BeginAutomation();
                break;

            case 4: // LOGOUT acknowledged
                Logger.Info("Logged out.");
                break;

            default:
                Logger.Debug($"UserDALC act0={act}");
                break;
        }
    }

    // GameDALC (dal0=0)
    static void HandleGameDALC(SFSObject r, int act)
    {
        switch (act)
        {
            case 0: // ENTER_BATTLE — server is starting a new battle wave
                _lastBattleEvent = DateTime.UtcNow;
                Logger.Info("[DUNGEON] ENTER_BATTLE received");
                OnEnterBattle(r);
                break;

            case 1: // ENTER_DUNGEON  — server→client dungeon session active (zone dungeon)
            case 2: // ENTER_INSTANCE — server→client dungeon session active (instance dungeon, room name "I:NNN")
            {
                string label = act == 1 ? "ENTER_DUNGEON" : "ENTER_INSTANCE";
                Logger.Info($"[DUNGEON] {label} received — session active");

                // Update the dungeon room reference (may already be set from ROOM_JOIN).
                if (r.ContainsKey("roo1"))
                {
                    int rid = r.GetInt("roo1");
                    var newRoom = _sfs!.GetRoomById(rid);
                    if (newRoom != null && (_dungeonRoom == null || _dungeonRoom.Id != rid))
                    {
                        _dungeonRoom = newRoom;
                        Logger.Info($"[DUNGEON] Room updated: {_dungeonRoom.Name}");
                    }
                }

                // Parse the dungeon grid from this packet.
                // ENTER_DUNGEON (act0=1) carries the full dun0 array for a zone-based dungeon.
                // Always do a clean parse here — this is the authoritative initial state.
                ParseDungeonState(r, clearFirst: true);

                // Notify GUI with parsed object list
                OnDungeonLoaded?.Invoke(_dungeonObjects.AsReadOnly());

                _auto = AutoState.WaitingForBattle;
                _lastBattleEvent = DateTime.UtcNow;
                SessionStats.SetState("WaitingForBattle", "Dungeon active — activating first enemy");

                // Server may embed an immediate first wave in the ENTER_DUNGEON packet (rare).
                // Otherwise activate the first enemy now — this is what CheckAutoPilot() does in
                // the real client immediately after the dungeon finishes loading.
                if (r.ContainsKey("bat0"))
                    OnEnterBattle(r);
                else
                    SendDungeonActivate();
                break;
            }

            case 3: // NOTIFICATION
                string note = r.ContainsKey("not1") ? r.GetUtfString("not1") : "(empty)";
                Logger.Info($"[NOTIFY] {note}");
                break;

            case 4: // GAME_UPDATE
                Logger.Debug("[GAME_UPDATE] received");
                TryParseCurrency(r);
                break;

            case 5: // Dual-use: (a) zone-entry ACK before dungeon, (b) energy update once inside
                if (_auto != AutoState.EnteringDungeon)
                {
                    // Already past zone entry — this is the server's energy-deduction update.
                    Logger.Debug("[GAME] Energy update received (act0=5 inside dungeon session).");
                    TryParseCurrency(r);
                    break;
                }

                // Zone-entry ACK: server confirmed ENTER_ZONE_NODE.
                Logger.Info("[DUNGEON] Zone node entry confirmed (act0=5)");
                _retryCount = 0;
                SessionStats.SetRetryCount(0);
                TryParseCurrency(r);

                // Server auto-joins us to the dungeon room; ENTER_DUNGEON (act0=1) will arrive
                // shortly via ServerExtension or DungeonExtension.
                if (r.ContainsKey("roo1"))
                {
                    int roomId = r.GetInt("roo1");
                    string roomName = r.ContainsKey("roo0") ? r.GetUtfString("roo0") : "?";
                    Logger.Info($"[DUNGEON] roo1 found in act0=5 → JoinRoomRequest id={roomId} name={roomName}");
                    _sfs!.Send(new JoinRoomRequest(roomId));
                }
                else
                {
                    Logger.Debug("[DUNGEON] No roo1 in act0=5 — awaiting server-side room join + ENTER_DUNGEON");
                    _auto = AutoState.WaitingForBattle;
                    _lastBattleEvent = DateTime.UtcNow;
                    SessionStats.SetState("WaitingForBattle", "Waiting for ENTER_DUNGEON");
                }
                break;

            case 7: // PLAYERS_ONLINE
                if (r.ContainsKey("serv5"))
                {
                    int cnt = r.GetInt("serv5");
                    Logger.Info($"[ONLINE] {cnt} players online");
                    OnPlayersOnline?.Invoke(cnt);
                }
                break;

            case 8: Logger.Debug("[EVENT] Player logged in.");  break;
            case 9: Logger.Debug("[EVENT] Player logged out."); break;

            case 0xA: // PLAYER_UPDATE
                Logger.Debug("[PLAYER_UPDATE] received");
                TryParseCurrency(r);
                break;

            case 0xB: // DAILY_QUESTS_UPDATE
                Logger.Debug("[BOUNTY] Daily quests updated by server.");
                break;

            case 0xC: // IDLE_RESPONSE — keep-alive acknowledged
                Logger.Debug("[IDLE] Keep-alive acknowledged.");
                break;

            case 0xE: // RECONNECT_DUNGEON response — fire-and-forget; automation already started
                Logger.Info("[DUNGEON] Reconnect/abandon acknowledged.");
                break;

            case 0x11: // HERO_FROZEN — server-side rate-limit signal
                Logger.Warn("[RATE-LIMIT] HERO_FROZEN received. Pausing 60 s.");
                ResetDungeonState();
                ScheduleCooldown(60_000, AutoState.EnteringDungeon);
                SessionStats.SetState("CooldownBeforeRepeat", "HERO_FROZEN – 60s pause");
                break;

            default:
                Logger.Debug($"GameDALC act0={act}");
                break;
        }
    }

    // CharacterDALC (dal0=1)
    static void HandleCharacterDALC(SFSObject r, int act)
    {
        switch (act)
        {
            case 5: // DAILY_REWARD
                Logger.Info("[+] Daily reward claimed.");
                SessionStats.RecordDailyClaimed();
                TryParseCurrency(r);
                AdvanceAutomationAfterDailyReward();
                break;

            case 0xD: // DAILY_QUEST_CHECK
                ParseAndQueueDailyQuests(r);
                LootNextQueuedQuestOrProceed();
                break;

            case 0xE: // DAILY_QUEST_LOOT
                Logger.Info("[+] Daily quest reward looted.");
                TryParseCurrency(r);
                LootNextQueuedQuestOrProceed();
                break;

            case 0x4E: // UPDATE_DAILY_REWARDS_CONSUMABLE_PERIODIC
                Logger.Debug("Daily consumable rewards refreshed.");
                break;

            case 0x14: // INVENTORY_CHECK
                Logger.Debug("Inventory check completed.");
                TryParseCurrency(r);
                break;

            default:
                Logger.Debug($"CharacterDALC act0={act}");
                break;
        }
    }

    // BattleDALC (dal0=10)
    static void HandleBattleDALC(SFSObject r, int act)
    {
        switch (act)
        {
            case 1:
            {
                // QUEUE — server packs battle animation events into bat3 SFSArray.
                // bat3 event act0 values (from Battle.cs RunQueue switch):
                //   0x01 = DoActionDelay            0x07 = DoActionTurnStart ← our turn
                //   0x02 = DoActionHealthChange      0x08 = DoActionTurnEnd
                //   0x03 = DoActionMeterChange       0x09 = DoActionDeathChange
                //   0x04 = DoActionMeterGain         0x0A = DoActionComplete  ← round done
                //   0x05 = DoActionBegin             0x0B = DoActionVictory   ← encounter won
                //   0x06 = DoActionAbility           0x0C = DoActionDefeat    ← encounter lost
                //   0x0F = DoActionCaptureSet        ← familiar capture prompt (bat7=entityIdx)
                //   0x15 = DoActionResults           ← dungeon combined result (bat47=win)
                //
                // Per-round flow: send AUTO → server processes round → QUEUE arrives.
                // If QUEUE has no 0xA/0xB/0xC/0x15 → more rounds remain → send AUTO again.
                // If 0xA only (Complete) → all rounds done, send RESULTS to collect loot.
                //   Server may then send ENTER_BATTLE (next wave of multi-wave encounter)
                //   or QUEUE [0xB/0x15] (encounter fully over).
                // If 0xB/0xC/0x15 → encounter fully resolved; DungeonExtension follows with
                //   OBJECT_REMOVE then ITEMS_ADDED → we activate the next enemy.
                // If 0x0F (CaptureSet) → auto-decline the familiar capture, then continue.
                _lastBattleEvent = DateTime.UtcNow;

                bool hasComplete   = false;
                bool hasVictory    = false;
                bool hasDefeat     = false;
                bool hasResults    = false;
                bool resultsIsWin  = false;  // bat47 value inside 0x15 event
                int  captureEntity = -1;     // bat7 from 0x0F CaptureSet event

                if (r.ContainsKey("bat3"))
                {
                    try
                    {
                        var events = r.GetSFSArray("bat3");
                        Logger.Debug($"[BATTLE] QUEUE bat3 has {events.Size()} event(s).");
                        for (int i = 0; i < events.Size(); i++)
                        {
                            var ev    = events.GetSFSObject(i);
                            int evAct = ev.ContainsKey("act0") ? ev.GetInt("act0") : -1;
                            Logger.Debug($"[BATTLE]   bat3[{i}] act0=0x{evAct:X}");
                            switch (evAct)
                            {
                                case 0xA:  hasComplete  = true; break;
                                case 0xB:  hasVictory   = true; break;
                                case 0xC:  hasDefeat    = true; break;
                                case 0xF:  // DoActionCaptureSet — familiar capture prompt
                                    captureEntity = ev.ContainsKey("bat7") ? ev.GetInt("bat7") : 0;
                                    break;
                                case 0x15: // DoActionResults — dungeon combined result
                                    hasResults   = true;
                                    resultsIsWin = ev.ContainsKey("bat47") && ev.GetBool("bat47");
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[BATTLE] bat3 parse error: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Debug("[BATTLE] QUEUE: no bat3 array.");
                }

                // Priority: terminal events > capture prompt > round continuation.
                if (hasVictory || hasDefeat || hasResults)
                {
                    // Real client flow after each individual encounter:
                    //   Victory (0x0B/0x15-win): CheckAutoPilot → DoObjectActivate for next enemy.
                    //     The server sends OBJECT_REMOVE only when it processes the next DoObjectActivate,
                    //     NOT automatically. Bot must send DoObjectActivate immediately to unblock server.
                    //   Defeat (0x0C/0x15-loss): server broadcasts DungeonExtension PLAYER_DEFEAT (act0=2)
                    //     → our case-2 handler sends DoPlayerExit.
                    // DoPlayerExit is ONLY sent at dungeon end (DUNGEON_COMPLETE) or on defeat.
                    bool win = hasVictory || (hasResults && resultsIsWin);
                    string tag = hasVictory ? "0x0B" : hasResults ? "0x15" : "0x0C";
                    _auto = AutoState.WaitingForBattle;
                    _lastBattleEvent = DateTime.UtcNow;
                    if (win)
                    {
                        // Real client: onCompleteBattleTransitionComplete → CheckAutoPilot → DoObjectActivate.
                        // The server sends OBJECT_REMOVE only AFTER it receives the next DoObjectActivate,
                        // not automatically after battle end. Send activate immediately to unblock the server.
                        Logger.Info($"[BATTLE] Encounter WON ({tag}) — sending DoObjectActivate for next enemy.");
                        SendDungeonActivate();
                    }
                    else
                    {
                        // Defeat: wait for DungeonExtension PLAYER_DEFEAT broadcast (act0=2).
                        // Our case-2 handler will call SendPlayerExit from there.
                        Logger.Info($"[BATTLE] Encounter LOST ({tag}) — awaiting PLAYER_DEFEAT broadcast.");
                    }
                }
                else if (hasComplete)
                {
                    // 0xA: DoActionComplete — no more turns this round; no victory yet.
                    // Real client: when _completed=true and _results=false → calls doResults().
                    // This finalises the round; server may respond with ENTER_BATTLE (next wave)
                    // or QUEUE [0x15] (encounter done).
                    Logger.Info("[BATTLE] Round complete (0xA) — sending RESULTS.");
                    _auto = AutoState.WaitingForResults;
                    SendBattleResults();
                }
                else if (captureEntity >= 0 && _config.Automation.AutoDeclineCaptures)
                {
                    // 0x0F: CaptureSet — server paused battle for familiar capture prompt.
                    // Decline immediately; server will send another QUEUE to resume combat.
                    Logger.Info($"[BATTLE] CaptureSet (0x0F) — auto-declining entity {captureEntity}.");
                    DeclineCapture(captureEntity);
                    _auto = AutoState.AutoBattleActive;
                }
                else
                {
                    // No terminal event and no capture — battle ongoing (multi-round AUTO).
                    // 0x07 (DoActionTurnStart) signals it is the player's turn.
                    // Send AUTO; server processes the round and replies with another QUEUE.
                    int battleNum = _waveCount;
                    Logger.Info($"[BATTLE] Round ongoing — sending AUTO (battle {battleNum}).");
                    _auto = AutoState.AutoBattleActive;
                    SessionStats.SetState("AutoBattleActive", $"AUTO (battle {battleNum})");
                    SendBattleAuto(GetCurrentDungeon().UseDamageGain);
                }
                break;
            }

            case 4:
            {
                // RESULTS response — server acknowledged our doResults() call.
                // Server will now send OBJECT_REMOVE + ITEMS_ADDED (if it hasn't already)
                // or another QUEUE with 0x15 (DoActionResults).
                Logger.Info("[DUNGEON] RESULTS ack (act0=4) received.");
                _lastBattleEvent = DateTime.UtcNow;
                TryParseCurrency(r);
                if (r.ContainsKey("bat47"))
                    Logger.Info($"[DUNGEON]   bat47={r.GetBool("bat47")}");
                _auto = AutoState.WaitingForBattle;
                // Recovery: if OBJECT_REMOVE already arrived while we were in WaitingForResults
                // (and was skipped by the old state check — now fixed), try activating now.
                // SendDungeonActivate() is a no-op if _pendingActivation is already true or
                // if there are no remaining enemies.
                if (!_pendingActivation)
                    SendDungeonActivate();
                break;
            }

            case 5:
            {
                // AUTO echo — server may echo this in some non-dungeon battle modes.
                // In dungeon mode this is rare; log and wait.
                _lastBattleEvent = DateTime.UtcNow;
                bool waveVictory = r.ContainsKey("bat47") && r.GetBool("bat47");
                Logger.Info($"[BATTLE] AUTO result (act0=5) — victory={waveVictory}");
                SessionStats.SetState("WaitingForBattle", "AUTO result received");

                if (_config.Automation.AutoDeclineCaptures &&
                    r.ContainsKey("bat33") && r.GetBool("bat33"))
                {
                    int eIdx = r.ContainsKey("bat7") ? r.GetInt("bat7") : 0;
                    Logger.Info($"[BATTLE] Auto-declining capture (entity {eIdx})");
                    DeclineCapture(eIdx);
                }
                _auto = AutoState.WaitingForBattle;
                break;
            }

            case 7: // QUIT acknowledged
                Logger.Info("[BATTLE] Quit acknowledged.");
                ResetDungeonState();
                _auto = AutoState.Idle;
                SessionStats.SetState("Idle", "Battle quit");
                break;

            case 8: // CAPTURE_ACCEPT response
                Logger.Debug("[BATTLE] Capture accepted.");
                break;

            case 9: // CAPTURE_DECLINE response — server will send QUEUE again for our turn
                Logger.Debug("[BATTLE] Capture declined.");
                break;

            default:
                Logger.Debug($"BattleDALC act0={act}");
                if (r.ContainsKey("bat47"))
                    Logger.Debug($"  embedded bat47={r.GetBool("bat47")}");
                break;
        }
    }

    // ChatDALC (dal0=8)
    static void HandleChatDALC(SFSObject r, int act)
    {
        switch (act)
        {
            case 1: // CHAT_MESSAGE
            case 2: // PRIVATE_MESSAGE
                string sender  = r.ContainsKey("cha2")  ? r.GetUtfString("cha2")  : "?";
                string text    = r.ContainsKey("chat0") ? r.GetUtfString("chat0") : "";
                bool   isPriv  = act == 2;
                Logger.Info($"{(isPriv ? "[PM]" : "[CHAT]")} {sender}: {text}");
                OnChatMessage?.Invoke(sender, text, isPriv);
                break;

            default:
                Logger.Debug($"ChatDALC act0={act}");
                break;
        }
    }

    // GuildDALC (dal0=9)
    static void HandleGuildDALC(SFSObject r, int act) =>
        Logger.Debug($"GuildDALC act0={act}");

    // DungeonExtension — server→client events sent to the dungeon room (no dal0 field).
    // action constants match DungeonExtension.ParseSFSObject() switch in the decompiled client.
    static void HandleDungeonExtension(SFSObject r, int act)
    {
        switch (act)
        {
            case 1: // PLAYER_EXIT broadcast — server echoes act0=1 to all room members after a
            {       // player exits a battle and returns to dungeon navigation.
                    // Real client OnPlayerExit(): if dun8 == our charId → fire _dungeon.COMPLETE
                    //   which (in auto-pilot mode) calls CheckAutoPilot() for the next object.
                int charId = r.ContainsKey("dun8") ? r.GetInt("dun8") : -1;
                // isOurs: true when dun8 matches our char ID, OR when dun8 is absent / we don't
                // know our char ID yet (solo dungeon — the broadcast must be ours).
                bool isOurs = charId == -1
                              || _myCharId <= 0
                              || charId == _myCharId;
                Logger.Debug($"[DUNGEON-EXT] PLAYER_EXIT_BROADCAST dun8={charId} ours={isOurs}");
                if (isOurs && _auto != AutoState.AutoBattleActive && !_pendingActivation)
                {
                    // Our character has returned to dungeon navigation — trigger the next enemy.
                    // This is the equivalent of _dungeon.COMPLETE → CheckAutoPilot() in the real client.
                    Logger.Debug("[DUNGEON-EXT] Our PLAYER_EXIT confirmed — calling SendDungeonActivate.");
                    SendDungeonActivate();
                }
                break;
            }

            case 2: // PLAYER_DEFEAT — server broadcast: a player was defeated (dun8 = their character ID)
            {   // Real client DungeonExtension.OnPlayerDefeat(): if dun8==ourCharId → DoPlayerExit().
                // DoPlayerExit formally ends the dungeon session on defeat.
                int charId = r.ContainsKey("dun8") ? r.GetInt("dun8") : -1;
                bool isOurs = charId == -1 || _myCharId <= 0 || charId == _myCharId;
                Logger.Info($"[DUNGEON-EXT] PLAYER_DEFEAT dun8={charId} ours={isOurs}");
                if (isOurs)
                {
                    Logger.Info("[DUNGEON-EXT] Our character was defeated — sending PLAYER_EXIT.");
                    _auto = AutoState.WaitingForBattle;
                    SendPlayerExit();
                }
                break;
            }

            case 3: // OBJECT_ADD — server dynamically added object(s) to the dungeon grid.
            {       // Real client OnObjectAdd(): parses dun0 array → adds to grid → calls CheckAutoPilot.
                    // This arrives for dungeons whose ENTER_INSTANCE carried an empty/partial dun0.
                TryParseCurrency(r);
                if (r.ContainsKey("dun0"))
                {
                    try
                    {
                        var added = r.GetSFSArray("dun0");
                        for (int i = 0; i < added.Size(); i++)
                        {
                            var o     = added.GetSFSObject(i);
                            int oRow  = o.ContainsKey("dun1")  ? o.GetInt("dun1")  : 0;
                            int oCol  = o.ContainsKey("dun2")  ? o.GetInt("dun2")  : 0;
                            int oType = o.ContainsKey("dun14") ? o.GetInt("dun14") : 0;
                            bool oUsed = o.ContainsKey("dun32") && o.GetBool("dun32");
                            bool oEmp  = o.ContainsKey("dun28") && o.GetBool("dun28");
                            Logger.Debug($"[DUNGEON-EXT] OBJECT_ADD ({oRow},{oCol}) type={oType} used={oUsed} empty={oEmp}");
                            // Replace existing entry for the same cell, or append.
                            int idx = _dungeonObjects.FindIndex(x => x.Row == oRow && x.Col == oCol);
                            var info = new DungeonObjectInfo(oRow, oCol, oType, oUsed, oEmp);
                            if (idx >= 0) _dungeonObjects[idx] = info;
                            else          _dungeonObjects.Add(info);
                        }
                        Logger.Debug($"[DUNGEON-EXT] After OBJECT_ADD: {_dungeonObjects.Count} total objects");
                    }
                    catch (Exception ex) { Logger.Debug($"[DUNGEON-EXT] OBJECT_ADD parse error: {ex.Message}"); }
                }
                // Mirror CheckAutoPilot(): always attempt activation after objects are added.
                if (_auto != AutoState.AutoBattleActive && !_pendingActivation)
                    SendDungeonActivate();
                break;
            }

            case 4: // OBJECT_REMOVE — object destroyed/looted; mark as used in our grid
            {
                int  row       = r.ContainsKey("dun1")  ? r.GetInt("dun1")  : -1;
                int  col       = r.ContainsKey("dun2")  ? r.GetInt("dun2")  : -1;
                // dun32 semantics (from DungeonExtension.OnObjectRemove decompile):
                //   dun32=FALSE → enemy/boss removed → CheckAutoPilot() IS called immediately
                //   dun32=TRUE  → treasure/shrine removed (plays sound) → CheckAutoPilot NOT called
                //                 (OnItemsAdded opens loot window → OnItemWindowClosed → CheckAutoPilot)
                bool isLootObj = r.ContainsKey("dun32") && r.GetBool("dun32");
                if (row >= 0) MarkDungeonObjectUsed(row, col);
                Logger.Debug($"[DUNGEON-EXT] OBJECT_REMOVE ({row},{col}) isLootObj={isLootObj}");
                // Mirror CheckAutoPilot() exactly: the real client has NO state check here —
                // it only checks extension.waiting (= _pendingActivation) and autoPilot=true.
                // Do NOT gate on _auto: OBJECT_REMOVE can arrive while _auto=WaitingForResults
                // (server sends OBJECT_REMOVE before the BattleDALC RESULTS ack), and we must
                // not miss it. Only skip if currently mid-fight or already activating.
                if (!isLootObj && _auto != AutoState.AutoBattleActive && !_pendingActivation)
                    SendDungeonActivate();
                break;
            }

            case 5: // OBJECT_ACTIVATE ACK — server echoes act0=5 back; no action needed.
                Logger.Debug("[DUNGEON-EXT] OBJECT_ACTIVATE ack");
                break;

            case 6: // ENTITY_VALUES — entity (player/enemy) full stat block
            case 7: // ENTITY_UPDATE — partial entity update
                Logger.Debug($"[DUNGEON-EXT] ENTITY act0={act}");
                break;

            case 9: // DUNGEON_COMPLETE — server confirms all enemies in the dungeon are defeated.
                // Real client: CheckAutoPilot() finds IsCleared() → ShowCleared() → ShowDungeonCompleteWindow()
                //   → user/autopilot clicks OK → DoPlayerExit() (DungeonExtension act0=1).
                // Bot: we receive DUNGEON_COMPLETE, record the run, then send PLAYER_EXIT to formally
                // close the dungeon session before resetting state.
                Logger.Info("[DUNGEON-EXT] DUNGEON_COMPLETE received.");
                TryParseCurrency(r);
                _awaitingDungeonComplete = false;
                if (_auto is AutoState.WaitingForBattle or AutoState.AutoBattleActive
                                                        or AutoState.WaitingForResults)
                {
                    // Send PLAYER_EXIT (DungeonExtension act0=1) to formally close the dungeon.
                    // Must be done BEFORE ResetDungeonState() clears _dungeonRoom.
                    Logger.Debug("[DUNGEON-EXT] Sending PLAYER_EXIT to formally close dungeon session.");
                    SendPlayerExit();

                    _runCount++;
                    var cfg = GetCurrentDungeon();
                    var dunResult = new DungeonResult
                    {
                        Victory = true, Loot = new(), RunNumber = _runCount,
                        ZoneId = cfg.ZoneId, NodeId = cfg.NodeId,
                        DifficultyId = cfg.DifficultyId, Waves = _waveCount
                    };
                    SessionStats.RecordRun(dunResult);
                    OnDungeonComplete?.Invoke(dunResult);
                    ResetDungeonState();
                    ScheduleNextRun();
                }
                break;

            case 11: // ITEMS_ADDED — loot added to inventory mid-dungeon
                Logger.Debug("[DUNGEON-EXT] ITEMS_ADDED");
                TryParseCurrency(r);
                // Secondary activation trigger: mirrors OnItemWindowClosed() → CheckAutoPilot().
                // Real client: item window auto-closes after 1.5s → CheckAutoPilot (no state check).
                // Same rule as OBJECT_REMOVE: no _auto state gate, only guard mid-fight + pending.
                if (_auto != AutoState.AutoBattleActive && !_pendingActivation)
                    SendDungeonActivate();
                break;

            case 12: // ITEMS_REMOVED
                Logger.Debug("[DUNGEON-EXT] ITEMS_REMOVED");
                break;

            case 13: // OBJECT_DISABLE — object no longer interactable (occupied, spent, etc.)
                // DungeonExtension.OnObjectDisable() always calls CheckAutoPilot() with no state check.
                Logger.Debug("[DUNGEON-EXT] OBJECT_DISABLE");
                if (_auto != AutoState.AutoBattleActive && !_pendingActivation)
                    SendDungeonActivate();
                break;

            case 14: // ERROR
                int errCode = r.ContainsKey("err0") ? r.GetInt("err0") : -1;
                Logger.Warn($"[DUNGEON-EXT] ERROR err0={errCode}");
                break;

            case 15: // CURRENCY update
                Logger.Debug("[DUNGEON-EXT] CURRENCY");
                TryParseCurrency(r);
                break;

            default:
                Logger.Debug($"[DUNGEON-EXT] act0={act} keys: {string.Join(", ", r.GetKeys())}");
                break;
        }
    }

    #endregion

    #region Battle wave logic

    /// <summary>
    /// Processes a new battle wave: auto-declines any pre-existing capture prompt,
    /// then sends AUTO.
    /// </summary>
    static void OnEnterBattle(SFSObject r)
    {
        // Clear pending flag — our DoObjectActivate was accepted, battle has begun.
        _pendingActivation = false;
        _waveCount++;
        _lastBattleEvent = DateTime.UtcNow;
        _auto = AutoState.AutoBattleActive;
        SessionStats.SetWave(_waveCount);
        SessionStats.SetState("AutoBattleActive", $"Battle {_waveCount} — sending AUTO");
        Logger.Info($"[BATTLE] Battle {_waveCount} starting — sending AUTO");

        // bat33=true in ENTER_BATTLE means a capture prompt is already waiting.
        // Decline it; server will send a QUEUE that resumes normal combat.
        if (_config.Automation.AutoDeclineCaptures &&
            r.ContainsKey("bat33") && r.GetBool("bat33"))
        {
            int eIdx = r.ContainsKey("bat7") ? r.GetInt("bat7") : 0;
            Logger.Info($"[BATTLE] Capture prompt on entry — auto-declining entity {eIdx}");
            DeclineCapture(eIdx);
            return;
        }

        SendBattleAuto(GetCurrentDungeon().UseDamageGain);
    }

    #endregion

    #region Automation state machine

    static void BeginAutomation()
    {
        // Fire-and-forget: cancel any orphaned dungeon session.
        // Server may or may not respond; case 0xE just logs and does not re-trigger this.
        if (_config.Automation.AbandonOrphanedDungeon)
            ReconnectDungeon(cancel: true);

        StartDailyRewardSequence();
    }

    static void StartDailyRewardSequence()
    {
        if (_config.Automation.AutoClaimDailyReward)
        {
            _auto = AutoState.ClaimingDailyReward;
            SessionStats.SetState("ClaimingDailyReward", "Claiming daily reward");
            ClaimDailyReward();
        }
        else
        {
            AdvanceAutomationAfterDailyReward();
        }
    }

    static void AdvanceAutomationAfterDailyReward()
    {
        if (_config.Automation.AutoClaimDailyQuests)
        {
            _auto = AutoState.CheckingDailyQuests;
            SessionStats.SetState("CheckingDailyQuests", "Checking daily quests");
            CheckDailyQuests();
        }
        else
        {
            SetIdleAfterDailies();
        }
    }

    static void LootNextQueuedQuestOrProceed()
    {
        if (_questsToLoot.Count > 0)
        {
            var quest = _questsToLoot.Dequeue();
            _auto = AutoState.ClaimingDailyQuest;
            SessionStats.SetState("ClaimingDailyQuest", $"Looting quest {quest.QuestId}");
            LootDailyQuest(quest.QuestId);
        }
        else
        {
            Logger.Info("All daily quests processed.");
            SetIdleAfterDailies();
        }
    }

    static void SetIdleAfterDailies()
    {
        _auto = AutoState.Idle;
        SessionStats.SetState("Idle", "Ready — press Run Dungeons");
        Logger.Info("Daily tasks complete. Press \"Run Dungeons\" to start the loop.");
    }

    static void StartDungeonLoopIfConfigured()
    {
        var q = _config.DungeonQueue;
        bool anyEnabled = q?.Any(d => d.Enabled) == true;
        if (!anyEnabled)
        {
            Logger.Info("Dungeon queue is empty or all disabled. Idling.");
            _auto = AutoState.Idle;
            SessionStats.SetState("Idle", "Dungeon disabled");
            return;
        }

        // Ensure the current slot is enabled; skip to next if not
        if (!GetCurrentDungeon().Enabled)
            AdvanceQueueToNextEnabled();

        if (_inTutorial) { HandleTutorialDetected(); return; }

        var cfg = GetCurrentDungeon();
        if (cfg.MaxRuns >= 0 && _runCount >= cfg.MaxRuns)
        {
            Logger.Info($"MaxRuns ({cfg.MaxRuns}) reached. Stopping dungeon loop.");
            _auto = AutoState.Stopped;
            SessionStats.SetState("Stopped", "MaxRuns reached");
            return;
        }

        // Pre-entry resource check: avoid wasting a retry if we already know we can't afford entry.
        // NextEnergyAt/NextTicketAt return DateTime.MinValue until regen data has been received,
        // so these guards only fire once we have confirmed currency state from the server.
        if (SessionStats.Energy == 0 && SessionStats.NextEnergyAt > DateTime.MinValue)
        {
            var next = SessionStats.NextEnergyAt;
            int waitMs = next > DateTime.UtcNow
                ? (int)(next - DateTime.UtcNow).TotalMilliseconds + 5_000
                : _config.Automation.EnergyWaitMinutes * 60_000;
            Logger.Warn($"Energy is 0 — skipping entry. " +
                        $"Next regen: {(next > DateTime.UtcNow ? next.ToString("HH:mm:ss") + " UTC" : "unknown")}.");
            _auto = AutoState.EnergyWait;
            SessionStats.SetState("EnergyWait", "Waiting for energy regen");
            ScheduleCooldown(waitMs, AutoState.EnteringDungeon);
            return;
        }

        if (SessionStats.Tickets == 0 && SessionStats.NextTicketAt > DateTime.MinValue)
        {
            var next = SessionStats.NextTicketAt;
            int waitMs = next > DateTime.UtcNow
                ? (int)(next - DateTime.UtcNow).TotalMilliseconds + 5_000
                : _config.Automation.EnergyWaitMinutes * 60_000;
            Logger.Warn($"Tickets are 0 — skipping entry. " +
                        $"Next regen: {(next > DateTime.UtcNow ? next.ToString("HH:mm:ss") + " UTC" : "unknown")}.");
            _auto = AutoState.EnergyWait;
            SessionStats.SetState("EnergyWait", "Waiting for ticket regen");
            ScheduleCooldown(waitMs, AutoState.EnteringDungeon);
            return;
        }

        _auto = AutoState.EnteringDungeon;
        SessionStats.SetQueuePosition(_dungeonQueueIndex, q!.Count);
        SessionStats.SetState("EnteringDungeon", $"Z{cfg.ZoneId}/N{cfg.NodeId}");
        EnterZoneNode(cfg);
    }

    static void ScheduleNextRun()
    {
        var q = _config.DungeonQueue;
        bool anyEnabled = q?.Any(d => d.Enabled) == true;
        if (!anyEnabled)
        {
            _auto = AutoState.Idle;
            SessionStats.SetState("Idle", "No dungeons enabled");
            return;
        }

        var current = GetCurrentDungeon();
        if (current.MaxRuns >= 0 && _runCount >= current.MaxRuns)
        {
            Logger.Info($"MaxRuns ({current.MaxRuns}) reached for this dungeon. Stopping.");
            _auto = AutoState.Stopped;
            SessionStats.SetState("Stopped", "MaxRuns reached");
            return;
        }

        // Advance to next enabled dungeon in the queue (wraps around)
        AdvanceQueueToNextEnabled();
        var next = GetCurrentDungeon();
        SessionStats.SetQueuePosition(_dungeonQueueIndex, q!.Count);
        Logger.Info($"Waiting {next.RepeatDelayMs} ms before next run (Z{next.ZoneId}/N{next.NodeId})...");
        ScheduleCooldown(next.RepeatDelayMs, AutoState.EnteringDungeon);
        SessionStats.SetState("CooldownBeforeRepeat", $"Next run in {next.RepeatDelayMs}ms");
    }

    static void ScheduleCooldown(int ms, AutoState resumeState)
    {
        _cooldownUntil       = DateTime.UtcNow.AddMilliseconds(ms);
        _stateAfterCooldown  = resumeState;
        _auto = AutoState.CooldownBeforeRepeat;
    }

    static void ResetDungeonState()
    {
        _waveCount                = 0;
        _lastBattleEvent          = DateTime.MinValue;
        _dungeonRoom              = null;
        _dungeonObjects.Clear();
        _navTargetRow             = -1;
        _navTargetCol             = -1;
        _pendingActivation        = false;
        _awaitingDungeonComplete  = false;
        SessionStats.SetWave(0);
    }

    static DungeonConfig GetCurrentDungeon()
    {
        var q = _config.DungeonQueue;
        if (q == null || q.Count == 0) return new DungeonConfig();
        return q[_dungeonQueueIndex % q.Count];
    }

    /// <summary>
    /// Advance <see cref="_dungeonQueueIndex"/> to the next enabled entry in the queue.
    /// Wraps around. Does nothing if no other enabled entry exists.
    /// </summary>
    static void AdvanceQueueToNextEnabled()
    {
        var q = _config.DungeonQueue;
        if (q == null || q.Count <= 1) return;
        int start = _dungeonQueueIndex;
        for (int i = 1; i <= q.Count; i++)
        {
            int next = (start + i) % q.Count;
            if (q[next].Enabled)
            {
                _dungeonQueueIndex = next;
                return;
            }
        }
        // No other enabled entry; stay on current
    }

    #endregion

    #region Tick-driven timers

    static void CheckHeartbeat()
    {
        if (_login != LoginState.InGame) return;
        if ((DateTime.UtcNow - _lastHeartbeat).TotalMilliseconds < HEARTBEAT_MS) return;
        _lastHeartbeat = DateTime.UtcNow;
        Send(Packet(DALC_GAME, 0xC));
        Logger.Debug("[IDLE] Keep-alive sent.");
    }

    static void CheckCooldowns()
    {
        if (_login != LoginState.InGame) return;

        // Cooldown expiry (between-run delay, energy wait, error backoff)
        if ((_auto == AutoState.CooldownBeforeRepeat || _auto == AutoState.EnergyWait) &&
            DateTime.UtcNow >= _cooldownUntil)
        {
            _auto = AutoState.Idle; // reset before transition to avoid re-entry
            if (_stateAfterCooldown == AutoState.EnteringDungeon)
                StartDungeonLoopIfConfigured();
            else
                _auto = _stateAfterCooldown;
        }

        // AutoBattleActive timeout: if QUEUE never arrives (or AUTO result never arrives),
        // something went wrong — reset and retry.
        if (_auto == AutoState.AutoBattleActive)
        {
            double elapsed = (DateTime.UtcNow - _lastBattleEvent).TotalMilliseconds;
            if (elapsed > BATTLE_DONE_TIMEOUT_MS)
            {
                Logger.Warn($"[BATTLE] No QUEUE/result in {BATTLE_DONE_TIMEOUT_MS}ms — resetting dungeon.");
                ResetDungeonState();
                _retryCount++;
                SessionStats.SetRetryCount(_retryCount);
                ScheduleCooldown(_config.Automation.RetryDelayMs, AutoState.EnteringDungeon);
            }
        }

        // Post-battle timeout: WaitingForBattle, ≥1 battle fought, no pending activation, and
        // no DUNGEON_COMPLETE / ENTER_BATTLE within timeout → dungeon stalled.
        // Send RESULTS as a nudge; server should reply with DUNGEON_COMPLETE or a final QUEUE.
        // Do NOT fire this when _pendingActivation=true (waiting for ENTER_BATTLE after DoObjectActivate)
        // or _awaitingDungeonComplete=true (all enemies cleared, waiting for DUNGEON_COMPLETE packet).
        if (_auto == AutoState.WaitingForBattle && _waveCount > 0 && !_pendingActivation && !_awaitingDungeonComplete)
        {
            double elapsed = (DateTime.UtcNow - _lastBattleEvent).TotalMilliseconds;
            if (elapsed > BATTLE_DONE_TIMEOUT_MS)
            {
                Logger.Info($"[BATTLE] Stall after {BATTLE_DONE_TIMEOUT_MS} ms — sending RESULTS as fallback nudge.");
                _auto = AutoState.WaitingForResults;
                _lastBattleEvent = DateTime.UtcNow;
                SendBattleResults();
            }
        }

        // WaitingForResults timeout: RESULTS sent but DUNGEON_COMPLETE never arrived.
        // Give the server extra time (30 s) then force-reset the run.
        if (_auto == AutoState.WaitingForResults)
        {
            double elapsed = (DateTime.UtcNow - _lastBattleEvent).TotalMilliseconds;
            if (elapsed > BATTLE_DONE_TIMEOUT_MS * 2)
            {
                Logger.Warn("[BATTLE] No DUNGEON_COMPLETE after RESULTS — force-resetting run.");
                ResetDungeonState();
                _retryCount++;
                SessionStats.SetRetryCount(_retryCount);
                ScheduleCooldown(_config.Automation.RetryDelayMs, AutoState.EnteringDungeon);
            }
        }

        // ENTER_DUNGEON / first-battle timeout:
        //   WaitingForBattle + 0 waves + no pending activation → ENTER_DUNGEON (or ENTER_BATTLE)
        //   never arrived after ENTER_ZONE_NODE was confirmed. Retry zone entry.
        //   Guard !_pendingActivation: if we already sent DoObjectActivate (first enemy), we are
        //   correctly waiting for ENTER_BATTLE — don't retry just because it hasn't fired yet.
        if (_auto == AutoState.WaitingForBattle && _waveCount == 0 && !_pendingActivation)
        {
            double elapsed = (DateTime.UtcNow - _lastBattleEvent).TotalMilliseconds;
            if (elapsed > BATTLE_DONE_TIMEOUT_MS)
            {
                Logger.Warn($"[DUNGEON] ENTER_DUNGEON not received after {BATTLE_DONE_TIMEOUT_MS} ms — retrying zone entry.");
                ResetDungeonState();
                _retryCount++;
                SessionStats.SetRetryCount(_retryCount);
                if (_retryCount >= _config.Automation.MaxRetries)
                {
                    Logger.Error("ENTER_DUNGEON never arrived. Max retries reached. Idling.");
                    _auto = AutoState.Idle;
                    SessionStats.SetState("Idle", "ENTER_DUNGEON timeout");
                }
                else
                {
                    ScheduleCooldown(_config.Automation.RetryDelayMs, AutoState.EnteringDungeon);
                }
            }
        }
    }

    #endregion

    #region Login packets

    // Step 2 – PlayerDALC LOGIN_PLATFORM (dal0=21, act0=1)
    // Field layout verified against live traffic dump.
    static void SendLoginPlatformPacket()
    {
        var p = new SFSObject();
        p.PutInt("act0", 1);
        if (_playerId != -1)
            p.PutInt("pla3", _playerId);
        p.PutUtfString("pla9",  "Guest_Game_Auth_Token"); // Kongregate placeholder
        p.PutUtfString("pla10", _steamId64);              // Steam 64-bit ID
        p.PutUtfString("pla11", _steamTicket);            // Steam auth ticket
        p.PutUtfString("pla12", _anonymousId);            // anonymous/device ID
        p.PutInt("pla2",  PLATFORM);
        p.PutInt("cli0",  CLI_PLATFORM);
        p.PutUtfString("pla4", "Windows 10  (10.0.19045) 64bit");
        p.PutUtfString("pla5", "en");
        p.PutUtfString("pla6", "");
        AddEnvelope(p, DALC_PLAYER);
        RawSend(p);
        _login = LoginState.AwaitingPlatformResp;
        Logger.Info("[>] PlayerDALC LOGIN_PLATFORM");
    }

    // Step 3 – UserDALC LOAD_XMLS (dal0=4, act0=5)
    // dal0001=1 is a deliberate typo present in the original client (not a typo in our code).
    static void SendLoadXmlsPacket()
    {
        var p = new SFSObject();
        p.PutInt("act0", 5);
        p.PutUtfString("cli2", SFS_VERSION);
        p.PutUtfString("use8", "en");
        p.PutInt("dal0001", 1);           // original client typo — must be present
        p.PutUtfString("cli1", APP_VERSION);
        p.PutInt("cli0", CLI_PLATFORM);
        p.PutInt("dal0", DALC_USER);
        p.PutUtfString("cli3", DLC_VERSION);
        RawSend(p);
        Logger.Info("[>] UserDALC LOAD_XMLS");
    }

    // UserDALC LOGOUT (dal0=4, act0=4)
    static void SendLogoutPacket()
    {
        var p = new SFSObject();
        p.PutUtfString("cli2", SFS_VERSION);
        p.PutInt("act0", 4);
        p.PutUtfString("cli1", APP_VERSION);
        p.PutInt("dal0", DALC_USER);
        RawSend(p);
        Logger.Info("[>] UserDALC LOGOUT");
    }

    #endregion

    #region Packet helpers

    /// <summary>
    /// Build an SFSObject with the standard envelope (dal0, cli1, cli2, cli3) and act0.
    /// Used for ServerExtension (DALC-routed) packets.
    /// </summary>
    static SFSObject Packet(int dalcId, int action)
    {
        var p = new SFSObject();
        p.PutInt("act0", action);
        AddEnvelope(p, dalcId);
        return p;
    }

    /// <summary>
    /// Build an SFSObject for DungeonExtension packets.
    /// Includes cli* version fields but NOT dal0 — DungeonExtension does not use DALC routing.
    /// Matches the real client's <c>DungeonExtension.Send()</c> which adds cli1/cli2/cli3 only.
    /// </summary>
    static SFSObject DungeonPacket(int action)
    {
        var p = new SFSObject();
        p.PutInt("act0", action);
        p.PutUtfString("cli2", SFS_VERSION);
        p.PutUtfString("cli1", APP_VERSION);
        p.PutUtfString("cli3", DLC_VERSION);
        return p;
    }

    static void AddEnvelope(SFSObject p, int dalcId)
    {
        p.PutUtfString("cli2", SFS_VERSION);
        p.PutInt("dal0", dalcId);
        p.PutUtfString("cli1", APP_VERSION);
        p.PutUtfString("cli3", DLC_VERSION);
    }

    static void Send(SFSObject p) => RawSend(p);

    static void RawSend(SFSObject p) =>
        _sfs!.Send(new ExtensionRequest(SFS_EXTENSION, p, null));

    /// <summary>
    /// Send a packet to the active dungeon room via "DungeonExtension".
    /// Use ONLY for dungeon navigation packets (DoObjectActivate, act0=5) that have NO dal0.
    /// BattleDALC packets (AUTO, RESULTS, QUIT, CAPTURE) go via <see cref="Send"/> instead —
    /// they carry dal0 and are routed by ServerExtension, not the room extension.
    /// Falls back to RawSend if no room is active.
    /// </summary>
    static void SendDungeon(SFSObject p)
    {
        if (_dungeonRoom != null)
            _sfs!.Send(new ExtensionRequest(DUNGEON_EXTENSION, p, _dungeonRoom));
        else
            RawSend(p);
    }

    /// <summary>
    /// DungeonExtension act0=1 — PLAYER_EXIT.
    /// Mirrors DoPlayerExit() in the real client. Called only when the entire dungeon
    /// is cleared (all enemies defeated) or when the player is defeated.
    /// The real client's DungeonExtension.Send() adds cli1/cli2/cli3, so we use DungeonPacket().
    /// </summary>
    static void SendPlayerExit()
    {
        var p = DungeonPacket(1);   // includes cli1, cli2, cli3 — matches real client DungeonExtension.Send()
        Logger.Debug("[DUNGEON] Sending PLAYER_EXIT (act0=1)");
        SendDungeon(p);
    }

    /// <summary>
    /// CharacterDALC act0=12 — lineup/team data sent immediately before ENTER_ZONE_NODE.
    /// team0=mode, team1/team6=party size, team2-5/8=feature toggles observed in traffic dump.
    /// </summary>
    static void SendCharacterLineup(DungeonConfig cfg)
    {
        var teammates = GetEffectiveTeammates(cfg);
        var p = Packet(DALC_CHARACTER, 12);
        p.PutInt("team0", 1);
        p.PutInt("team1", teammates.Count);
        p.PutInt("team6", teammates.Count);
        p.PutBool("team2", true);
        p.PutBool("team3", true);
        p.PutBool("team4", true);
        p.PutBool("team5", false);
        p.PutBool("team8", false);
        SerialiseTeammates(p, teammates);
        Send(p);
        Logger.Debug("[>] CharacterDALC LINEUP (act0=12)");
    }

    /// <summary>
    /// CharacterDALC act0=6 — client feature-flag state sent immediately after ENTER_DUNGEON.
    /// Values are the boolean flags observed in the real client traffic dump.
    /// </summary>
    static void SendCharacterStateConfirmation()
    {
        var p = Packet(DALC_CHARACTER, 6);
        p.PutBool("cha32",  false);
        p.PutBool("cha35",  true);
        p.PutInt ("cha36",  2);
        p.PutBool("cha40",  true);
        p.PutBool("cha82",  true);
        p.PutBool("cha48",  true);
        p.PutBool("cha93",  true);
        p.PutBool("cha133", true);
        p.PutBool("cha134", true);
        p.PutSFSObject("cha95", new SFSObject());
        Send(p);
        Logger.Debug("[>] CharacterDALC CHARACTER_STATE (act0=6)");
    }

    /// <summary>
    /// Parse the dungeon grid from the ENTER_DUNGEON (act0=1) packet.
    /// dun7 → player start position (dun9=row, dun10=col).
    /// dun0 → array of dungeon objects (dun1=row, dun2=col, dun14=type, dun32=used).
    /// </summary>
    static void ParseDungeonState(ISFSObject r, bool clearFirst = true)
    {
        _playerRow = 0; _playerCol = 0;
        if (clearFirst) _dungeonObjects.Clear();
        Logger.Debug($"[DUNGEON] ParseDungeonState(clearFirst={clearFirst}) packet keys: [{string.Join(", ", r.GetKeys())}]");

        // Player start position
        if (r.ContainsKey("dun7"))
        {
            try
            {
                var players = r.GetSFSArray("dun7");
                if (players.Size() > 0)
                {
                    var me = players.GetSFSObject(0);
                    _playerRow = me.ContainsKey("dun9")  ? me.GetInt("dun9")  : 0;
                    _playerCol = me.ContainsKey("dun10") ? me.GetInt("dun10") : 0;
                    Logger.Debug($"[DUNGEON] Player start: ({_playerRow},{_playerCol})");
                }
            }
            catch (Exception ex) { Logger.Debug($"[DUNGEON] dun7 parse error: {ex.Message}"); }
        }

        // Dungeon objects (enemies, treasures, shrines, …)
        if (r.ContainsKey("dun0"))
        {
            try
            {
                var objs = r.GetSFSArray("dun0");
                for (int i = 0; i < objs.Size(); i++)
                {
                    var o = objs.GetSFSObject(i);
                    int oRow   = o.ContainsKey("dun1")  ? o.GetInt("dun1")  : 0;
                    int oCol   = o.ContainsKey("dun2")  ? o.GetInt("dun2")  : 0;
                    int oType  = o.ContainsKey("dun14") ? o.GetInt("dun14") : 0;
                    bool oUsed = o.ContainsKey("dun32") && o.GetBool("dun32");
                    bool oEmp  = o.ContainsKey("dun28") && o.GetBool("dun28");
                    Logger.Debug($"[DUNGEON-OBJ] ({oRow},{oCol}) type={oType} used={oUsed} empty={oEmp}  " +
                                 $"keys=[{string.Join(",", o.GetKeys())}]");
                    _dungeonObjects.Add(new DungeonObjectInfo(oRow, oCol, oType, oUsed, oEmp));
                }
                Logger.Debug($"[DUNGEON] Parsed {_dungeonObjects.Count} dungeon object(s): " +
                             string.Join(", ", _dungeonObjects.Select(o => $"({o.Row},{o.Col}) t={o.Type}")));
            }
            catch (Exception ex) { Logger.Debug($"[DUNGEON] dun0 parse error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Autopilot: directly activate the nearest unused ENEMY (0) or BOSS (2) node.
    /// Dungeon objects can be activated regardless of player proximity — no path-walking needed.
    /// Sends DoObjectActivate with just the target node's coordinates (1-element path).
    /// </summary>
    static void SendDungeonActivate()
    {
        if (_dungeonObjects.Count == 0)
        {
            Logger.Debug("[DUNGEON] No dungeon objects — skipping activation.");
            return;
        }

        // Pick the nearest unused ENEMY (0) or BOSS (2) by Manhattan distance from player.
        // Mirrors CheckAutoPilot()'s shortest-path logic (Manhattan ≈ path length on a grid).
        DungeonObjectInfo? best = null;
        int bestDist = int.MaxValue;
        foreach (var obj in _dungeonObjects)
        {
            if (obj.Used || obj.Empty) continue;
            if (obj.Type != 0 && obj.Type != 2) continue;   // only fight ENEMY / BOSS
            int d = Math.Abs(obj.Row - _playerRow) + Math.Abs(obj.Col - _playerCol);
            Logger.Debug($"[DUNGEON-NAV] candidate {obj.TypeName} ({obj.Row},{obj.Col}) dist={d}");
            if (d < bestDist) { bestDist = d; best = obj; }
        }

        if (best == null)
        {
            // No more combat targets — all enemies defeated. DUNGEON_COMPLETE will arrive shortly.
            Logger.Info("[DUNGEON] No more enemies — awaiting DUNGEON_COMPLETE.");
            _awaitingDungeonComplete = true;
            // Reset the stall timer so the WaitingForBattle timeout does not fire while we wait.
            _lastBattleEvent = DateTime.UtcNow;
            return;
        }

        _navTargetRow      = best.Row;
        _navTargetCol      = best.Col;
        _pendingActivation = true;   // guard against double-activation from OBJECT_REMOVE + ITEMS_ADDED

        Logger.Info($"[DUNGEON] DoObjectActivate → {best.TypeName} ({best.Row},{best.Col}) dist={bestDist}  " +
                    $"room={((_dungeonRoom != null) ? _dungeonRoom.Name : "null→ServerExtension")}");

        // DoObjectActivate (DungeonExtension act0=5):
        //   dun11 = int[] of row coords along path from player to target
        //   dun12 = int[] of col coords along path
        // We send a 1-element path (just the target) — the server accepts this for autopilot.
        var pkt = DungeonPacket(5);
        pkt.PutIntArray("dun11", new[] { best.Row });
        pkt.PutIntArray("dun12", new[] { best.Col });
        SendDungeon(pkt);
    }

    /// <summary>Mark the dungeon object at (row, col) as used/defeated.</summary>
    static void MarkDungeonObjectUsed(int row, int col)
    {
        for (int i = 0; i < _dungeonObjects.Count; i++)
        {
            if (_dungeonObjects[i].Row == row && _dungeonObjects[i].Col == col)
            {
                var o = _dungeonObjects[i];
                _dungeonObjects[i] = o with { Used = true };
                return;
            }
        }
    }

    /// <summary>
    /// Returns the teammate list to use for a dungeon run.
    /// Priority: explicit config list → auto-detected PVE team from server → empty (server uses saved team).
    /// </summary>
    static List<TeammateConfig> GetEffectiveTeammates(DungeonConfig cfg)
    {
        if (cfg.Teammates.Count > 0) return cfg.Teammates;
        if (_savedTeammates.Count > 0) return _savedTeammates;
        return cfg.Teammates; // empty → server falls back to its own saved team
    }

    /// <summary>
    /// Parse the character's PVE dungeon team from a server response containing <c>cha62</c>.
    /// The <c>cha62</c> SFSArray holds all team types; we extract <c>team0=1</c> (TYPE_PVE).
    /// Teammate armoryId is always -1 — the server does not echo <c>tmts3</c> back.
    /// </summary>
    static void ParseAndStorePveTeam(ISFSObject r)
    {
        if (!r.ContainsKey("cha62")) return;
        try
        {
            var teams = r.GetSFSArray("cha62");
            for (int i = 0; i < teams.Size(); i++)
            {
                var team = teams.GetSFSObject(i);
                if (!team.ContainsKey("team0") || team.GetInt("team0") != 1) continue; // TYPE_PVE = 1
                if (!team.ContainsKey("tmts0")) break;

                var arr  = team.GetSFSArray("tmts0");
                var list = new List<TeammateConfig>();
                for (int j = 0; j < arr.Size(); j++)
                {
                    var tm = arr.GetSFSObject(j);
                    list.Add(new TeammateConfig
                    {
                        Id       = tm.ContainsKey("tmts1") ? tm.GetInt("tmts1") : 0,
                        Type     = tm.ContainsKey("tmts2") ? tm.GetInt("tmts2") : 1,
                        ArmoryId = -1   // server never sends tmts3 back
                    });
                }
                _savedTeammates = list;
                Logger.Info($"[TEAM] PVE team loaded: {list.Count} teammate(s) — " +
                            $"[{string.Join(", ", list.Select(t => $"id={t.Id} type={t.Type}"))}]");
                return;
            }
            Logger.Debug("[TEAM] cha62 present but no PVE team (team0=1) found.");
        }
        catch (Exception ex)
        {
            Logger.Debug($"[TEAM] Failed to parse cha62: {ex.Message}");
        }
    }

    /// <summary>
    /// Serialise a <see cref="TeammateConfig"/> list into the tmts0 SFSArray field.
    /// tmts1=Id, tmts2=Type (1=familiar/player, 2=guild), tmts3=(float)armoryId.
    /// Empty list = solo run; server uses the character's saved team.
    /// </summary>
    static void SerialiseTeammates(SFSObject p, List<TeammateConfig> teammates)
    {
        var arr = new SFSArray();
        foreach (var t in teammates)
        {
            var obj = new SFSObject();
            obj.PutInt("tmts1", t.Id);
            obj.PutInt("tmts2", t.Type);
            obj.PutFloat("tmts3", (float)t.ArmoryId);
            arr.AddSFSObject(obj);
        }
        p.PutSFSArray("tmts0", arr);
    }

    #endregion

    #region Response parsers

    static void ParseAndQueueDailyQuests(SFSObject r)
    {
        _questsToLoot.Clear();

        // Log all top-level keys so we can discover the real field names
        Logger.Debug($"[DAILY_QUESTS] keys: {string.Join(", ", r.GetKeys())}");

        if (!r.ContainsKey("dail0"))
        {
            Logger.Info("No dail0 in DAILY_QUEST_CHECK response – no quests or different field name.");
            Logger.Debug($"Full dump:\n{r.GetDump()}");
            return;
        }

        var arr     = r.GetSFSArray("dail0");
        int pending = 0;

        for (int i = 0; i < arr.Size(); i++)
        {
            ISFSObject? q = null;
            try { q = arr.GetSFSObject(i); } catch { Logger.Debug($"  dail0[{i}] is not SFSObject — skipping"); continue; }

            int  qid = q.ContainsKey("dail1") ? q.GetInt("dail1")  : -1;
            int  prg = q.ContainsKey("dail2") ? q.GetInt("dail2")  : 0;
            bool cmp = q.ContainsKey("dail3") && q.GetBool("dail3");
            bool lot = q.ContainsKey("dail4") && q.GetBool("dail4");

            Logger.Debug($"  Quest id={qid} progress={prg} completed={cmp} looted={lot}");

            if (cmp && !lot && qid != -1)
            {
                _questsToLoot.Enqueue(new DailyQuestEntry
                    { QuestId = qid, Progress = prg, Completed = cmp, Looted = lot });
                pending++;
            }
        }

        Logger.Info($"{pending} quest(s) ready to loot.");
    }

    static DungeonResult ParseDungeonResult(SFSObject r)
    {
        bool victory = r.ContainsKey("bat47") && r.GetBool("bat47");
        var  loot    = new List<LootItem>();

        if (r.ContainsKey("act2"))
        {
            var arr = r.GetSFSArray("act2");
            for (int i = 0; i < arr.Size(); i++)
                loot.Add(ParseLootItem(arr.GetSFSObject(i)));
        }
        else if (r.ContainsKey("act1"))
        {
            var obj = r.GetSFSObject("act1");
            if (obj != null) loot.Add(ParseLootItem(obj));
        }

        var cfg = GetCurrentDungeon();
        return new DungeonResult
        {
            Victory      = victory,
            Loot         = loot,
            RunNumber    = _runCount + 1,
            ZoneId       = cfg.ZoneId,
            NodeId       = cfg.NodeId,
            DifficultyId = cfg.DifficultyId,
            Waves        = _waveCount
        };
    }

    static LootItem ParseLootItem(ISFSObject item) => new()
    {
        ItemId   = item.ContainsKey("ite0") ? item.GetInt("ite0") : 0,
        ItemType = item.ContainsKey("ite1") ? item.GetInt("ite1") : 0,
        Qty      = item.ContainsKey("ite2") ? item.GetInt("ite2") : 1
    };

    /// <summary>
    /// Opportunistically parse currency fields from any server response.
    /// Also checks nested <c>act1</c> / <c>act3</c> sub-objects where character data
    /// is commonly wrapped in PlayerDALC / GameDALC responses.
    /// </summary>
    static void TryParseCurrency(ISFSObject r)
    {
        // Field names verified from Character.cs decompile.
        // chal9=gold(long), chal10=credits(long), cha27=energy(int), cha29=tickets(int),
        // cha67=shards(int), cha28=energyUpdatedAt(ms), cha97=energyCooldownMs,
        // cha30=ticketsUpdatedAt(ms), cha98=ticketsCooldownMs, cha94=highestZone(int)
        try
        {
            ExtractCurrencyFields(r);

            // Character data is often nested inside act1 or act3 sub-objects
            if (r.ContainsKey("act1")) try { ExtractCurrencyFields(r.GetSFSObject("act1")); } catch { }
            if (r.ContainsKey("act3")) try { ExtractCurrencyFields(r.GetSFSObject("act3")); } catch { }
        }
        catch { /* best-effort */ }
    }

    static void ExtractCurrencyFields(ISFSObject r)
    {
        if (r.ContainsKey("chal9"))  SessionStats.UpdateCurrency(gold:    r.GetLong("chal9"));
        if (r.ContainsKey("chal10")) SessionStats.UpdateCurrency(credits: r.GetLong("chal10"));
        if (r.ContainsKey("cha27"))  SessionStats.UpdateCurrency(energy:  r.GetInt("cha27"));
        if (r.ContainsKey("cha29"))  SessionStats.UpdateCurrency(tickets: r.GetInt("cha29"));
        if (r.ContainsKey("cha67"))  SessionStats.UpdateCurrency(shards:  r.GetInt("cha67"));
        if (r.ContainsKey("cha28") && r.ContainsKey("cha97"))
            SessionStats.UpdateEnergyRegen(r.GetLong("cha28"), r.GetLong("cha97"));
        if (r.ContainsKey("cha30") && r.ContainsKey("cha98"))
            SessionStats.UpdateTicketRegen(r.GetLong("cha30"), r.GetLong("cha98"));
        if (r.ContainsKey("cha94"))
            SessionStats.UpdateHighestZone(r.GetInt("cha94"));
    }

    static void LogDungeonResult(DungeonResult r)
    {
        Logger.Info($"[RESULT] Run #{r.RunNumber} – {(r.Victory ? "VICTORY" : "DEFEAT")} " +
                    $"zone={r.ZoneId} node={r.NodeId} waves={r.Waves} items={r.Loot.Count}");
        foreach (var item in r.Loot)
            Logger.Debug($"  {item}");
    }

    #endregion

    #region Steam auth ticket

    static string GetSteamAuthTicket()
    {
        byte[] buf = new byte[1024];
        SteamUser.GetAuthSessionTicket(buf, 1024, out uint size);
        return BitConverter.ToString(buf, 0, (int)size).Replace("-", "");
    }

    #endregion

    #region Persistence (playerid.txt / anonid.txt)

    static string LoadOrCreateAnonymousId()
    {
        const string path = "anonid.txt";
        if (File.Exists(path))
        {
            string v = File.ReadAllText(path).Trim();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        string newId = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-" +
                       $"{Path.GetRandomFileName().Replace(".", "")[..8]}";
        File.WriteAllText(path, newId);
        Logger.Info($"Generated anonymousId={newId}");
        return newId;
    }

    static int LoadPlayerId()
    {
        if (File.Exists("playerid.txt") &&
            int.TryParse(File.ReadAllText("playerid.txt").Trim(), out int id))
            return id;
        return -1;
    }

    static void SavePlayerId(int id) =>
        File.WriteAllText("playerid.txt", id.ToString());

    #endregion

    #region Misc

    static void AssertInGame()
    {
        if (_login != LoginState.InGame)
            throw new InvalidOperationException(
                "Not in-game yet. Wait for the OnInGame event before calling this method.");
    }

    #endregion
}
