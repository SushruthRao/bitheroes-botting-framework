using Sfs2X;
using Sfs2X.Core;
using Sfs2X.Entities.Data;
using Sfs2X.Requests;
using Steamworks;
using System.Security.Cryptography;
using System.Text;

// ============================================================
//  Bit Heroes – Standalone CLI Client
//  Reversed from Assembly-CSharp.dll via dnSpy
// ============================================================
//
//  Platform codes (AppInfo.getCurrentPlatform / pla2):
//    1 = Android   2 = iOS   4 = WebGL   7 = Windows/Mac (Steam)
//
//  Client platform codes (AppInfo.GetClientPlatform / cli0):
//    1 = Android   2 = iOS   3 = WebGL   4 = Desktop/Steam   5 = ?
//
//  DALC IDs (dal0):
//    0=Game  1=Character  2=Admin  3=Friend  4=User  5=PvP
//    6=Leaderboard  7=Merchant  8=Chat  9=Guild  10=Battle
//    11=Rift  12=Gauntlet  13=GvG  14=Invasion  15=Brawl
//    16=Fishing  17=GvE  18=PlayerVoting  19=EventSales  21=Player
//
//  Login flow:
//    1. sfs.Send(LoginRequest("","","Server")) -> OnLogin fires
//    2. PlayerDALC LOGIN_PLATFORM  (dal0=21, act0=1)
//    3. Server responds -> parse character list from response
//    4. PlayerDALC GET_CHARACTER_LIST (dal0=21, act0=4)  [if needed]
//    5. User selects character
//    6. PlayerDALC SELECT_CHARACTER (dal0=21, act0=6)
//    7. UserDALC LOAD_XMLS (dal0=4, act0=5)
//    8. Fully in-game
// ============================================================

class Program
{
    // ── Constants matching the reversed game ──────────────────
    const string APP_VERSION = "2.5.6";    // cli1
    const string SFS_VERSION = "1.7.5";    // cli2  (SmartFox lib version)
    const string DLC_VERSION = "StandaloneWindows64_20260316T191028Z"; // cli3
    const string SFS_ZONE = "Server";
    const string SFS_EXTENSION = "ServerExtension";
    const string SERVER_HOST = "f123.bitheroesgame.com";
    const int SERVER_PORT = 9933;

    // Platform = 7 → Windows/Mac desktop (Steam)
    // GetClientPlatform(7) → 4
    const int PLATFORM = 7;
    const int CLI_PLATFORM = 4;

    // Password hash salt (reversed from ServerExtension.GenerateHash)
    const string HASH_SALT = "k5iw3la0";

    // ── State machine ─────────────────────────────────────────
    enum LoginState
    {
        Disconnected,
        SfsLoggedIn,          // guest LoginRequest accepted; sent platform login
        AwaitingPlatformResp, // waiting for login response
        AwaitingCharList,     // waiting for character list
        SelectingCharacter,   // user choosing which char to play
        AwaitingCharConfirm,  // waiting for confirm character response
        LoadingXmls,          // waiting for dal0=4 act0=5 response
        InGame                // fully authenticated, interactive mode
    }

    static SmartFox sfs;
    static LoginState state = LoginState.Disconnected;

    // Steam credentials – filled during Init()
    static string steamID64 = "";   // use3: Steam 64-bit ID as string
    static string steamTicket = "";   // use4: auth session ticket as hex
    static string steamName = "";   // use0: Steam persona name

    // Filled in from server response / persisted locally
    static int playerID = -1;   // pla3
    static int selectedCharID = -1;
    static string anonymousID = "";   // pla12 – persisted in anonid.txt

    // Characters returned by the server: charID -> name
    static Dictionary<int, string> characters = new();

