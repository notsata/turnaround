using Turnaround.Aircraft;
using Turnaround.Baggage;
using Turnaround.Company;
using Turnaround.Events;
using Turnaround.Multiplayer;
using Turnaround.Player;
using Turnaround.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Turnaround.UI
{
    // Main menu, pause menu and HUD in one IMGUI pass, drawn at native resolution over the low-res world.
    // shortcut: IMGUI is fine for the prototype; move to UI Toolkit when the HUD gets real art.
    public class GameUI : MonoBehaviour
    {
        public GameObject menuCamera;

        public static bool MenuOpen { get; private set; }

        string address = "127.0.0.1";
        string status;
        bool showTasks;
        bool reportShown;
        bool joinScreen;
        AircraftController aircraft;
        GUIStyle title, label, big, button, field, center, mono, radioStyle, alert, smallButton;
        static bool companyOpen;
        int companyTab;
        Vector2 companyScroll;
        float styleScale;

        static NetworkManager Net => NetworkManager.Singleton;
        static bool InSession => Net != null && (Net.IsClient || Net.IsServer);

        void Awake()
        {
            MenuOpen = false;
            Cursor.lockState = CursorLockMode.None; // fresh menu (also after a disconnect reload): mouse is free
            Cursor.visible = true;
            status = ConnectionManager.LastDisconnectReason;
            ConnectionManager.LastDisconnectReason = null;
        }

        void Start()
        {
            Net.OnClientDisconnectCallback += OnClientDisconnect;
            gameObject.AddComponent<Turnaround.Core.Updater>();

            // "-host" or "-join <ip>" for quickly launching several local instances.
            var args = System.Environment.GetCommandLineArgs();
            int join = System.Array.IndexOf(args, "-join");
            if (System.Array.IndexOf(args, "-host") >= 0 && ConnectionManager.StartHost() && System.Array.IndexOf(args, "-autoturn") >= 0)
                gameObject.AddComponent<Turnaround.Core.DevAutoTurn>();
            else if (join >= 0 && join + 1 < args.Length) ConnectionManager.StartClient(address = args[join + 1]);
        }

        void OnDestroy()
        {
            if (Net != null) Net.OnClientDisconnectCallback -= OnClientDisconnect;
        }

        void OnClientDisconnect(ulong clientId)
        {
            // Host sees every client leave; only react when *we* got dropped.
            if (Net.IsServer || (clientId != Net.LocalClientId && clientId != NetworkManager.ServerClientId)) return;
            string reason = string.IsNullOrEmpty(Net.DisconnectReason) ? "Lost connection to the host." : Net.DisconnectReason;
            ConnectionManager.Leave(reason);
        }

        void Update()
        {
            menuCamera.SetActive(PlayerController.Local == null);
            var kb = Keyboard.current;
            if (kb == null || !InSession) return;
            if (kb.escapeKey.wasPressedThisFrame)
            {
                MenuOpen = !MenuOpen;
                companyOpen = false;
            }
            if (reportShown && Turn != null && Turn.Report.Length == 0) // host started the next flight
            {
                reportShown = false;
                if (!companyOpen) MenuOpen = false;
            }
            // Score screen pops up once when the aircraft leaves; Esc dismisses it like the pause menu.
            if (!reportShown && Turn != null && Turn.Report.Length > 0)
            {
                reportShown = true;
                MenuOpen = true;
            }
            showTasks = kb.tabKey.isPressed;
        }

        void BuildStyles()
        {
            float scale = Mathf.Max(0.6f, Screen.height / 1080f);
            if (title != null && Mathf.Approximately(scale, styleScale)) return;
            styleScale = scale;
            int Size(float px) => Mathf.RoundToInt(px * scale);
            title = new GUIStyle(GUI.skin.label) { fontSize = Size(96), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            title.normal.textColor = new Color(1f, 0.6f, 0.1f);
            label = new GUIStyle(GUI.skin.label) { fontSize = Size(26) };
            label.normal.textColor = Color.white;
            center = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter };
            big = new GUIStyle(center) { fontSize = Size(30), fontStyle = FontStyle.Bold };
            button = new GUIStyle(GUI.skin.button) { fontSize = Size(32), fontStyle = FontStyle.Bold };
            smallButton = new GUIStyle(GUI.skin.button) { fontSize = Size(22), fontStyle = FontStyle.Bold };
            field = new GUIStyle(GUI.skin.textField) { fontSize = Size(28), alignment = TextAnchor.MiddleCenter };
            radioStyle = new GUIStyle(label) { fontSize = Size(24), fontStyle = FontStyle.Bold };
            radioStyle.normal.textColor = new Color(0.55f, 1f, 0.6f); // handheld-radio green
            alert = new GUIStyle(label) { fontSize = Size(28), fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight };
            alert.normal.textColor = new Color(1f, 0.75f, 0.2f);
            mono = new GUIStyle(label) { font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New" }, Size(26)), fontSize = Size(26) };
        }

        void OnGUI()
        {
            GUI.depth = 0;
            BuildStyles();
            if (!InSession) DrawMainMenu();
            else if (Net.IsClient && !Net.IsConnectedClient) DrawConnecting();
            else
            {
                DrawHud();
                if (MenuOpen && companyOpen) DrawCompany();
                else if (MenuOpen && reportShown) DrawReport();
                else if (MenuOpen) DrawPauseMenu();
            }
        }

        Rect Column(float y, float h, float w = 520f)
        {
            float s = styleScale;
            return new Rect((Screen.width - w * s) / 2f, y * s, w * s, h * s);
        }

        void Shade() => GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none);

        void DrawMainMenu()
        {
            Shade();
            GUI.Label(Column(140, 130, 1400), "TURNAROUND", title);
            GUI.Label(Column(270, 40, 1400), "Regional Ground Handling Services  -  \"We'll get it out. Probably.\"", center);

            if (joinScreen)
            {
                // Only joiners need an address, so it lives behind JOIN GAME.
                GUI.Label(Column(390, 40, 1400), "Host's IP address", center);
                address = GUI.TextField(Column(440, 60), address, 64, field);
                GUI.Label(Column(505, 40, 1400), "Same PC: 127.0.0.1    Home network: their 192.168.x.x    Internet: their public IP", center);
                if (GUI.Button(Column(570, 80), "CONNECT", button) && !ConnectionManager.StartClient(address))
                    status = "Couldn't start client. Check the address.";
                if (GUI.Button(Column(670, 80), "BACK", button))
                {
                    joinScreen = false;
                    status = null;
                }
            }
            else
            {
                if (GUI.Button(Column(380, 80), "HOST GAME", button) && !ConnectionManager.StartHost())
                    status = "Couldn't host. Is port 7777 already in use?";
                if (GUI.Button(Column(480, 80), "JOIN GAME", button))
                {
                    joinScreen = true;
                    status = null;
                }
                if (GUI.Button(Column(580, 80), "QUIT", button)) Quit();
            }

            if (!string.IsNullOrEmpty(status)) GUI.Label(Column(800, 60, 1400), status, center);
            DrawUpdateStatus();
        }

        void DrawUpdateStatus()
        {
            string line = Turnaround.Core.Updater.Status switch
            {
                Turnaround.Core.Updater.State.Checking => "Checking for updates...",
                Turnaround.Core.Updater.State.Downloading => $"Downloading v{Turnaround.Core.Updater.LatestVersion}... {Turnaround.Core.Updater.Progress:P0}",
                Turnaround.Core.Updater.State.Ready => $"v{Turnaround.Core.Updater.LatestVersion} is ready to install",
                Turnaround.Core.Updater.State.Failed => "Couldn't check for updates (offline?)",
                _ => null,
            };
            if (line != null) GUI.Label(Column(880, 40, 1400), line, center);
            if (Turnaround.Core.Updater.Status == Turnaround.Core.Updater.State.Ready && GUI.Button(Column(930, 70, 640), "RESTART TO UPDATE", button))
                Turnaround.Core.Updater.ApplyAndRestart();
            GUI.Label(new Rect(20, Screen.height - 40, 400, 30), $"v{Application.version}", label);
        }

        void DrawConnecting()
        {
            Shade();
            GUI.Label(Column(420, 60, 1400), $"Clocking in at {address}...", big);
            if (GUI.Button(Column(520, 80), "CANCEL", button)) ConnectionManager.Leave();
        }

        void DrawPauseMenu()
        {
            Shade();
            GUI.Label(Column(200, 130, 1400), "ON BREAK", title);
            if (GUI.Button(Column(400, 80), "BACK TO WORK", button)) MenuOpen = false;
            if (GUI.Button(Column(500, 80), Net.IsServer ? "END SHIFT (HOST)" : "LEAVE", button)) ConnectionManager.Leave();
            if (GUI.Button(Column(600, 80), "QUIT", button)) Quit();
        }

        void DrawReport()
        {
            Shade();
            float s = styleScale;
            float w = 820f * s, h = 800f * s;
            var panel = new Rect((Screen.width - w) / 2f, 30f * s, w, h);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 40f * s, panel.y + 20f * s, w - 80f * s, h - 40f * s), Turn.Report, mono);
            if (Net.IsServer)
            {
                if (GUI.Button(Column(850, 70, 640), "NEXT FLIGHT", button)) Turn.ServerStartNextFlight();
            }
            else GUI.Label(Column(850, 70, 1000), "Waiting for the host to start the next flight", center);
            if (GUI.Button(new Rect(Screen.width / 2f - 330 * s, 935 * s, 320 * s, 70 * s), "OFFICE", button)) OpenCompanyScreen();
            if (GUI.Button(new Rect(Screen.width / 2f + 10 * s, 935 * s, 320 * s, 70 * s), "BACK TO WORK", button)) MenuOpen = false;
        }

        void DrawHud()
        {
            float s = styleScale;
            if (aircraft == null) aircraft = FindFirstObjectByType<AircraftController>(); // clients swap in network copies on join
            int players = Net.IsServer ? Net.ConnectedClientsIds.Count : 0;
            string crew = Net.IsServer ? $"HOSTING  -  {players}/{ConnectionManager.MaxPlayers} CREW" : "CONNECTED";
            string header = aircraft == null
                ? "GATE B4  -  NO FLIGHT ASSIGNED\n" + crew
                : $"{(Turn != null && Turn.Airline.Length > 0 ? Turn.Airline : "FLIGHT")} {aircraft.FlightNumber}  -  {aircraft.definition.shortName}  -  GATE {aircraft.flight.gate}\n{ClockLine()}\nAIRCRAFT: {StateText(aircraft.State)}\n{crew}{CompanyLine()}";
            var bagCounter = Bags;
            if (bagCounter != null && bagCounter.BagsArrived)
                header += $"\nUNLOAD {bagCounter.InboundDelivered}/{bagCounter.InboundTotal}    LOAD {bagCounter.OutboundLoaded}/{bagCounter.OutboundTotal}";
            var safety = SafetyViolationManager.Instance;
            if (safety != null && safety.Count > 0) header += $"    SAFETY VIOLATIONS: {safety.Count}";
            GUI.Label(new Rect(24 * s, 18 * s, 1100 * s, 200 * s), header, label);

            if (showTasks && aircraft != null)
            {
                string list = TaskList();
                float h = (list.Split('\n').Length + 1) * 31f;
                GUI.Box(new Rect(24 * s, 215 * s, 760 * s, h * s), GUIContent.none);
                GUI.Label(new Rect(44 * s, 225 * s, 720 * s, h * s), list, label);
            }

            DrawRadio(s);
            string banner = RandomEventManager.RampClosed ? "RAMP CLOSED - LIGHTNING" : RandomEventManager.Raining ? "RAIN - RAMP IS SLIPPERY" : null;
            if (banner != null) GUI.Label(new Rect(Screen.width - 640 * s, 18 * s, 616 * s, 40 * s), banner, alert);

            if (MenuOpen) return;
            var cx = Screen.width / 2f;
            var cy = Screen.height / 2f;
            GUI.Label(new Rect(cx - 10 * s, cy - 18 * s, 20 * s, 36 * s), "+", center);

            var me = PlayerInteraction.Local;
            if (me == null) return;
            if (!string.IsNullOrEmpty(me.Prompt)) GUI.Label(new Rect(cx - 600 * s, cy + 40 * s, 1200 * s, 140 * s), me.Prompt, big);
            if (me.ThrowCharge > 0f)
            {
                var bar = new Rect(cx - 150 * s, cy + 140 * s, 300 * s, 18 * s);
                GUI.Box(bar, GUIContent.none);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * me.ThrowCharge, bar.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f,
                    Color.Lerp(Color.yellow, Color.red, me.ThrowCharge), 0f, 0f);
            }
        }

        string StateText(AircraftState state) => state switch
        {
            AircraftState.Approaching => "APPROACHING",
            AircraftState.Parked => "ON STAND",
            AircraftState.Servicing => "SERVICING",
            AircraftState.ReadyForPush => "READY FOR PUSH - BRING THE TUG",
            AircraftState.Pushback => "PUSHING BACK",
            AircraftState.Departing => "TAXIING OUT",
            _ => $"GONE  -  TOOK {Bags?.OutboundLoaded ?? 0} BAGS WITH IT",
        };

        string ClockLine()
        {
            var turn = Turn;
            if (turn == null || !turn.ClockRunning) return $"DEPARTURE {turn?.DepartureText ?? "--:--"}    CLOCK NOT STARTED";
            float left = turn.MinutesToDeparture;
            string countdown = left >= 0f ? $"T-{TurnaroundManager.Duration(left)}" : $"LATE {TurnaroundManager.Duration(-left)}";
            return $"DEPARTURE {turn.DepartureText}    TIME {turn.ClockText}    {countdown}";
        }

        // One line per task: check / cross / progress, locked tasks dimmed with a dot.
        string TaskList()
        {
            var tasks = TaskManager.Instance;
            var sb = new System.Text.StringBuilder("TASKS\n");
            if (tasks == null || tasks.Count == 0) return sb.Append("   Waiting for the aircraft").ToString();
            for (int i = 0; i < tasks.Count; i++)
            {
                var def = tasks.Definition(i);
                int value = tasks.ProgressOf(i), target = tasks.TargetOf(i);
                string mark = tasks.StateOf(i) switch
                {
                    TaskState.Complete => "✓",
                    TaskState.Failed => "X",
                    TaskState.Locked => "·",
                    _ when target > 1 && target != 100 => $"{value}/{target}",
                    _ when target == 100 && value > 0 => $"{value}%",
                    _ => "○",
                };
                sb.AppendLine($"{mark} {def.displayName}{(tasks.StateOf(i) == TaskState.Locked ? "  (later)" : "")}");
            }
            var bags = Bags;
            if (bags != null && bags.WrongLoaded > 0) sb.AppendLine($"   Wrong bags in hold: {bags.WrongLoaded}");
            if (bags != null && bags.Damaged > 0) sb.AppendLine($"   Damaged bags: {bags.Damaged}");
            if (bags != null && bags.BagsArrived) sb.AppendLine($"   Still in hold: {bags.InboundInHold} inbound");
            return sb.ToString().TrimEnd();
        }

        static BaggageManager Bags => BaggageManager.Instance;
        static TurnaroundManager Turn => TurnaroundManager.Instance;

        static string CompanyLine()
        {
            var st = CompanyManager.Instance != null ? CompanyManager.Instance.State : null;
            return st == null ? "" : $"    ${st.money:N0}   REP {st.reputation}   DAY {st.day}";
        }

        public static void OpenCompanyScreen()
        {
            companyOpen = true;
            MenuOpen = true;
        }

        // ---------- office computer ----------

        void DrawCompany()
        {
            Shade();
            float s = styleScale;
            var company = CompanyManager.Instance;
            var st = company != null ? company.State : null;
            var area = new Rect((Screen.width - 1300 * s) / 2f, 50 * s, 1300 * s, 980 * s);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 30 * s, area.y + 20 * s, area.width - 60 * s, area.height - 40 * s));
            if (st == null) GUILayout.Label("Connecting to head office...", big);
            else
            {
                GUILayout.Label($"{st.companyName.ToUpper()}    DAY {st.day}    ${st.money:N0}    REPUTATION {st.reputation}", big);
                companyTab = GUILayout.Toolbar(companyTab, new[] { "OVERVIEW", "CONTRACTS", "EQUIPMENT", "UPGRADES" }, button, GUILayout.Height(60 * s));
                GUILayout.Space(10 * s);
                companyScroll = GUILayout.BeginScrollView(companyScroll);
                switch (companyTab)
                {
                    case 0: CompanyOverview(company, st, s); break;
                    case 1: CompanyContracts(company, st, s); break;
                    case 2: CompanyEquipment(company, st, s); break;
                    default: CompanyUpgrades(company, st, s); break;
                }
                GUILayout.EndScrollView();
            }
            if (GUILayout.Button("CLOSE", button, GUILayout.Height(60 * s)))
            {
                companyOpen = false;
                MenuOpen = reportShown; // back to the score sheet if that's where we came from
            }
            GUILayout.EndArea();
        }

        // One line of text with a button on the right. Returns true when an enabled button is clicked.
        bool Row(string text, string buttonText, bool enabled, float s)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(text, label, GUILayout.Width(920 * s));
            GUI.enabled = enabled;
            bool clicked = GUILayout.Button(buttonText, smallButton, GUILayout.Width(280 * s), GUILayout.Height(50 * s));
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(8 * s);
            return clicked && enabled;
        }

        void CompanyOverview(CompanyManager company, CompanyState st, float s)
        {
            GUILayout.Label("CONTRACTS", big);
            if (st.contracts.Count == 0) GUILayout.Label("None. Nobody is paying you. See the CONTRACTS tab.", label);
            foreach (var rec in st.contracts)
            {
                var def = company.ContractDef(rec.id);
                if (def == null) continue;
                GUILayout.Label($"{def.airline}: {rec.turnsToday}/{def.turnsPerDay} turns today, on-time {rec.Otp:P0} (needs {def.minimumOtp:P0}), damage strikes {rec.strikes}/3", label);
            }
            if (!string.IsNullOrEmpty(st.lastDaySummary)) GUILayout.Label("Last day: " + st.lastDaySummary, label);

            GUILayout.Space(16 * s);
            GUILayout.Label("EQUIPMENT", big);
            foreach (var owned in st.equipment)
            {
                var def = company.EquipmentDef(owned.id);
                int cost = company.RepairCost(owned.kind);
                string name = def != null ? def.displayName : owned.id;
                if (Row($"{owned.kind}: {name}   condition {owned.condition:F0}%", cost > 0 ? $"REPAIR ${cost:N0}" : "GOOD AS IT GETS", cost > 0 && st.money >= cost, s))
                    company.RepairRpc(owned.kind);
            }
            GUILayout.Label($"Extra baggage carts: {st.extraCarts}/{company.maxExtraCarts}    Turns worked: {st.totalTurns}", label);

            GUILayout.Space(16 * s);
            if (Row("Declare bankruptcy: wipe the save and start over with nothing.", Net.IsServer ? "NEW COMPANY" : "HOST ONLY", Net.IsServer, s))
                company.NewCompanyRpc();
        }

        void CompanyContracts(CompanyManager company, CompanyState st, float s)
        {
            GUILayout.Label($"Contract slots used: {st.contracts.Count}/{company.MaxContracts}", label);
            foreach (var def in company.contracts)
            {
                bool active = st.Contract(def.id) != null;
                string button = active ? "DROP" : st.reputation < def.reputationRequired ? $"NEEDS REP {def.reputationRequired}"
                    : st.contracts.Count >= company.MaxContracts ? "NO FREE SLOT" : "ACCEPT";
                string text = $"{def.airline} ({def.aircraft})   {def.turnsPerDay} turns/day   ${def.paymentPerTurn:N0}/turn\n" +
                              $"   on-time minimum {def.minimumOtp:P0}, damage tolerance ${def.damageTolerance:N0}/turn, reputation {def.reputationRequired}+";
                if (Row(text, button, active || button == "ACCEPT", s))
                {
                    if (active) company.DropContractRpc(def.id);
                    else company.AcceptContractRpc(def.id);
                }
            }
        }

        void CompanyEquipment(CompanyManager company, CompanyState st, float s)
        {
            foreach (var def in company.equipment)
            {
                if (def.price <= 0) continue; // the junk you started with isn't for sale
                bool owned = def.kind != EquipmentKind.Cart && st.Equipment(def.kind)?.id == def.id;
                bool maxed = def.kind == EquipmentKind.Cart && st.extraCarts >= company.maxExtraCarts;
                string button = owned ? "OWNED" : maxed ? "NO ROOM" : $"BUY ${def.price:N0}";
                if (Row($"{def.displayName}   ({def.kind}, reliability {def.reliability:P0})\n   {def.blurb}", button, !owned && !maxed && st.money >= def.price, s))
                    company.BuyEquipmentRpc(def.id);
            }
        }

        void CompanyUpgrades(CompanyManager company, CompanyState st, float s)
        {
            foreach (var def in company.upgrades)
            {
                bool owned = st.Has(def.id);
                if (Row($"{def.displayName}\n   {def.blurb}", owned ? "OWNED" : $"BUY ${def.price:N0}", !owned && st.money >= def.price, s))
                    company.BuyUpgradeRpc(def.id);
            }
        }

        // Last few radio lines, bottom left, fading out after a while.
        void DrawRadio(float s)
        {
            var radio = RadioManager.Instance;
            if (radio == null) return;
            float y = Screen.height - 60 * s;
            for (int i = radio.Recent.Count - 1; i >= 0; i--)
            {
                var m = radio.Recent[i];
                float age = Time.time - m.time;
                if (age > 12f) continue;
                var old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(12f - age));
                GUI.Label(new Rect(24 * s, y, 1200 * s, 36 * s), $"[{m.speaker}] {m.text}", radioStyle);
                GUI.color = old;
                y -= 34 * s;
            }
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
