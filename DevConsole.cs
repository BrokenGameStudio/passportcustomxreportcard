// DevConsole.cs — Console de développement restreinte pour PEAK
// Accès : uniquement les joueurs dont le NickName == "bynex" (insensible à la casse)
// Touche : Suppr → demande d'accès à l'host → accepté/refusé → console ouverte
// Totalement indépendant de la pagination et du système de report.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace passportcustom
{
    // =========================================================================
    //  CODES D'ÉVÉNEMENTS PHOTON (plage 221-229, distincte du ReportCard)
    // =========================================================================
    public static class DevConsoleEvents
    {
        /// <summary>bynex → host : demande d'accès à la console</summary>
        public const byte EVT_REQUEST_ACCESS  = 221;

        /// <summary>host → bynex : réponse (true = accepté, false = refusé)</summary>
        public const byte EVT_ACCESS_RESPONSE = 222;

        /// <summary>bynex → host : exécution d'une commande</summary>
        public const byte EVT_EXEC_COMMAND    = 223;

        /// <summary>host → bynex : résultat d'une commande</summary>
        public const byte EVT_COMMAND_RESULT  = 224;
    }

    // =========================================================================
    //  GESTIONNAIRE PRINCIPAL
    // =========================================================================
    public class DevConsoleManager : MonoBehaviour, IOnEventCallback
    {
        public static DevConsoleManager Instance { get; private set; }

        // Nom autorisé (insensible à la casse)
        private const string ALLOWED_NAME   = "bynex";
        private const KeyCode OPEN_KEY      = KeyCode.Delete;

        // Options Photon
        private static readonly SendOptions _reliable = SendOptions.SendReliable;

        // État
        private bool _accessGranted    = false;   // côté bynex : accès accepté par l'host
        private bool _waitingForAnswer  = false;   // demande en cours

        // Acteur de bynex côté host (pour lui envoyer les résultats)
        private int _authorizedActor = -1;

        // UI
        private DevConsoleUI _ui;

        private static ManualLogSource Log => Plugin.Log;

        // =====================================================================
        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            PhotonNetwork.AddCallbackTarget(this);
            _ui = gameObject.AddComponent<DevConsoleUI>();
            _ui.Manager = this;
            Log.LogInfo("[DevConsole] Initialisé.");
        }

        void OnDestroy() => PhotonNetwork.RemoveCallbackTarget(this);

        void Update()
        {
            if (!Input.GetKeyDown(OPEN_KEY))      return;
            if (!PhotonNetwork.IsConnected)        return;
            if (!IsLocalPlayerBynex())             return;   // touche ignorée pour les autres

            if (_accessGranted)
            {
                _ui.ToggleConsole();
            }
            else if (!_waitingForAnswer)
            {
                RequestAccess();
            }
        }

        // =====================================================================
        //  LOGIQUE CÔTÉ CLIENT (bynex)
        // =====================================================================

        private bool IsLocalPlayerBynex()
        {
            var local = PhotonNetwork.LocalPlayer;
            return local != null &&
                   !string.IsNullOrEmpty(local.NickName) &&
                   local.NickName.Trim().ToLower() == ALLOWED_NAME;
        }

        private void RequestAccess()
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                // Envoyer la demande au MasterClient
                _waitingForAnswer = true;
                var opts = new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient };
                PhotonNetwork.RaiseEvent(DevConsoleEvents.EVT_REQUEST_ACCESS,
                    PhotonNetwork.LocalPlayer.NickName, opts, _reliable);

                Log.LogInfo("[DevConsole] Demande d'accès envoyée au host.");
                _ui.ShowPending("Demande envoyée à l'host...");
            }
            else
            {
                // L'utilisateur bynex EST le host → accès direct
                _accessGranted   = true;
                _authorizedActor = PhotonNetwork.LocalPlayer.ActorNumber;
                Log.LogInfo("[DevConsole] bynex est l'host → accès direct.");
                _ui.OpenConsole();
            }
        }

        // =====================================================================
        //  LOGIQUE CÔTÉ HOST
        // =====================================================================

        /// <summary>Appelé par l'UI host quand il clique Accepter.</summary>
        public void GrantAccess(int requesterActor)
        {
            _authorizedActor = requesterActor;
            var toRequester = new RaiseEventOptions { TargetActors = new[] { requesterActor } };
            PhotonNetwork.RaiseEvent(DevConsoleEvents.EVT_ACCESS_RESPONSE,
                true, toRequester, _reliable);

            Log.LogInfo($"[DevConsole] Accès ACCORDÉ à actor {requesterActor}.");
            _ui.CloseHostRequest();
        }

        /// <summary>Appelé par l'UI host quand il clique Refuser.</summary>
        public void DenyAccess(int requesterActor)
        {
            var toRequester = new RaiseEventOptions { TargetActors = new[] { requesterActor } };
            PhotonNetwork.RaiseEvent(DevConsoleEvents.EVT_ACCESS_RESPONSE,
                false, toRequester, _reliable);

            Log.LogInfo($"[DevConsole] Accès REFUSÉ à actor {requesterActor}.");
            _ui.CloseHostRequest();
        }

        // =====================================================================
        //  EXÉCUTION DES COMMANDES
        // =====================================================================

        /// <summary>bynex envoie une commande à l'host pour exécution.</summary>
        public void ExecuteCommand(string input)
        {
            input = input?.Trim() ?? "";
            if (string.IsNullOrEmpty(input)) return;

            _ui.AppendLog($"<color=#aaaaff>> {input}</color>");

            if (PhotonNetwork.IsMasterClient)
            {
                // L'host exécute directement
                string result = RunCommand(input);
                _ui.AppendLog(result);
            }
            else
            {
                // Envoyer la commande à l'host
                var opts = new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient };
                PhotonNetwork.RaiseEvent(DevConsoleEvents.EVT_EXEC_COMMAND,
                    input, opts, _reliable);
            }
        }

        /// <summary>Exécution réelle de la commande — côté host uniquement.</summary>
        private string RunCommand(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            var parts = input.Split(' ');
            string cmd = parts[0].ToLower();

            switch (cmd)
            {
                // ---- Infos ----
                case "help":
                    return "<color=#ffff88>Commandes disponibles :</color>\n" +
                           "  <b>players</b>          — liste les joueurs connectés\n" +
                           "  <b>kick</b> [actor]      — kick un joueur par son actor number\n" +
                           "  <b>ping</b>              — latence des joueurs\n" +
                           "  <b>room</b>              — infos sur la room\n" +
                           "  <b>time</b>              — heure serveur / temps de jeu\n" +
                           "  <b>clear</b>             — efface la console\n" +
                           "  <b>say</b> [message]     — envoie un message dans les toasts de tous\n" +
                           "  <b>master</b>            — affiche qui est le MasterClient";

                case "players":
                    return ListPlayers();

                case "kick":
                    return KickPlayer(parts);

                case "ping":
                    return ListPings();

                case "room":
                    return RoomInfo();

                case "time":
                    return $"Time.time = {Time.time:F1}s  |  Time.realtimeSinceStartup = {Time.realtimeSinceStartup:F1}s";

                case "clear":
                    _ui.ClearLog();
                    return "";

                case "say":
                    if (parts.Length < 2) return "<color=#ff8888>Usage : say [message]</color>";
                    string msg = string.Join(" ", parts, 1, parts.Length - 1);
                    BroadcastSay(msg);
                    return $"<color=#88ff88>Message diffusé : \"{msg}\"</color>";

                case "master":
                    var mc = PhotonNetwork.MasterClient;
                    return mc != null
                        ? $"MasterClient : <b>{mc.NickName}</b> (actor #{mc.ActorNumber})"
                        : "MasterClient introuvable.";

                default:
                    return $"<color=#ff8888>Commande inconnue : '{cmd}'. Tapez <b>help</b>.</color>";
            }
        }

        private string ListPlayers()
        {
            var players = PhotonNetwork.PlayerList;
            if (players == null || players.Length == 0) return "Aucun joueur.";

            var sb = new StringBuilder();
            sb.AppendLine($"<color=#ffff88>{players.Length} joueur(s) :</color>");
            foreach (var p in players)
            {
                string tag = p.IsMasterClient ? " <color=#ffcc00>[HOST]</color>" : "";
                string me  = p.IsLocal        ? " <color=#88ff88>[toi]</color>"  : "";
                sb.AppendLine($"  #{p.ActorNumber}  <b>{p.NickName}</b>{tag}{me}");
            }
            return sb.ToString().TrimEnd();
        }

        private string ListPings()
        {
            // Photon expose seulement le ping local
            int localPing = PhotonNetwork.GetPing();
            var sb = new StringBuilder();
            sb.AppendLine($"<color=#ffff88>Ping :</color>");
            sb.AppendLine($"  Local ({PhotonNetwork.LocalPlayer.NickName}) : <b>{localPing} ms</b>");
            sb.AppendLine("  (Photon n'expose pas le ping des autres clients)");
            return sb.ToString().TrimEnd();
        }

        private string KickPlayer(string[] parts)
        {
            if (!PhotonNetwork.IsMasterClient)
                return "<color=#ff8888>Seul l'host peut kick.</color>";

            if (parts.Length < 2 || !int.TryParse(parts[1], out int actor))
                return "<color=#ff8888>Usage : kick [actorNumber]</color>";

            var target = PhotonNetwork.CurrentRoom?.GetPlayer(actor);
            if (target == null)
                return $"<color=#ff8888>Actor #{actor} introuvable.</color>";

            if (target.IsLocal)
                return "<color=#ff8888>Tu ne peux pas te kicker toi-même.</color>";

            string name = target.NickName ?? $"Actor#{actor}";
            PhotonNetwork.CloseConnection(target);
            return $"<color=#88ff88>Joueur <b>{name}</b> (actor #{actor}) kické.</color>";
        }

        private string RoomInfo()
        {
            var room = PhotonNetwork.CurrentRoom;
            if (room == null) return "Pas dans une room.";
            return $"<color=#ffff88>Room :</color>\n" +
                   $"  Nom        : <b>{room.Name}</b>\n" +
                   $"  Joueurs    : {room.PlayerCount} / {room.MaxPlayers}\n" +
                   $"  Visible    : {room.IsVisible}\n" +
                   $"  Ouverte    : {room.IsOpen}";
        }

        private void BroadcastSay(string message)
        {
            var opts = new RaiseEventOptions { Receivers = ReceiverGroup.All };
            PhotonNetwork.RaiseEvent(DevConsoleEvents.EVT_COMMAND_RESULT,
                $"<color=#ffcc00>[HOST]</color> {message}", opts, _reliable);
        }

        // =====================================================================
        //  IOnEventCallback
        // =====================================================================
        public void OnEvent(EventData photonEvent)
        {
            switch (photonEvent.Code)
            {
                case DevConsoleEvents.EVT_REQUEST_ACCESS:
                    HandleRequestAccess(photonEvent);
                    break;

                case DevConsoleEvents.EVT_ACCESS_RESPONSE:
                    HandleAccessResponse(photonEvent);
                    break;

                case DevConsoleEvents.EVT_EXEC_COMMAND:
                    HandleExecCommand(photonEvent);
                    break;

                case DevConsoleEvents.EVT_COMMAND_RESULT:
                    HandleCommandResult(photonEvent);
                    break;
            }
        }

        private void HandleRequestAccess(EventData e)
        {
            if (!PhotonNetwork.IsMasterClient) return;

            string requesterName = e.CustomData as string ?? "?";
            int    requesterActor = e.Sender;

            // Vérifier que le demandeur est bien un bynex
            var sender = PhotonNetwork.CurrentRoom?.GetPlayer(requesterActor);
            string realName = sender?.NickName ?? requesterName;
            if (!realName.Trim().ToLower().Equals(ALLOWED_NAME))
            {
                Log.LogWarning($"[DevConsole] Demande d'accès de '{realName}' refusée automatiquement (pas bynex).");
                var deny = new RaiseEventOptions { TargetActors = new[] { requesterActor } };
                PhotonNetwork.RaiseEvent(DevConsoleEvents.EVT_ACCESS_RESPONSE, false, deny, _reliable);
                return;
            }

            Log.LogInfo($"[DevConsole] Demande d'accès de '{realName}' (actor {requesterActor}) → popup host.");
            _ui.ShowHostRequest(requesterActor, realName);
        }

        private void HandleAccessResponse(EventData e)
        {
            // Vérifier que c'est bien le MasterClient qui répond
            var sender = PhotonNetwork.CurrentRoom?.GetPlayer(e.Sender);
            if (sender == null || !sender.IsMasterClient) return;

            bool granted = e.CustomData is bool b && b;
            _waitingForAnswer = false;

            if (granted)
            {
                _accessGranted = true;
                Log.LogInfo("[DevConsole] Accès accordé par l'host.");
                _ui.HidePending();
                _ui.OpenConsole();
            }
            else
            {
                _accessGranted = false;
                Log.LogInfo("[DevConsole] Accès refusé par l'host.");
                _ui.ShowRefused();
            }
        }

        private void HandleExecCommand(EventData e)
        {
            if (!PhotonNetwork.IsMasterClient) return;

            // Vérifier que c'est bien l'actor autorisé
            if (e.Sender != _authorizedActor)
            {
                Log.LogWarning($"[DevConsole] Commande rejetée (sender={e.Sender} ≠ autorisé={_authorizedActor}).");
                return;
            }

            string cmd    = e.CustomData as string ?? "";
            string result = RunCommand(cmd);

            if (string.IsNullOrEmpty(result)) return;

            var toSender = new RaiseEventOptions { TargetActors = new[] { e.Sender } };
            PhotonNetwork.RaiseEvent(DevConsoleEvents.EVT_COMMAND_RESULT, result, toSender, _reliable);
        }

        private void HandleCommandResult(EventData e)
        {
            // Reçu soit par bynex (résultat de commande), soit par tout le monde (say)
            string result = e.CustomData as string ?? "";
            if (!string.IsNullOrEmpty(result))
                _ui.AppendLog(result);
        }
    }

    // =========================================================================
    //  UI DE LA CONSOLE
    // =========================================================================
    public class DevConsoleUI : MonoBehaviour
    {
        internal DevConsoleManager Manager;

        // Panels
        private GameObject _consoleRoot;      // La console principale
        private GameObject _pendingRoot;      // "En attente de l'host..."
        private GameObject _refusedRoot;      // "Refusé"
        private GameObject _hostRequestRoot;  // Popup host : Accepter/Refuser

        // Refs console
        private TMP_InputField  _cmdInput;
        private TextMeshProUGUI _logText;
        private ScrollRect      _logScroll;
        private StringBuilder   _logBuffer = new StringBuilder();

        // Couleurs
        private static readonly Color C_BG      = new Color(0.04f, 0.04f, 0.06f, 0.97f);
        private static readonly Color C_HEADER  = new Color(0.10f, 0.10f, 0.14f, 1.00f);
        private static readonly Color C_INPUT   = new Color(0.08f, 0.08f, 0.11f, 1.00f);
        private static readonly Color C_ACCENT  = new Color(0.20f, 0.80f, 0.40f, 1.00f);  // vert terminal
        private static readonly Color C_RED     = new Color(0.85f, 0.18f, 0.18f, 1.00f);
        private static readonly Color C_GREEN   = new Color(0.12f, 0.60f, 0.20f, 1.00f);
        private static readonly Color C_GREY    = new Color(0.20f, 0.20f, 0.23f, 1.00f);
        private static readonly Color C_TEXT    = new Color(0.88f, 0.92f, 0.88f, 1.00f);

        private Canvas _canvas;
        private Coroutine _refusedCoroutine;

        // =====================================================================
        void Awake()
        {
            BuildCanvas();
            BuildConsole();
            BuildPendingOverlay();
            BuildRefusedOverlay();
            BuildHostRequestPopup();
        }

        // =====================================================================
        //  CONSOLE PRINCIPALE
        // =====================================================================
        public void ToggleConsole()
        {
            if (_consoleRoot.activeSelf) CloseConsole();
            else                          OpenConsole();
        }

        public void OpenConsole()
        {
            _consoleRoot.SetActive(true);
            _cmdInput.text = "";
            _cmdInput.ActivateInputField();
            ScrollToBottom();
        }

        public void CloseConsole()
        {
            _consoleRoot.SetActive(false);
        }

        public void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            _logBuffer.AppendLine(line);

            // Garder le buffer raisonnable (max ~300 lignes)
            string full = _logBuffer.ToString();
            var lines = full.Split('\n');
            if (lines.Length > 300)
            {
                _logBuffer.Clear();
                _logBuffer.Append(string.Join("\n", lines, lines.Length - 250, 250));
            }

            _logText.text = _logBuffer.ToString();
            ScrollToBottom();
        }

        public void ClearLog()
        {
            _logBuffer.Clear();
            _logText.text = "";
        }

        private void ScrollToBottom()
        {
            // Forcer le rebuild du layout avant de scroller
            Canvas.ForceUpdateCanvases();
            if (_logScroll != null)
                _logScroll.verticalNormalizedPosition = 0f;
        }

        // =====================================================================
        //  ÉTATS INTERMÉDIAIRES
        // =====================================================================
        public void ShowPending(string msg)
        {
            _pendingRoot.transform.Find("Label").GetComponent<TextMeshProUGUI>().text = msg;
            _pendingRoot.SetActive(true);
        }

        public void HidePending() => _pendingRoot.SetActive(false);

        public void ShowRefused()
        {
            _pendingRoot.SetActive(false);
            _refusedRoot.SetActive(true);
            if (_refusedCoroutine != null) StopCoroutine(_refusedCoroutine);
            _refusedCoroutine = StartCoroutine(HideRefusedAfter(3f));
        }

        private IEnumerator HideRefusedAfter(float t)
        {
            yield return new WaitForSeconds(t);
            _refusedRoot.SetActive(false);
        }

        // =====================================================================
        //  POPUP HOST
        // =====================================================================
        public void ShowHostRequest(int requesterActor, string requesterName)
        {
            _hostRequestRoot.transform.Find("Label").GetComponent<TextMeshProUGUI>().text =
                $"<b>{requesterName}</b> demande\nl'accès à la console.";

            var btnAccept = _hostRequestRoot.transform.Find("BtnAccept").GetComponent<Button>();
            var btnDeny   = _hostRequestRoot.transform.Find("BtnDeny").GetComponent<Button>();

            btnAccept.onClick.RemoveAllListeners();
            btnAccept.onClick.AddListener(() => Manager.GrantAccess(requesterActor));

            btnDeny.onClick.RemoveAllListeners();
            btnDeny.onClick.AddListener(() => Manager.DenyAccess(requesterActor));

            _hostRequestRoot.SetActive(true);
        }

        public void CloseHostRequest() => _hostRequestRoot.SetActive(false);

        // =====================================================================
        //  CONSTRUCTION UI
        // =====================================================================
        private void BuildCanvas()
        {
            var go = new GameObject("DevConsole_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(go);
            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 400;   // Au-dessus de tout le reste

            var cs = go.GetComponent<CanvasScaler>();
            cs.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            cs.referenceResolution = new Vector2(1920, 1080);
        }

        private void BuildConsole()
        {
            // Root — couvre la moitié basse de l'écran
            _consoleRoot = MakePanel("DC_Console", new Vector2(1400, 480), new Vector2(0, -280));
            _consoleRoot.GetComponent<Image>().color = C_BG;

            // ---- Header ----
            var header = new GameObject("Header", typeof(RectTransform), typeof(Image));
            header.transform.SetParent(_consoleRoot.transform, false);
            var hrt = header.GetComponent<RectTransform>();
            hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(1, 1);
            hrt.pivot     = new Vector2(0.5f, 1);
            hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
            hrt.sizeDelta = new Vector2(0, 34);
            header.GetComponent<Image>().color = C_HEADER;

            var title = MakeTMP(header.transform, "DevConsole  <color=#44aa66>●</color>", 14, C_ACCENT);
            var trt = title.GetComponent<RectTransform>();
            trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 1);
            trt.offsetMin = new Vector2(12, 0); trt.offsetMax = new Vector2(-40, 0);
            title.alignment = TextAlignmentOptions.Left;

            // Bouton fermer
            var closeBtn = MakeBtn(_consoleRoot.transform, "✕", C_GREY * 0.7f,
                () => CloseConsole(), new Vector2(30, 30), new Vector2(686, 217));

            // ---- Zone de log (scrollable) ----
            var logArea = MakeScrollArea(_consoleRoot.transform,
                new Vector2(1380, 390), new Vector2(0, 10));
            _logScroll = logArea.GetComponent<ScrollRect>();
            var content = logArea.transform.Find("Viewport/Content");

            var logGo = new GameObject("LogText", typeof(RectTransform), typeof(TextMeshProUGUI));
            logGo.transform.SetParent(content, false);
            _logText = logGo.GetComponent<TextMeshProUGUI>();
            _logText.fontSize    = 12;
            _logText.color       = C_TEXT;
            _logText.alignment   = TextAlignmentOptions.TopLeft;
            _logText.textWrappingMode = TMPro.TextWrappingModes.Normal;
            _logText.richText    = true;
            var lrt = logGo.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 1);
            lrt.offsetMin = new Vector2(8, 4); lrt.offsetMax = new Vector2(-8, -4);

            // ---- Barre de saisie ----
            var inputBar = new GameObject("InputBar", typeof(RectTransform), typeof(Image));
            inputBar.transform.SetParent(_consoleRoot.transform, false);
            var irt = inputBar.GetComponent<RectTransform>();
            irt.anchorMin = new Vector2(0, 0); irt.anchorMax = new Vector2(1, 0);
            irt.pivot     = new Vector2(0.5f, 0);
            irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;
            irt.sizeDelta = new Vector2(0, 36);
            inputBar.GetComponent<Image>().color = C_HEADER;

            // Prompt "$"
            var prompt = MakeTMP(inputBar.transform, ">", 14, C_ACCENT);
            var prt = prompt.GetComponent<RectTransform>();
            prt.anchorMin = new Vector2(0, 0); prt.anchorMax = new Vector2(0, 1);
            prt.offsetMin = new Vector2(8, 2); prt.offsetMax = new Vector2(24, -2);
            prompt.alignment = TextAlignmentOptions.Center;

            // Champ de saisie
            _cmdInput = BuildInputField(inputBar.transform, new Vector2(-60, 0), new Vector2(-8, 0));
            _cmdInput.onSubmit.AddListener(text =>
            {
                Manager.ExecuteCommand(text);
                _cmdInput.text = "";
                _cmdInput.ActivateInputField();
            });

            AppendLog($"<color=#44aa66>DevConsole v1.0 — Bienvenue, bynex.</color>");
            AppendLog($"<color=#888888>Tapez <b>help</b> pour la liste des commandes. [Suppr] pour fermer.</color>");

            _consoleRoot.SetActive(false);
        }

        private void BuildPendingOverlay()
        {
            _pendingRoot = MakePanel("DC_Pending", new Vector2(400, 70), new Vector2(0, -430));
            _pendingRoot.GetComponent<Image>().color = new Color(0.05f, 0.05f, 0.08f, 0.95f);

            var lbl = MakeTMP(_pendingRoot.transform, "...", 14, C_TEXT);
            lbl.name = "Label";
            var rt = lbl.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(12, 4); rt.offsetMax = new Vector2(-12, -4);
            lbl.alignment = TextAlignmentOptions.Center;

            _pendingRoot.SetActive(false);
        }

        private void BuildRefusedOverlay()
        {
            _refusedRoot = MakePanel("DC_Refused", new Vector2(320, 60), new Vector2(0, -430));
            _refusedRoot.GetComponent<Image>().color = new Color(0.25f, 0.05f, 0.05f, 0.95f);

            var lbl = MakeTMP(_refusedRoot.transform,
                "⛔  Accès refusé par l'host.", 14, new Color(1f, 0.4f, 0.4f));
            var rt = lbl.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(12, 4); rt.offsetMax = new Vector2(-12, -4);
            lbl.alignment = TextAlignmentOptions.Center;

            _refusedRoot.SetActive(false);
        }

        private void BuildHostRequestPopup()
        {
            _hostRequestRoot = MakePanel("DC_HostRequest", new Vector2(320, 150), new Vector2(600, 400));
            _hostRequestRoot.GetComponent<Image>().color = new Color(0.07f, 0.07f, 0.10f, 0.97f);

            // Icône + titre
            var title = MakeTMP(_hostRequestRoot.transform, "🖥  Demande console", 14,
                new Color(0.6f, 0.8f, 1f));
            title.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 45);
            title.alignment = TextAlignmentOptions.Center;

            // Label dynamique
            var lbl = MakeTMP(_hostRequestRoot.transform, "", 13, C_TEXT);
            lbl.name = "Label";
            var lrt = lbl.GetComponent<RectTransform>();
            lrt.sizeDelta        = new Vector2(290, 40);
            lrt.anchoredPosition = new Vector2(0, 8);
            lbl.alignment        = TextAlignmentOptions.Center;
            lbl.textWrappingMode = TMPro.TextWrappingModes.Normal;

            // Boutons
            var btnAccept = MakeBtn(_hostRequestRoot.transform, "✓  Accepter", C_GREEN,
                () => { }, new Vector2(130, 36), new Vector2(70, -48));
            btnAccept.name = "BtnAccept";

            var btnDeny = MakeBtn(_hostRequestRoot.transform, "✕  Refuser", C_RED,
                () => { }, new Vector2(130, 36), new Vector2(-72, -48));
            btnDeny.name = "BtnDeny";

            // Bordure colorée
            var outline = _hostRequestRoot.AddComponent<Outline>();
            outline.effectColor    = new Color(0.4f, 0.6f, 1f, 0.4f);
            outline.effectDistance = new Vector2(1, -1);

            _hostRequestRoot.SetActive(false);
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================
        private GameObject MakePanel(string name, Vector2 size, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_canvas.transform, false);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta        = size;
            rt.anchoredPosition = pos;
            return go;
        }

        private TextMeshProUGUI MakeTMP(Transform parent, string text, int size, Color color)
        {
            var go = new GameObject("T", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text     = text;
            tmp.fontSize = size;
            tmp.color    = color;
            tmp.richText = true;
            return tmp;
        }

        private GameObject MakeBtn(Transform parent, string label, Color bg,
            Action onClick, Vector2 size, Vector2 pos)
        {
            var go = new GameObject("Btn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta        = size;
            rt.anchoredPosition = pos;
            go.GetComponent<Image>().color = bg;

            var btn = go.GetComponent<Button>();
            var cols = btn.colors;
            cols.normalColor      = bg;
            cols.highlightedColor = bg * 1.3f;
            cols.pressedColor     = bg * 0.6f;
            btn.colors = cols;
            btn.onClick.AddListener(() => onClick());

            var tGo = new GameObject("L", typeof(RectTransform), typeof(TextMeshProUGUI));
            tGo.transform.SetParent(go.transform, false);
            var tmp = tGo.GetComponent<TextMeshProUGUI>();
            tmp.text      = label;
            tmp.fontSize  = 13;
            tmp.color     = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            var trt = tGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(4, 2); trt.offsetMax = new Vector2(-4, -2);

            return go;
        }

        private TMP_InputField BuildInputField(Transform parent, Vector2 anchorOffsetMin, Vector2 anchorOffsetMax)
        {
            var go = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = C_INPUT;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1);
            rt.offsetMin = new Vector2(28, 2) + anchorOffsetMin;
            rt.offsetMax = new Vector2(0, -2) + anchorOffsetMax;

            var field = go.GetComponent<TMP_InputField>();
            field.characterLimit = 200;

            // Text area
            var ta = new GameObject("TA", typeof(RectTransform), typeof(TextMeshProUGUI));
            ta.transform.SetParent(go.transform, false);
            var taT = ta.GetComponent<TextMeshProUGUI>();
            taT.fontSize = 13;
            taT.color    = C_ACCENT;
            var tart = ta.GetComponent<RectTransform>();
            tart.anchorMin = Vector2.zero; tart.anchorMax = Vector2.one;
            tart.offsetMin = new Vector2(6, 2); tart.offsetMax = new Vector2(-6, -2);
            field.textComponent = taT;

            // Placeholder
            var ph = new GameObject("PH", typeof(RectTransform), typeof(TextMeshProUGUI));
            ph.transform.SetParent(go.transform, false);
            var phT = ph.GetComponent<TextMeshProUGUI>();
            phT.text     = "Entrez une commande...";
            phT.fontSize = 13;
            phT.color    = C_TEXT * 0.3f;
            phT.fontStyle = FontStyles.Italic;
            var phrt = ph.GetComponent<RectTransform>();
            phrt.anchorMin = Vector2.zero; phrt.anchorMax = Vector2.one;
            phrt.offsetMin = new Vector2(6, 2); phrt.offsetMax = new Vector2(-6, -2);
            field.placeholder = phT;

            return field;
        }

        private Transform MakeScrollArea(Transform parent, Vector2 size, Vector2 pos)
        {
            var root = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            root.transform.SetParent(parent, false);
            var rootRT = root.GetComponent<RectTransform>();
            rootRT.sizeDelta        = size;
            rootRT.anchoredPosition = pos;
            root.GetComponent<Image>().color = Color.clear;

            var vp = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            vp.transform.SetParent(root.transform, false);
            var vpRT = vp.GetComponent<RectTransform>();
            vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
            vpRT.offsetMin = Vector2.zero; vpRT.offsetMax = Vector2.zero;
            vp.GetComponent<Image>().color = Color.clear;
            vp.GetComponent<Mask>().showMaskGraphic = false;

            var content = new GameObject("Content",
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(vp.transform, false);
            var cRT = content.GetComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0, 0); cRT.anchorMax = new Vector2(1, 0);
            cRT.pivot     = new Vector2(0.5f, 0);
            cRT.offsetMin = Vector2.zero; cRT.offsetMax = Vector2.zero;

            var vlg = content.GetComponent<VerticalLayoutGroup>();
            vlg.childControlWidth  = true;
            vlg.childControlHeight = true;
            vlg.spacing            = 0;
            vlg.padding            = new RectOffset(0, 0, 4, 4);

            var csf = content.GetComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var sr = root.GetComponent<ScrollRect>();
            sr.viewport          = vpRT;
            sr.content           = cRT;
            sr.horizontal        = false;
            sr.vertical          = true;
            sr.scrollSensitivity = 30f;
            sr.movementType      = ScrollRect.MovementType.Clamped;

            return root.transform;
        }
    }

    // =========================================================================
    //  PATCH HARMONY — injection du DevConsoleManager dans la scène
    // =========================================================================
    [HarmonyPatch(typeof(PassportManager), "Awake")]
    static class DevConsoleInjector
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (DevConsoleManager.Instance != null) return;
            if (!PhotonNetwork.IsConnected)
            {
                Plugin.Log?.LogInfo("[DevConsole] Non connecté — injection ignorée.");
                return;
            }

            var go = new GameObject("DevConsoleManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<DevConsoleManager>();
            Plugin.Log?.LogInfo("[DevConsole] DevConsoleManager injecté dans la scène.");
        }
    }
}