    // ── Entry point ───────────────────────────────────────────
    static void Main(string[] args)
    {
        Console.WriteLine("=== Bit Heroes CLI Client ===");

        // ── Steam initialisation ──────────────────────────────
        // Requires:
        //   1. Steam is running and you own Bit Heroes (AppID 666860)
        //   2. A file named  steam_appid.txt  next to this .exe containing:  666860
        //   3. com.rlabrecque.steamworks.net.dll referenced from the game's Managed folder
        if (!SteamAPI.Init())
        {
            Console.WriteLine("[-] SteamAPI.Init() failed.");
            Console.WriteLine("    Make sure Steam is running, you own Bit Heroes,");
            Console.WriteLine("    and steam_appid.txt contains: 666860");
            Environment.Exit(1);
        }

        // Get credentials exactly as SteamLogin.cs does
        steamID64 = ((long)SteamUser.GetSteamID().m_SteamID).ToString();
        steamName = SteamFriends.GetPersonaName();
        steamTicket = GetSteamAuthTicket();

        Console.WriteLine($"[+] Steam OK – ID={steamID64}  Name={steamName}");
        Console.WriteLine($"    Ticket: {steamTicket.Substring(0, Math.Min(16, steamTicket.Length))}...");

        // Load persisted state from previous sessions
        anonymousID = LoadOrCreateAnonymousID();
        playerID = LoadPlayerID();
        if (playerID != -1)
            Console.WriteLine($"[+] Loaded cached playerID={playerID}");

        sfs = new SmartFox();
        sfs.ThreadSafeMode = true;

        sfs.AddEventListener(SFSEvent.CONNECTION, OnConnection);
        sfs.AddEventListener(SFSEvent.CONNECTION_LOST, OnConnectionLost);
        sfs.AddEventListener(SFSEvent.LOGIN, OnLogin);
        sfs.AddEventListener(SFSEvent.LOGIN_ERROR, OnLoginError);
        sfs.AddEventListener(SFSEvent.EXTENSION_RESPONSE, OnExtensionResponse);

        // Clean up Steam on exit
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SteamAPI.Shutdown();

        Console.WriteLine($"Connecting to {SERVER_HOST}:{SERVER_PORT}...");
        sfs.Connect(SERVER_HOST, SERVER_PORT);

        // Main loop – sfs.ProcessEvents() pumps async callbacks
        while (true)
        {
            SteamAPI.RunCallbacks(); // keep Steam alive
            sfs.ProcessEvents();

            // Interactive mode: accept console commands once fully in-game
            if (state == LoginState.InGame && Console.KeyAvailable)
            {
                string line = Console.ReadLine();
                if (line != null)
                    HandleCommand(line.Trim());
            }
        }
    }

    // ── SFS2X event handlers ──────────────────────────────────

    static void OnConnection(BaseEvent evt)
    {
        if ((bool)evt.Params["success"])
        {
            Console.WriteLine("[+] TCP connected. Sending guest LoginRequest...");
            // GuestLogin: empty username/password, zone = "Server"
            // use8 = language code (reversed from ServerExtension.GuestLogin)
            SFSObject loginParams = new SFSObject();
            loginParams.PutUtfString("use8", "en");
            sfs.Send(new LoginRequest("", "", SFS_ZONE, loginParams));
        }
        else
        {
            Console.WriteLine("[-] Connection failed.");
        }
    }

    static void OnConnectionLost(BaseEvent evt)
    {
        state = LoginState.Disconnected;
        Console.WriteLine("[-] Connection lost.");
    }

    static void OnLoginError(BaseEvent evt)
    {
        Console.WriteLine("[-] SFS2X login error: " + evt.Params["errorMessage"]);
    }

    // Called when the SFS2X guest login (LoginRequest) succeeds.
    // Now we send the game-level Steam authentication via UserDALC.
    static void OnLogin(BaseEvent evt)
    {
        Console.WriteLine("[+] SFS2X guest login OK. Sending PlayerDALC LOGIN_PLATFORM...");
        state = LoginState.SfsLoggedIn;
        SendPlayerLoginPlatform();
    }

    // Central router for all extension responses
    static void OnExtensionResponse(BaseEvent evt)
    {
        string cmd = evt.Params["cmd"] as string ?? "";
        SFSObject resp = evt.Params["params"] as SFSObject;

        // Check for server-side error first
        if (resp.ContainsKey("err0"))
        {
            int errCode = resp.GetInt("err0");
            Console.WriteLine($"[!] Server error code: {errCode}");
            // Non-fatal for many actions; still continue parsing
        }

        if (!resp.ContainsKey("dal0"))
        {
            Console.WriteLine("[?] Response has no dal0 – raw dump:");
            Console.WriteLine(resp.GetDump());
            return;
        }

        int dalcID = resp.GetInt("dal0");
        int actionID = resp.ContainsKey("act0") ? resp.GetInt("act0") : -1;

        Console.WriteLine($"[<] cmd={cmd} dal0={dalcID} act0={actionID}");

        switch (dalcID)
        {
            case 21: HandlePlayerDALC(resp, actionID); break;
            case 4: HandleUserDALC(resp, actionID); break;
            case 0: HandleGameDALC(resp, actionID); break;
            case 1: HandleCharacterDALC(resp, actionID); break;
            case 8: HandleChatDALC(resp, actionID); break;
            case 9: HandleGuildDALC(resp, actionID); break;
            case 10: HandleBattleDALC(resp, actionID); break;
            default:
                Console.WriteLine($"[?] Unhandled dal0={dalcID} act0={actionID}");
                Console.WriteLine(resp.GetDump());
                break;
        }
    }

