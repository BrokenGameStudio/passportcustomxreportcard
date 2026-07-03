// ReportCard.cs — Système de report en jeu pour PEAK
// Architecture : Photon RaiseEvent / IOnEventCallback (pas de PhotonView requis)
// Aucune dépendance sur la pagination — fonctionne de façon totalement indépendante.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using PhotonPlayer = Photon.Realtime.Player;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace passportcustom
{
    // =========================================================================
    //  CONSTANTES D'ÉVÉNEMENTS PHOTON
    //  On réserve deux codes d'event custom (200-299 : zone libre pour les mods)
    // =========================================================================
    public static class ReportEvents
    {
        /// <summary>Client → MasterClient : envoi d'un report</summary>
        public const byte EVT_SUBMIT_REPORT  = 211;

        /// <summary>MasterClient → joueur ciblé : notification de ban</summary>
        public const byte EVT_NOTIFY_BANNED  = 212;

        /// <summary>MasterClient → tous : diffusion d'un ban</summary>
        public const byte EVT_BROADCAST_BAN  = 213;
    }

    // =========================================================================
    //  MODÈLE DE DONNÉES
    // =========================================================================
    public class ReportEntry
    {
        public int    ReporterActorNumber;
        public string ReporterName;
        public int    TargetActorNumber;
        public string TargetName;
        public string Reason;
        public float  Timestamp;

        public ReportEntry(int rActor, string rName, int tActor, string tName, string reason)
        {
            ReporterActorNumber = rActor;
            ReporterName        = rName;
            TargetActorNumber   = tActor;
            TargetName          = tName;
            Reason              = reason;
            Timestamp           = Time.time;
        }
    }

    // =========================================================================
    //  GESTIONNAIRE CENTRAL — MonoBehaviour ordinaire + IOnEventCallback
    //  Pas de PhotonView, pas d'allocation de ViewID.
    // =========================================================================
    public class ReportCardManager : MonoBehaviour, IOnEventCallback
    {
        // ----- Singleton -----
        public static ReportCardManager Instance { get; private set; }

        // ----- Config -----
        public const int    REPORTS_THRESHOLD = 3;
        public const int    MAX_REASON_LENGTH = 120;
        public const KeyCode OPEN_KEY         = KeyCode.F1;

        // ----- Options Photon réutilisées -----
        // Envoi fiable vers le MasterClient uniquement
        private static readonly RaiseEventOptions _toMaster = new RaiseEventOptions
            { Receivers = ReceiverGroup.MasterClient };

        // Envoi fiable vers tous sauf l'expéditeur
        private static readonly RaiseEventOptions _toOthers = new RaiseEventOptions
            { Receivers = ReceiverGroup.Others };

        // Envoi fiable vers tout le monde
        private static readonly RaiseEventOptions _toAll = new RaiseEventOptions
            { Receivers = ReceiverGroup.All };

        private static readonly SendOptions _reliable = SendOptions.SendReliable;

        // ----- État -----
        // actorNumber cible → liste de reports reçus (MasterClient uniquement)
        private readonly Dictionary<int, List<ReportEntry>> _reports
            = new Dictionary<int, List<ReportEntry>>();

        // Cibles déjà reportées par le joueur local (anti-spam)
        private readonly HashSet<int> _alreadyReported = new HashSet<int>();

        // Acteurs bannis cette session
        private readonly HashSet<int> _banned = new HashSet<int>();

        // ----- UI -----
        private ReportCardUI _ui;

        private static ManualLogSource Log => Plugin.Log;

        // =====================================================================
        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            // S'abonner aux events Photon
            PhotonNetwork.AddCallbackTarget(this);
            Log.LogInfo("[ReportCard] Callbacks Photon enregistrés.");

            _ui = gameObject.AddComponent<ReportCardUI>();
            _ui.Manager = this;
        }

        void OnDestroy()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
        }

        void Update()
        {
            if (Input.GetKeyDown(OPEN_KEY) && PhotonNetwork.IsConnected)
                _ui.ToggleReportMenu();
        }

        // =====================================================================
        //  API PUBLIQUE
        // =====================================================================

        /// <summary>Soumet un report — appelé par l'UI côté client.</summary>
        public void SubmitReport(int targetActor, string reason)
        {
            if (!PhotonNetwork.IsConnected)              { Log.LogWarning("[ReportCard] Non connecté."); return; }
            if (PhotonNetwork.LocalPlayer.ActorNumber == targetActor) { _ui.ShowToast("Tu ne peux pas te reporter toi-même."); return; }
            if (_alreadyReported.Contains(targetActor))               { _ui.ShowToast("Tu as déjà reporté ce joueur."); return; }

            reason = (reason ?? "").Trim();
            if (string.IsNullOrEmpty(reason)) { _ui.ShowToast("La raison ne peut pas être vide."); return; }
            if (reason.Length > MAX_REASON_LENGTH) reason = reason.Substring(0, MAX_REASON_LENGTH);

            _alreadyReported.Add(targetActor);

            var local = PhotonNetwork.LocalPlayer;
            // Données envoyées : [reporterActor(int), reporterName(string), targetActor(int), reason(string)]
            object[] data = { local.ActorNumber, local.NickName ?? "?", targetActor, reason };

            PhotonNetwork.RaiseEvent(ReportEvents.EVT_SUBMIT_REPORT, data, _toMaster, _reliable);

            Log.LogInfo($"[ReportCard] Report envoyé → actor {targetActor}, raison: '{reason}'");
            _ui.ShowToast($"Report envoyé sur {GetPlayerName(targetActor)}.");
            _ui.CloseAll();
        }

        /// <summary>Bannit un joueur — host uniquement, appelé via le panneau host.</summary>
        public void BanPlayer(int targetActor)
        {
            if (!PhotonNetwork.IsMasterClient) return;
            if (_banned.Contains(targetActor))  { Log.LogInfo($"[ReportCard] Actor {targetActor} déjà banni."); return; }

            var target = PhotonNetwork.CurrentRoom?.GetPlayer(targetActor);
            if (target == null) { Log.LogError($"[ReportCard] Actor {targetActor} introuvable."); return; }

            _banned.Add(targetActor);
            string name = target.NickName ?? $"Actor#{targetActor}";

            Log.LogInfo($"[ReportCard] BAN de {name} (actor {targetActor}).");

            // 1) Notifier le joueur banni
            var toTarget = new RaiseEventOptions { TargetActors = new[] { targetActor } };
            PhotonNetwork.RaiseEvent(ReportEvents.EVT_NOTIFY_BANNED, null, toTarget, _reliable);

            // 2) Diffuser à tous les autres
            PhotonNetwork.RaiseEvent(ReportEvents.EVT_BROADCAST_BAN, name, _toOthers, _reliable);

            // 3) Fermer la connexion (méthode Photon officielle côté MasterClient)
            PhotonNetwork.CloseConnection(target);

            // 4) Nettoyage
            _reports.Remove(targetActor);
            _ui.CloseAll();
            _ui.ShowToast($"{name} a été banni.", 3f);
        }

        /// <summary>Ignore les reports sur un joueur — host uniquement.</summary>
        public void DismissReports(int targetActor)
        {
            if (!PhotonNetwork.IsMasterClient) return;
            string name = GetPlayerName(targetActor);
            _reports.Remove(targetActor);
            Log.LogInfo($"[ReportCard] Reports ignorés pour {name}.");
            _ui.CloseAll();
            _ui.ShowToast($"Reports sur {name} effacés.");
        }

        // =====================================================================
        //  IOnEventCallback — réception des events Photon
        // =====================================================================
        public void OnEvent(EventData photonEvent)
        {
            switch (photonEvent.Code)
            {
                case ReportEvents.EVT_SUBMIT_REPORT:
                    HandleSubmitReport(photonEvent);
                    break;

                case ReportEvents.EVT_NOTIFY_BANNED:
                    HandleNotifyBanned(photonEvent);
                    break;

                case ReportEvents.EVT_BROADCAST_BAN:
                    HandleBroadcastBan(photonEvent);
                    break;
            }
        }

        // ----- Handlers privés -----

        private void HandleSubmitReport(EventData e)
        {
            // Seul le MasterClient traite les reports
            if (!PhotonNetwork.IsMasterClient) return;

            var data          = (object[])e.CustomData;
            int reporterActor = (int)data[0];
            string reporterName = (string)data[1];
            int targetActor   = (int)data[2];
            string reason     = (string)data[3];

            // Anti-triche : le sender Photon doit être le reporter déclaré
            if (e.Sender != reporterActor)
            {
                Log.LogWarning($"[ReportCard] Report frauduleux ignoré (sender={e.Sender} ≠ reporter={reporterActor}).");
                return;
            }

            var target = PhotonNetwork.CurrentRoom?.GetPlayer(targetActor);
            string targetName = target?.NickName ?? $"Actor#{targetActor}";

            if (!_reports.ContainsKey(targetActor))
                _reports[targetActor] = new List<ReportEntry>();

            // Empêcher un même reporter de reporter plusieurs fois via l'event brut
            bool alreadySent = _reports[targetActor].Any(r => r.ReporterActorNumber == reporterActor);
            if (alreadySent)
            {
                Log.LogWarning($"[ReportCard] Report dupliqué ignoré (reporter={reporterActor}).");
                return;
            }

            _reports[targetActor].Add(new ReportEntry(reporterActor, reporterName, targetActor, targetName, reason));
            int count = _reports[targetActor].Count;

            Log.LogInfo($"[ReportCard] Report enregistré ({count}/{REPORTS_THRESHOLD}) contre {targetName} : '{reason}'");

            // Seuil atteint → ouvrir le panneau décision host
            if (count >= REPORTS_THRESHOLD)
            {
                Log.LogInfo($"[ReportCard] ⚠ Seuil atteint pour {targetName}.");
                _ui.ShowHostPanel(targetActor, targetName, _reports[targetActor]);
            }
        }

        private void HandleNotifyBanned(EventData e)
        {
            // Ce message m'est adressé personnellement : je suis en train d'être banni
            Log.LogInfo("[ReportCard] Tu as été banni par le host.");
            _ui.ShowToast("Tu as été banni de la partie par le host.", 8f);
        }

        private void HandleBroadcastBan(EventData e)
        {
            // Vérification : l'expéditeur doit être le MasterClient
            var sender = PhotonNetwork.CurrentRoom?.GetPlayer(e.Sender);
            if (sender == null || !sender.IsMasterClient) return;

            string bannedName = (string)e.CustomData;
            Log.LogInfo($"[ReportCard] {bannedName} a été banni.");
            _ui.ShowToast($"{bannedName} a été banni de la partie.", 3f);
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================
        public List<ReportEntry> GetReportsFor(int actor)
            => _reports.TryGetValue(actor, out var list) ? list : new List<ReportEntry>();

        public PhotonPlayer[] GetOtherPlayers()
        {
            var list = PhotonNetwork.PlayerListOthers;
            return (list != null && list.Length > 0) ? list : Array.Empty<PhotonPlayer>();
        }

        public bool AlreadyReported(int actor)
            => _alreadyReported.Contains(actor);

        public static string GetPlayerName(int actor)
            => PhotonNetwork.CurrentRoom?.GetPlayer(actor)?.NickName ?? $"Actor#{actor}";
    }

    // =========================================================================
    //  UI — construite entièrement en code (aucun prefab requis)
    // =========================================================================
    public class ReportCardUI : MonoBehaviour
    {
        internal ReportCardManager Manager;

        // Roots
        private GameObject _menuRoot;        // Liste des joueurs à reporter
        private GameObject _reasonRoot;      // Saisie de la raison
        private GameObject _hostRoot;        // Panneau décision host
        private GameObject _toastRoot;       // Toast de notification

        // Refs
        private TMP_InputField     _reasonInput;
        private TextMeshProUGUI    _toastText;
        private TextMeshProUGUI    _hostTitle;
        private Transform          _hostReportList;
        private Button             _hostBanBtn;
        private Button             _hostDismissBtn;
        private Transform          _playerListContent;

        private int    _pendingTargetActor;
        private string _pendingTargetName;
        private Coroutine _toastCoroutine;

        // Couleurs
        private static readonly Color C_BG      = new Color(0.07f, 0.07f, 0.09f, 0.96f);
        private static readonly Color C_ACCENT  = new Color(0.85f, 0.18f, 0.18f, 1.00f);
        private static readonly Color C_GREY    = new Color(0.20f, 0.20f, 0.23f, 1.00f);
        private static readonly Color C_GREEN   = new Color(0.12f, 0.52f, 0.18f, 1.00f);
        private static readonly Color C_TEXT    = new Color(0.92f, 0.92f, 0.92f, 1.00f);
        private static readonly Color C_DIM     = new Color(0.55f, 0.55f, 0.58f, 1.00f);

        private Canvas _canvas;

        // =====================================================================
        void Awake()
        {
            BuildCanvas();
            BuildReportMenu();
            BuildReasonPanel();
            BuildHostPanel();
            BuildToast();
        }

        // =====================================================================
        //  TOGGLE / OPEN / CLOSE
        // =====================================================================
        public void ToggleReportMenu()
        {
            if (_menuRoot.activeSelf) CloseAll();
            else OpenReportMenu();
        }

        public void OpenReportMenu()
        {
            CloseAll();
            RefreshPlayerList();
            _menuRoot.SetActive(true);
        }

        public void CloseAll()
        {
            _menuRoot.SetActive(false);
            _reasonRoot.SetActive(false);
            _hostRoot.SetActive(false);
        }

        // =====================================================================
        //  TOAST
        // =====================================================================
        public void ShowToast(string msg, float duration = 2.5f)
        {
            _toastText.text = msg;
            _toastRoot.SetActive(true);
            if (_toastCoroutine != null) StopCoroutine(_toastCoroutine);
            _toastCoroutine = StartCoroutine(CoHideToast(duration));
        }

        private IEnumerator CoHideToast(float t)
        {
            yield return new WaitForSeconds(t);
            _toastRoot.SetActive(false);
        }

        // =====================================================================
        //  REPORT MENU — liste des joueurs
        // =====================================================================
        private void RefreshPlayerList()
        {
            foreach (Transform c in _playerListContent) Destroy(c.gameObject);

            var others = Manager.GetOtherPlayers();
            if (others.Length == 0)
            {
                MakeTextLine(_playerListContent, "Aucun autre joueur dans la partie.", C_DIM);
                return;
            }

            foreach (var player in others)
            {
                int    actor     = player.ActorNumber;
                string name      = player.NickName ?? $"Joueur {actor}";
                bool   reported  = Manager.AlreadyReported(actor);
                string btnLabel  = reported ? $"{name}  ✓" : name;
                Color  btnColor  = reported ? C_GREY * 0.6f : C_GREY;

                var btn = MakeButton(_playerListContent, btnLabel, btnColor,
                    () => { if (!Manager.AlreadyReported(actor)) OpenReasonPanel(actor, name); });
                btn.GetComponent<Button>().interactable = !reported;
            }
        }

        private void OpenReasonPanel(int actor, string name)
        {
            _pendingTargetActor = actor;
            _pendingTargetName  = name;
            _menuRoot.SetActive(false);
            _reasonRoot.transform.Find("Label").GetComponent<TextMeshProUGUI>().text =
                $"Reporter  <b>{name}</b>\nDécris le comportement :";
            _reasonInput.text = "";
            _reasonRoot.SetActive(true);
            _reasonInput.ActivateInputField();
        }

        // =====================================================================
        //  HOST PANEL
        // =====================================================================
        public void ShowHostPanel(int targetActor, string targetName, List<ReportEntry> entries)
        {
            if (!PhotonNetwork.IsMasterClient) return;

            _hostTitle.text = $"⚠  <b>{entries.Count}</b> reports sur  <b>{targetName}</b>";

            // Vider et reconstruire la liste des raisons
            foreach (Transform c in _hostReportList) Destroy(c.gameObject);
            foreach (var r in entries)
                MakeTextLine(_hostReportList,
                    $"<color=#888888>{r.ReporterName}</color>  →  {r.Reason}", C_TEXT, 12);

            // Reconfigurer les boutons avec la bonne cible capturée
            int   actor = targetActor;
            string tName = targetName;

            _hostBanBtn.onClick.RemoveAllListeners();
            _hostBanBtn.onClick.AddListener(() => Manager.BanPlayer(actor));
            _hostBanBtn.GetComponentInChildren<TextMeshProUGUI>().text = $"🔨  Bannir {tName}";

            _hostDismissBtn.onClick.RemoveAllListeners();
            _hostDismissBtn.onClick.AddListener(() => Manager.DismissReports(actor));

            CloseAll();
            _hostRoot.SetActive(true);
        }

        // =====================================================================
        //  CONSTRUCTION UI
        // =====================================================================

        private void BuildCanvas()
        {
            var go = new GameObject("RC_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            DontDestroyOnLoad(go);
            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 300;

            var cs = go.GetComponent<CanvasScaler>();
            cs.uiScaleMode        = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            cs.referenceResolution = new Vector2(1920, 1080);
        }

        // ---- Report Menu (gauche, centré verticalement) ----
        private void BuildReportMenu()
        {
            _menuRoot = Panel("RC_Menu", new Vector2(360, 400), new Vector2(-680, 0));

            // En-tête
            MakeLabel(_menuRoot.transform, "🚩  Reporter un joueur", 16, C_ACCENT,
                      new Vector2(0, 160));
            MakeLabel(_menuRoot.transform, $"[F1] pour fermer", 10, C_DIM,
                      new Vector2(0, 140));

            // Scroll
            var scroll = MakeScrollView(_menuRoot.transform, new Vector2(330, 260), new Vector2(0, -10));
            _playerListContent = scroll.Find("Viewport/Content");

            // Bouton fermer (X)
            MakeSmallClose(_menuRoot.transform, new Vector2(155, 163), CloseAll);

            _menuRoot.SetActive(false);
        }

        // ---- Saisie de la raison ----
        private void BuildReasonPanel()
        {
            _reasonRoot = Panel("RC_Reason", new Vector2(400, 230), new Vector2(-680, 0));

            // Label dynamique
            var lbl = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            lbl.transform.SetParent(_reasonRoot.transform, false);
            var tmp = lbl.GetComponent<TextMeshProUGUI>();
            tmp.text      = "";
            tmp.fontSize  = 14;
            tmp.color     = C_TEXT;
            tmp.alignment = TextAlignmentOptions.Center;
            lbl.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 78);

            // Champ de saisie
            _reasonInput = MakeInputField(_reasonRoot.transform, new Vector2(360, 50),
                                          new Vector2(0, 14), "Décris le comportement...");

            // Boutons
            MakeButton(_reasonRoot.transform, "Envoyer", C_ACCENT,
                       () => Manager.SubmitReport(_pendingTargetActor, _reasonInput.text),
                       new Vector2(160, 36), new Vector2(72, -65));

            MakeButton(_reasonRoot.transform, "Annuler", C_GREY,
                       () => { _reasonRoot.SetActive(false); _menuRoot.SetActive(true); },
                       new Vector2(120, 36), new Vector2(-95, -65));

            _reasonRoot.SetActive(false);
        }

        // ---- Panneau host ----
        private void BuildHostPanel()
        {
            _hostRoot = Panel("RC_Host", new Vector2(440, 460), new Vector2(680, 0));

            // Titre dynamique
            var titleGo = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI));
            titleGo.transform.SetParent(_hostRoot.transform, false);
            _hostTitle = titleGo.GetComponent<TextMeshProUGUI>();
            _hostTitle.fontSize  = 15;
            _hostTitle.color     = C_ACCENT;
            _hostTitle.alignment = TextAlignmentOptions.Center;
            _hostTitle.textWrappingMode = TMPro.TextWrappingModes.Normal;
            titleGo.GetComponent<RectTransform>().sizeDelta      = new Vector2(400, 50);
            titleGo.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 185);

            // Séparateur
            MakeDivider(_hostRoot.transform, new Vector2(0, 158));

            // Zone scroll pour les raisons
            var scroll = MakeScrollView(_hostRoot.transform, new Vector2(410, 290), new Vector2(0, 10));
            _hostReportList = scroll.Find("Viewport/Content");

            // Bouton BAN
            var banGo = MakeButton(_hostRoot.transform, "🔨  Bannir", C_ACCENT,
                                   () => { }, new Vector2(185, 42), new Vector2(100, -183));
            _hostBanBtn = banGo.GetComponent<Button>();

            // Bouton IGNORER
            var dismissGo = MakeButton(_hostRoot.transform, "✓  Ignorer les reports", C_GREEN,
                                        () => { }, new Vector2(195, 42), new Vector2(-103, -183));
            _hostDismissBtn = dismissGo.GetComponent<Button>();

            // Bouton fermer
            MakeSmallClose(_hostRoot.transform, new Vector2(183, 193), CloseAll);

            _hostRoot.SetActive(false);
        }

        // ---- Toast ----
        private void BuildToast()
        {
            _toastRoot = new GameObject("RC_Toast",
                typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            _toastRoot.transform.SetParent(_canvas.transform, false);
            DontDestroyOnLoad(_toastRoot);

            var rt = _toastRoot.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0.5f, 0f);
            rt.anchorMax        = new Vector2(0.5f, 0f);
            rt.pivot            = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0, 90);
            rt.sizeDelta        = new Vector2(440, 46);

            _toastRoot.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.10f, 0.93f);

            var textGo = new GameObject("T", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(_toastRoot.transform, false);
            _toastText = textGo.GetComponent<TextMeshProUGUI>();
            _toastText.alignment = TextAlignmentOptions.Center;
            _toastText.color     = C_TEXT;
            _toastText.fontSize  = 13;
            FillParent(textGo.GetComponent<RectTransform>(), 10, 4);

            _toastRoot.SetActive(false);
        }

        // =====================================================================
        //  HELPERS DE CONSTRUCTION
        // =====================================================================

        private GameObject Panel(string name, Vector2 size, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_canvas.transform, false);
            DontDestroyOnLoad(go);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta        = size;
            rt.anchoredPosition = pos;
            go.GetComponent<Image>().color = C_BG;

            // Bordure subtile
            var outline = go.AddComponent<Outline>();
            outline.effectColor    = C_ACCENT * 0.35f;
            outline.effectDistance = new Vector2(1, -1);
            return go;
        }

        private void MakeLabel(Transform parent, string text, int size,
                                Color color, Vector2 pos)
        {
            var go = new GameObject("Lbl", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text      = text;
            tmp.fontSize  = size;
            tmp.color     = color;
            tmp.alignment = TextAlignmentOptions.Center;
            go.GetComponent<RectTransform>().anchoredPosition = pos;
        }

        private void MakeTextLine(Transform parent, string text,
                                   Color color, int size = 13)
        {
            var go = new GameObject("Line", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text             = text;
            tmp.fontSize         = size;
            tmp.color            = color;
            tmp.alignment        = TextAlignmentOptions.Left;
            tmp.textWrappingMode = TMPro.TextWrappingModes.Normal;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(380, 0);
        }

        private GameObject MakeButton(Transform parent, string label, Color bg,
                                       Action onClick, Vector2 size, Vector2 pos)
        {
            var go = new GameObject($"Btn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta        = size;
            rt.anchoredPosition = pos;

            go.GetComponent<Image>().color = bg;

            var btn = go.GetComponent<Button>();
            var cols = btn.colors;
            cols.normalColor      = bg;
            cols.highlightedColor = bg * 1.25f;
            cols.pressedColor     = bg * 0.65f;
            btn.colors = cols;
            btn.onClick.AddListener(() => onClick());

            var textGo = new GameObject("T", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(go.transform, false);
            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            tmp.text      = label;
            tmp.fontSize  = 13;
            tmp.color     = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            FillParent(textGo.GetComponent<RectTransform>(), 4, 2);

            return go;
        }

        // Surcharge avec parent/label/color/action uniquement (pour la player list)
        private GameObject MakeButton(Transform parent, string label, Color bg, Action onClick)
            => MakeButton(parent, label, bg, onClick, new Vector2(310, 38), Vector2.zero);

        private void MakeSmallClose(Transform parent, Vector2 pos, Action onClick)
        {
            var go = MakeButton(parent, "✕", C_GREY * 0.8f, onClick,
                                new Vector2(28, 28), pos);
            // (label géré par MakeButton)
        }

        private TMP_InputField MakeInputField(Transform parent, Vector2 size,
                                               Vector2 pos, string placeholder)
        {
            var go = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta        = size;
            rt.anchoredPosition = pos;
            go.GetComponent<Image>().color = new Color(0.13f, 0.13f, 0.16f, 1f);

            var field = go.GetComponent<TMP_InputField>();
            field.characterLimit = ReportCardManager.MAX_REASON_LENGTH;

            // Text area
            var ta = MakeTMPChild(go.transform, "TextArea", 13, C_TEXT);
            FillParent(ta.GetComponent<RectTransform>(), 8, 4);
            field.textComponent = ta;

            // Placeholder
            var ph = MakeTMPChild(go.transform, "Placeholder", 13, C_DIM);
            ph.text = placeholder;
            FillParent(ph.GetComponent<RectTransform>(), 8, 4);
            field.placeholder = ph;

            return field;
        }

        private Transform MakeScrollView(Transform parent, Vector2 size, Vector2 pos)
        {
            // Root scroll
            var root = new GameObject("Scroll",
                typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            root.transform.SetParent(parent, false);
            var rootRT = root.GetComponent<RectTransform>();
            rootRT.sizeDelta        = size;
            rootRT.anchoredPosition = pos;
            root.GetComponent<Image>().color = Color.clear;

            // Viewport
            var vp = new GameObject("Viewport",
                typeof(RectTransform), typeof(Image), typeof(Mask));
            vp.transform.SetParent(root.transform, false);
            var vpRT = vp.GetComponent<RectTransform>();
            vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
            vpRT.offsetMin = Vector2.zero; vpRT.offsetMax = Vector2.zero;
            vp.GetComponent<Image>().color = Color.clear;
            vp.GetComponent<Mask>().showMaskGraphic = false;

            // Content
            var content = new GameObject("Content",
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(vp.transform, false);
            var cRT = content.GetComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0, 1); cRT.anchorMax = new Vector2(1, 1);
            cRT.pivot     = new Vector2(0.5f, 1);
            cRT.offsetMin = Vector2.zero; cRT.offsetMax = Vector2.zero;

            var vlg = content.GetComponent<VerticalLayoutGroup>();
            vlg.childControlWidth  = true;
            vlg.childControlHeight = true;
            vlg.spacing            = 5;
            vlg.padding            = new RectOffset(6, 6, 6, 6);

            var csf = content.GetComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var sr = root.GetComponent<ScrollRect>();
            sr.viewport          = vpRT;
            sr.content           = cRT;
            sr.horizontal        = false;
            sr.vertical          = true;
            sr.scrollSensitivity = 25f;
            sr.movementType      = ScrollRect.MovementType.Clamped;

            return root.transform;
        }

        private void MakeDivider(Transform parent, Vector2 pos)
        {
            var go = new GameObject("Div", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = C_ACCENT * 0.3f;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta        = new Vector2(400, 1);
            rt.anchoredPosition = pos;
        }

        private static TextMeshProUGUI MakeTMPChild(Transform parent, string name,
                                                     int size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.fontSize  = size;
            tmp.color     = color;
            tmp.alignment = TextAlignmentOptions.Left;
            return tmp;
        }

        private static void FillParent(RectTransform rt, float hPad = 0, float vPad = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(hPad, vPad);
            rt.offsetMax = new Vector2(-hPad, -vPad);
        }
    }

    // =========================================================================
    //  PATCH HARMONY — injection du ReportCardManager dans la scène
    //  Se greffe sur PassportManager.Awake pour être cohérent avec le projet,
    //  mais est totalement indépendant de la logique de pagination.
    // =========================================================================
    [HarmonyPatch(typeof(PassportManager), "Awake")]
    static class ReportCardInjector
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (ReportCardManager.Instance != null) return;
            if (!PhotonNetwork.IsConnected)
            {
                Plugin.Log?.LogInfo("[ReportCard] Non connecté — injection ignorée.");
                return;
            }

            var go = new GameObject("ReportCardManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ReportCardManager>();
            Plugin.Log?.LogInfo("[ReportCard] ReportCardManager injecté dans la scène.");
        }
    }
}