    // ── DALC response handlers ────────────────────────────────

    // PlayerDALC (dal0=21)
    static void HandlePlayerDALC(SFSObject resp, int act)
    {
        switch (act)
        {
            // LOGIN_PLATFORM response (act0=1)
            // For a returning player the server auto-selects the last-used character;
            // the client must send LOAD_XMLS next (no GET_CHARACTER_LIST / SELECT_CHARACTER).
            // GET_CHARACTER_LIST is only needed for brand-new accounts with no characters.
            case 1:
                {
                    Console.WriteLine("[+] Platform login response received.");

                    if (resp.ContainsKey("pla3"))
                    {
                        playerID = resp.GetInt("pla3");
                        Console.WriteLine($"    playerID = {playerID}");
                        SavePlayerID(playerID);
                    }

                    if (playerID != -1)
                    {
                        // Returning player – server has already selected the character.
                        // Match the observed traffic: go straight to LOAD_XMLS.
                        state = LoginState.LoadingXmls;
                        SendLoadXMLs();
                    }
                    else
                    {
                        // New account with no characters yet.
                        Console.WriteLine("[*] No playerID in response – requesting character list...");
                        state = LoginState.AwaitingCharList;
                        SendGetCharacterList();
                    }
                    break;
                }

            // GET_CHARACTER_LIST response (act0=4)
            case 4:
                {
                    Console.WriteLine("[+] Character list response received.");
                    ParseCharacterList(resp);
                    PromptCharacterSelect();
                    break;
                }

            // SELECT_CHARACTER response (act0=6)
            case 6:
                {
                    Console.WriteLine("[+] Character selected.");
                    state = LoginState.LoadingXmls;
                    SendLoadXMLs();
                    break;
                }

            // CREATE_CHARACTER response (act0=5)
            case 5:
                {
                    Console.WriteLine("[+] Character created.");
                    if (resp.ContainsKey("cha1"))
                    {
                        selectedCharID = resp.GetInt("cha1");
                        state = LoginState.AwaitingCharConfirm;
                        SendConfirmCharacter(selectedCharID, playerID);
                    }
                    break;
                }

            default:
                Console.WriteLine($"[?] PlayerDALC unknown act0={act}");
                Console.WriteLine(resp.GetDump());
                break;
        }
    }

    // UserDALC (dal0=4)
    static void HandleUserDALC(SFSObject resp, int act)
    {
        switch (act)
        {
            // LOAD_XMLS response (act0=5) – game config XML data
            case 5:
                Console.WriteLine("[+] XML data loaded. Fully in-game!");
                state = LoginState.InGame;
                PrintHelp();
                break;

            // LOGOUT response (act0=4)
            case 4:
                Console.WriteLine("[+] Logout acknowledged.");
                break;

            default:
                Console.WriteLine($"[?] UserDALC act0={act}");
                Console.WriteLine(resp.GetDump());
                break;
        }
    }

    // GameDALC (dal0=0)
    static void HandleGameDALC(SFSObject resp, int act)
    {
        switch (act)
        {
            case 3:  // NOTIFICATION
                {
                    string msg = resp.ContainsKey("not1") ? resp.GetUtfString("not1") : "(no message)";
                    Console.WriteLine($"[NOTIFY] {msg}");
                    break;
                }
            case 4:  // GAME_UPDATE
                Console.WriteLine("[GAME_UPDATE] Received game update.");
                break;
            case 7:  // PLAYERS_ONLINE
                if (resp.ContainsKey("serv5"))
                    Console.WriteLine($"[ONLINE] Players online: {resp.GetInt("serv5")}");
                break;
            case 8:  // PLAYER_LOGIN (another player logged in nearby)
                Console.WriteLine("[EVENT] A player logged in.");
                break;
            case 9:  // PLAYER_LOGOUT
                Console.WriteLine("[EVENT] A player logged out.");
                break;
            case 12: // IDLE_RESPONSE
                Console.WriteLine("[IDLE] Idle response acknowledged.");
                break;
            case 17: // HERO_FROZEN
                Console.WriteLine("[!] Hero frozen by server.");
                break;
            default:
                Console.WriteLine($"[GameDALC] act0={act}");
                break;
        }
    }

    // CharacterDALC (dal0=1)
    static void HandleCharacterDALC(SFSObject resp, int act)
    {
        Console.WriteLine($"[CharacterDALC] act0={act}");
        // Dump full response for now – extensive, add cases as needed
        Console.WriteLine(resp.GetDump());
    }

    // ChatDALC (dal0=8)
    static void HandleChatDALC(SFSObject resp, int act)
    {
        switch (act)
        {
            case 1: // CHAT_MESSAGE
                {
                    string sender = resp.ContainsKey("cha2") ? resp.GetUtfString("cha2") : "?";
                    string message = resp.ContainsKey("chat0") ? resp.GetUtfString("chat0") : "";
                    Console.WriteLine($"[CHAT] {sender}: {message}");
                    break;
                }
            case 2: // PRIVATE_MESSAGE
                {
                    string sender = resp.ContainsKey("cha2") ? resp.GetUtfString("cha2") : "?";
                    string message = resp.ContainsKey("chat0") ? resp.GetUtfString("chat0") : "";
                    Console.WriteLine($"[PM] {sender}: {message}");
                    break;
                }
            default:
                Console.WriteLine($"[ChatDALC] act0={act}");
                break;
        }
    }

    // GuildDALC (dal0=9)
    static void HandleGuildDALC(SFSObject resp, int act)
    {
        Console.WriteLine($"[GuildDALC] act0={act}");
    }

    // BattleDALC (dal0=10)
    static void HandleBattleDALC(SFSObject resp, int act)
    {
        Console.WriteLine($"[BattleDALC] act0={act}");
        Console.WriteLine(resp.GetDump());
    }

    // ── Login sequence helpers ────────────────────────────────

    // Steam auth session ticket – mirrors SteamLogin.GetPlatformSpecificUserToken()
    // SteamUser.GetAuthSessionTicket() returns raw bytes; game converts to uppercase hex.
    static string GetSteamAuthTicket()
    {
        byte[] buffer = new byte[1024];
        uint ticketSize;
        SteamUser.GetAuthSessionTicket(buffer, 1024, out ticketSize);
        byte[] ticket = new byte[ticketSize];
        Array.Copy(buffer, ticket, ticketSize);
        // BitConverter.ToString gives "AB-CD-EF", replace dashes to get "ABCDEF"
        return BitConverter.ToString(ticket, 0, ticket.Length).Replace("-", "");
    }

    // Step 2 – PlayerDALC LOGIN_PLATFORM (dal0=21, act0=1)
    // Mirrors PlayerDALC.doLoginPlatform() as observed in traffic dump.
    //
    // Fields observed in dump:
    //   pla3  = internal playerID (if known from prior session)
    //   pla9  = Kongregate token placeholder for non-Kongregate logins
    //   pla10 = Steam 64-bit ID (repurposed device-ID field)
    //   pla11 = Steam auth session ticket hex (repurposed push-token field)
    //   pla12 = anonymous / device ID (persisted locally)
    //   pla2  = platform = 7 (Windows/Steam)
    //   pla4  = OS string
    //   pla5  = language
    //   pla6  = "" (push notification token – empty on desktop)
    //   cli0  = 4 (Desktop client platform)
    static void SendPlayerLoginPlatform()
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 1);                                        // LOGIN_PLATFORM
        if (playerID != -1)
            sfsObj.PutInt("pla3", playerID);                             // internal player ID (if cached)
        sfsObj.PutUtfString("pla9", "Guest_Game_Auth_Token");            // Kongregate token placeholder
        sfsObj.PutUtfString("pla10", steamID64);                         // Steam 64-bit ID
        sfsObj.PutUtfString("pla11", steamTicket);                       // Steam auth session ticket
        sfsObj.PutUtfString("pla12", anonymousID);                       // anonymous/device ID
        sfsObj.PutInt("pla2", PLATFORM);                                 // platform = 7
        sfsObj.PutInt("cli0", CLI_PLATFORM);                             // cli0 = 4
        sfsObj.PutUtfString("pla4", "Windows 10  (10.0.19045) 64bit");  // OS string
        sfsObj.PutUtfString("pla5", "en");                               // language
        sfsObj.PutUtfString("pla6", "");                                 // push notification token (empty)
        AddEnvelope(sfsObj, 21);                                         // dal0=21 + cli1/cli2/cli3
        Send(sfsObj);
        state = LoginState.AwaitingPlatformResp;
        Console.WriteLine("[>] Sent PlayerDALC LOGIN_PLATFORM (Steam)");
    }

    // Step 4 (optional) – PlayerDALC GET_CHARACTER_LIST (dal0=21, act0=4)
    static void SendGetCharacterList()
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 4);              // GET_CHARACTER_LIST
        if (playerID != -1)
            sfsObj.PutInt("pla3", playerID);
        AddEnvelope(sfsObj, 21);
        Send(sfsObj);
        Console.WriteLine("[>] Sent PlayerDALC GET_CHARACTER_LIST");
    }

    // Step 6 – PlayerDALC SELECT_CHARACTER (dal0=21, act0=6)
    // Mirrors PlayerDALC.doConfirmCharacter()
    static void SendConfirmCharacter(int charID, int pid)
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 6);              // SELECT_CHARACTER
        sfsObj.PutInt("cha1", charID);
        if (pid != -1)
        {
            playerID = pid;
            sfsObj.PutInt("pla3", pid);
        }
        sfsObj.PutUtfString("pla10", "");
        sfsObj.PutUtfString("pla11", "");
        sfsObj.PutUtfString("pla12", "");
        AddEnvelope(sfsObj, 21);
        Send(sfsObj);
        state = LoginState.AwaitingCharConfirm;
        Console.WriteLine($"[>] Sent PlayerDALC SELECT_CHARACTER charID={charID}");
    }

    // Step 7 – UserDALC LOAD_XMLS (dal0=4, act0=5)
    // Mirrors UserDALC.doLoadXMLs() – field order matches observed dump exactly.
    static void SendLoadXMLs()
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 5);              // LOAD_XMLS
        sfsObj.PutUtfString("cli2", SFS_VERSION);
        sfsObj.PutUtfString("use8", "en");     // language
        sfsObj.PutInt("dal0001", 1);           // typo/leftover from original – game sends it
        sfsObj.PutUtfString("cli1", APP_VERSION);
        sfsObj.PutInt("cli0", CLI_PLATFORM);
        sfsObj.PutInt("dal0", 4);              // UserDALC
        sfsObj.PutUtfString("cli3", DLC_VERSION);
        Send(sfsObj);
        Console.WriteLine("[>] Sent UserDALC LOAD_XMLS");
    }

    // ── Character list helpers ────────────────────────────────

    // The game packs the character list into the SFSObject.
    // Common keys: cha1=charID (int), cha2=name (string).
    // They may come as a nested array of SFSObjects.
    static void ParseCharacterList(SFSObject resp)
    {
        characters.Clear();

        // Try array-of-objects format (SFSArray of SFSObject)
        if (resp.ContainsKey("act2"))
        {
            ISFSArray arr = resp.GetSFSArray("act2");
            for (int i = 0; i < arr.Size(); i++)
            {
                ISFSObject charObj = arr.GetSFSObject(i);
                if (charObj.ContainsKey("cha1") && charObj.ContainsKey("cha2"))
                {
                    int id = charObj.GetInt("cha1");
                    string name = charObj.GetUtfString("cha2");
                    characters[id] = name;
                }
            }
        }

        // Also try flat single-character (act1 = single SFSObject)
        if (characters.Count == 0 && resp.ContainsKey("act1"))
        {
            ISFSObject charObj = resp.GetSFSObject("act1");
            if (charObj != null && charObj.ContainsKey("cha1") && charObj.ContainsKey("cha2"))
            {
                int id = charObj.GetInt("cha1");
                string name = charObj.GetUtfString("cha2");
                characters[id] = name;
            }
        }

        if (characters.Count > 0)
        {
            Console.WriteLine($"    Found {characters.Count} character(s):");
            foreach (var kv in characters)
                Console.WriteLine($"      [{kv.Key}] {kv.Value}");
        }
    }

    static void PromptCharacterSelect()
    {
        state = LoginState.SelectingCharacter;

        if (characters.Count == 0)
        {
            Console.WriteLine("[?] No characters found. Type 'create <name>' to make one.");
            return;
        }

        Console.WriteLine("[?] Enter character ID to play (or 'create <name>' for a new one):");
        string input = Console.ReadLine()?.Trim();
        if (input == null) return;

        if (input.StartsWith("create "))
        {
            string charName = input.Substring(7).Trim();
            SendCreateCharacter(charName);
            return;
        }

        if (int.TryParse(input, out int cid) && characters.ContainsKey(cid))
        {
            selectedCharID = cid;
            SendConfirmCharacter(selectedCharID, playerID);
        }
        else
        {
            Console.WriteLine("[-] Invalid character ID.");
            PromptCharacterSelect();
        }
    }

    // PlayerDALC CREATE_CHARACTER (dal0=21, act0=5)
    static void SendCreateCharacter(string name, bool male = true,
        int hairID = 0, int hairColorID = 0, int skinColorID = 0)
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 5);              // CREATE_CHARACTER
        sfsObj.PutUtfString("cha2", name);
        sfsObj.PutBool("cha12", male);
        sfsObj.PutInt("cha20", hairID);
        sfsObj.PutInt("cha21", hairColorID);
        sfsObj.PutInt("cha22", skinColorID);
        if (playerID != -1)
            sfsObj.PutInt("pla3", playerID);
        sfsObj.PutUtfString("pla10", "");
        sfsObj.PutUtfString("pla11", "");
        sfsObj.PutUtfString("pla12", "");
        AddEnvelope(sfsObj, 21);
        Send(sfsObj);
        Console.WriteLine($"[>] Sent PlayerDALC CREATE_CHARACTER name={name}");
    }

    // ── In-game interactive commands ──────────────────────────

    static void PrintHelp()
    {
        Console.WriteLine("");
        Console.WriteLine("Commands:");
        Console.WriteLine("  chat <message>        – Send public chat message");
        Console.WriteLine("  pm <charID> <msg>     – Send private message");
        Console.WriteLine("  online                – Request players online count");
        Console.WriteLine("  guild                 – Load guild data");
        Console.WriteLine("  leaderboard           – Get player leaderboard");
        Console.WriteLine("  idle                  – Send idle response (keep-alive)");
        Console.WriteLine("  logout                – Logout and exit");
        Console.WriteLine("  dump                  – Dump raw next response");
        Console.WriteLine("  help                  – Show this list");
        Console.WriteLine("");
    }

    static void HandleCommand(string input)
    {
        if (string.IsNullOrEmpty(input)) return;
        string[] parts = input.Split(' ', 2);
        string cmd = parts[0].ToLower();
        string arg = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "chat":
                SendChatMessage(arg);
                break;

            case "pm":
                {
                    string[] pmParts = arg.Split(' ', 2);
                    if (pmParts.Length == 2 && int.TryParse(pmParts[0], out int charID))
                        SendPrivateMessage(pmParts[1], charID);
                    else
                        Console.WriteLine("Usage: pm <charID> <message>");
                    break;
                }

            case "online":
                SendPlayersOnline();
                break;

            case "guild":
                SendGuildLoadData();
                break;

            case "leaderboard":
                SendLeaderboard(0);
                break;

            case "idle":
                SendIdleResponse();
                break;

            case "logout":
                SendLogout();
                break;

            case "help":
                PrintHelp();
                break;

            default:
                Console.WriteLine($"Unknown command: {cmd}");
                break;
        }
    }

    // ── Game packet senders ───────────────────────────────────

    // ChatDALC CHAT_MESSAGE (dal0=8, act0=1)
    static void SendChatMessage(string message)
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 1);
        sfsObj.PutUtfString("chat0", message);
        AddEnvelope(sfsObj, 8);
        Send(sfsObj);
        Console.WriteLine($"[>] Chat: {message}");
    }

    // ChatDALC PRIVATE_MESSAGE (dal0=8, act0=2)
    static void SendPrivateMessage(string message, int charID)
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 2);
        sfsObj.PutUtfString("chat0", message);
        sfsObj.PutInt("cha1", charID);
        AddEnvelope(sfsObj, 8);
        Send(sfsObj);
    }

    // GameDALC PLAYERS_ONLINE (dal0=0, act0=7)
    static void SendPlayersOnline()
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 7);
        sfsObj.PutBool("act4", false);         // act4 = update flag
        AddEnvelope(sfsObj, 0);
        Send(sfsObj);
    }

    // GuildDALC LOAD_DATA (dal0=9, act0=5)
    static void SendGuildLoadData()
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 5);
        AddEnvelope(sfsObj, 9);
        Send(sfsObj);
    }

    // LeaderboardDALC GET_LIST (dal0=6, act0=1)
    //   type: 0=players, 1=guilds, 2=guild_members
    static void SendLeaderboard(int id)
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 1);
        sfsObj.PutInt("lea0", id);
        AddEnvelope(sfsObj, 6);
        Send(sfsObj);
    }

    // GameDALC IDLE_RESPONSE (dal0=0, act0=12)  – keep-alive heartbeat
    static void SendIdleResponse()
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutInt("act0", 12);             // 0xC
        AddEnvelope(sfsObj, 0);
        // Note: game sends this WITHOUT resetting the idle timer (idleTimer=false)
        Send(sfsObj);
    }

    // UserDALC LOGOUT (dal0=4, act0=4)
    static void SendLogout()
    {
        SFSObject sfsObj = new SFSObject();
        sfsObj.PutUtfString("cli2", SFS_VERSION);
        sfsObj.PutInt("act0", 4);
        sfsObj.PutUtfString("cli1", APP_VERSION);
        sfsObj.PutUtfString("use15", BuildServerReady());
        sfsObj.PutInt("dal0", 4);
        Send(sfsObj);
        Console.WriteLine("[>] Sent logout. Disconnecting...");
        sfs.Disconnect();
    }

    // ── Utility ───────────────────────────────────────────────

    // Adds the standard envelope fields that BaseDALC.send() injects.
    static void AddEnvelope(SFSObject sfsObj, int dalcID)
    {
        sfsObj.PutUtfString("cli2", SFS_VERSION);
        sfsObj.PutInt("dal0", dalcID);
        sfsObj.PutUtfString("cli1", APP_VERSION);
        sfsObj.PutUtfString("cli3", DLC_VERSION);
    }

    // Wraps sfs.Send with the correct extension name.
    static void Send(SFSObject sfsObj)
    {
        sfs.Send(new ExtensionRequest(SFS_EXTENSION, sfsObj, null));
    }

    // Loads anonymousID from anonid.txt, or generates and saves a new one.
    // Format mirrors observed value: "<timestamp>-<random alphanumeric>"
    static string LoadOrCreateAnonymousID()
    {
        const string path = "anonid.txt";
        if (File.Exists(path))
        {
            string id = File.ReadAllText(path).Trim();
            if (!string.IsNullOrEmpty(id))
                return id;
        }
        // Generate a new anonymous ID in the same format as the game
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string rand = Path.GetRandomFileName().Replace(".", "").Substring(0, 8);
        string newID = $"{ts}-{rand}";
        File.WriteAllText(path, newID);
        Console.WriteLine($"[+] Generated anonymousID={newID}");
        return newID;
    }

    // Loads playerID from playerid.txt (-1 if not found).
    static int LoadPlayerID()
    {
        const string path = "playerid.txt";
        if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int id))
            return id;
        return -1;
    }

    // Persists playerID to playerid.txt for future sessions.
    static void SavePlayerID(int id)
    {
        File.WriteAllText("playerid.txt", id.ToString());
    }

    // Builds the _serverReady string that UserDALC embeds in login packets.
    // Format: "<RuntimePlatform>:v#<appVersion>:l#<fileCount>"
    // The file count is the number of files enumerated in Application.dataPath.
    // We use a plausible approximation for a standalone client.
    static string BuildServerReady()
    {
        return $"WindowsPlayer:v#{APP_VERSION}:l#2048";
    }

    // MD5 password hash matching ServerExtension.GenerateHash()
    // Hash = MD5(plaintext + "k5iw3la0")
    static string HashPassword(string plaintext)
    {
        string salted = plaintext + HASH_SALT;
        using MD5 md5 = MD5.Create();
        byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(salted));
        StringBuilder sb = new StringBuilder();
        foreach (byte b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
