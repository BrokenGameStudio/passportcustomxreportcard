using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Zorro.Core;

namespace passportcustom
{
    // -------------------------------------------------------------------------
    //  API PUBLIQUE — les autres mods utilisent cette classe pour s'enregistrer
    // -------------------------------------------------------------------------
    public static class PassportPaginationAPI
    {
        // Dictionnaire : CustomizationType → liste d'items injectés par les mods
        private static readonly Dictionary<int, List<CustomizationOption>> _extraItems
            = new Dictionary<int, List<CustomizationOption>>();

        // Événement déclenché après chaque changement de page (pour que les mods
        // puissent réagir si besoin)
        public static event Action<int /*typeKey*/, int /*page*/, int /*maxPage*/> OnPageChanged;

        /// <summary>
        /// Enregistre des items supplémentaires pour un onglet du passport.
        /// Appeler depuis le Awake() de ton BepInEx plugin.
        /// </summary>
        /// <param name="typeKey">
        ///   La valeur int du CustomizationType (ou PassportTab) ciblé.
        ///   Ex : (int)PassportTab.Hat, ou un type custom enregistré ailleurs.
        /// </param>
        /// <param name="options">Les CustomizationOption à ajouter.</param>
        public static void RegisterExtraItems(int typeKey, IEnumerable<CustomizationOption> options)
        {
            if (options == null) return;
            if (!_extraItems.ContainsKey(typeKey))
                _extraItems[typeKey] = new List<CustomizationOption>();

            _extraItems[typeKey].AddRange(options);
            Plugin.Log?.LogInfo(
                $"[PassportPaginationAPI] Registered {_extraItems[typeKey].Count} extra item(s) for type key {typeKey}.");
        }

        /// <summary>
        /// Retire tous les items injectés pour ce typeKey.
        /// </summary>
        public static void UnregisterExtraItems(int typeKey)
        {
            if (_extraItems.Remove(typeKey))
                Plugin.Log?.LogInfo($"[PassportPaginationAPI] Unregistered extra items for type key {typeKey}.");
        }

        /// <summary>
        /// Retourne la liste fusionnée (native + extras) pour un typeKey donné.
        /// Utilisé en interne par le bridge ; accessible pour les mods avancés.
        /// </summary>
        public static CustomizationOption[] GetMergedList(int typeKey, CustomizationOption[] nativeList)
        {
            if (!_extraItems.TryGetValue(typeKey, out var extras) || extras.Count == 0)
                return nativeList;

            var merged = new CustomizationOption[nativeList.Length + extras.Count];
            nativeList.CopyTo(merged, 0);
            for (int i = 0; i < extras.Count; i++)
                merged[nativeList.Length + i] = extras[i];
            return merged;
        }

        /// <summary>
        /// Indique si des items extra existent pour ce typeKey.
        /// </summary>
        public static bool HasExtraItems(int typeKey)
            => _extraItems.ContainsKey(typeKey) && _extraItems[typeKey].Count > 0;

        // Accès interne pour le bridge
        internal static void RaisePageChanged(int typeKey, int page, int maxPage)
            => OnPageChanged?.Invoke(typeKey, page, maxPage);
    }

    // -------------------------------------------------------------------------
    //  PLUGIN PRINCIPAL
    // -------------------------------------------------------------------------
    [BepInPlugin("radsi.pagination", "passportcustom", "2.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        // Logger accessible par l'API et le Patcher
        internal static ManualLogSource Log;

        // État interne du bridge (statique pour être accessible depuis Patcher)
        internal static PassportManager CurrentPassportManager;
        internal static Texture2D ArrowImage;
        internal static int CurrentPage = 0;
        internal static GameObject ButtonRight, ButtonLeft;

        // Réflexion — on les résout une seule fois au démarrage
        private static MethodInfo _setActiveButtonMethod;
        private static FieldInfo  _buttonsPerPageField;

        void Awake()
        {
            Log = Logger;
            Log.LogInfo("[passportcustom] Initialisation du bridge de pagination...");

            // Charger la texture flèche
            ArrowImage = new Texture2D(2, 2);
            ImageConversion.LoadImage(ArrowImage, Resource1.arrow);

            // Résoudre les membres privés de PassportManager une seule fois
            var pmType = typeof(PassportManager);

            _setActiveButtonMethod = pmType.GetMethod(
                "SetActiveButton",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (_setActiveButtonMethod == null)
                Log.LogWarning("[passportcustom] SetActiveButton introuvable — la sélection active ne sera pas mise à jour.");

            _buttonsPerPageField = pmType.GetField(
                "buttonsPerPage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_buttonsPerPageField == null)
                Log.LogWarning("[passportcustom] buttonsPerPage introuvable — fallback sur buttons.Length.");

            var harmony = new Harmony("radsi.pagination");
            harmony.PatchAll(typeof(Patcher));
            harmony.PatchAll(typeof(ReportCardInjector));
            harmony.PatchAll(typeof(DevConsoleInjector));
            Log.LogInfo("[passportcustom] Harmony patches appliqués.");
        }

        // ------------------------------------------------------------------
        //  Helpers internes utilisés par Patcher
        // ------------------------------------------------------------------

        internal static int GetButtonsPerPage(PassportManager pm)
        {
            if (_buttonsPerPageField != null)
            {
                var val = _buttonsPerPageField.GetValue(pm);
                if (val is int i && i > 0) return i;
            }
            // Fallback : taille du tableau de boutons
            return pm.buttons?.Length ?? 28;
        }

        internal static void InvokeSetActiveButton(PassportManager pm)
            => _setActiveButtonMethod?.Invoke(pm, null);

        // ------------------------------------------------------------------
        //  Trouver le bouton modèle de façon robuste (plusieurs stratégies)
        // ------------------------------------------------------------------

        internal static GameObject FindTemplateButton()
        {
            // Stratégie 1 — chemin complet (ancienne version)
            const string fullPath =
                "GAME/PassportManager/PassportUI/Canvas/Panel/Panel/BG/Options/Grid/UI_PassportGridButton";
            var go = GameObject.Find(fullPath);
            if (go != null)
            {
                Log.LogInfo($"[passportcustom] Bouton template trouvé via path complet.");
                return go;
            }

            // Stratégie 2 — recherche dans tous les PassportButton actifs
            var all = UnityEngine.Object.FindObjectsOfType<PassportButton>();
            if (all != null && all.Length > 0)
            {
                Log.LogInfo($"[passportcustom] Bouton template trouvé via FindObjectsOfType<PassportButton> ({all.Length} bouton(s)).");
                return all[0].gameObject;
            }

            // Stratégie 3 — chercher un enfant "Grid" sous le PassportManager
            if (CurrentPassportManager != null)
            {
                var grid = FindChildRecursive(CurrentPassportManager.transform, "Grid");
                if (grid != null && grid.childCount > 0)
                {
                    Log.LogInfo($"[passportcustom] Bouton template trouvé via Grid child.");
                    return grid.GetChild(0).gameObject;
                }
            }

            Log.LogError(
                "[passportcustom] IMPOSSIBLE de trouver le bouton template. " +
                "Le nom de l'objet a probablement changé dans cette version de PEAK. " +
                "Les boutons de navigation ne seront pas créés.");
            return null;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name.Contains(name)) return child;
                var found = FindChildRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // =====================================================================
        //  PATCHER — tous les patches Harmony
        // =====================================================================
        private class Patcher
        {
            // -----------------------------------------------------------------
            //  Awake — création des boutons de navigation
            // -----------------------------------------------------------------
            [HarmonyPatch(typeof(PassportManager), "Awake")]
            [HarmonyPostfix]
            static void OnPassportManagerAwake(PassportManager __instance)
            {
                CurrentPassportManager = __instance;
                Log.LogInfo($"[passportcustom] PassportManager.Awake — instance capturée.");

                var template = FindTemplateButton();
                if (template == null) return;

                var parent = template.transform.parent?.parent?.parent;
                if (parent == null)
                {
                    Log.LogError("[passportcustom] Impossible de remonter au parent des boutons (parent.parent.parent == null).");
                    return;
                }

                // Nettoyer d'éventuels boutons orphelins d'une session précédente
                DestroyModButtons();

                ButtonRight = UnityEngine.Object.Instantiate(template);
                ButtonLeft  = UnityEngine.Object.Instantiate(template);

                ButtonRight.transform.SetParent(parent, false);
                ButtonLeft.transform.SetParent(parent, false);

                ButtonRight.transform.localScale = new Vector3( 1f, 0.4f, 1f);
                ButtonLeft.transform.localScale  = new Vector3(-1f, 0.4f, 1f);

                // Appliquer la texture flèche (robuste : on teste les deux chemins d'enfant connus)
                TrySetArrowTexture(ButtonRight);
                TrySetArrowTexture(ButtonLeft);

                ButtonRight.transform.localPosition = new Vector3( 276.6f, -196.6f, 0f);
                ButtonLeft.transform.localPosition  = new Vector3( -56.6f, -196.6f, 0f);

                // Supprimer le composant PassportButton pour éviter les conflits
                var pbR = ButtonRight.GetComponent<PassportButton>();
                var pbL = ButtonLeft.GetComponent<PassportButton>();
                if (pbR != null) UnityEngine.Object.Destroy(pbR);
                if (pbL != null) UnityEngine.Object.Destroy(pbL);

                ButtonRight.name = "passportcustom_right";
                ButtonLeft.name  = "passportcustom_left";

                ButtonRight.SetActive(false);
                ButtonLeft.SetActive(false);

                // Brancher les clics
                SetButtonAction(ButtonRight, "right_mod", () =>
                {
                    int max = GetMaxPage(CurrentPassportManager);
                    CurrentPage = Mathf.Min(CurrentPage + 1, max);
                    Log.LogInfo($"[passportcustom] → Page {CurrentPage}/{max}");
                    UpdatePage(CurrentPassportManager);
                });

                SetButtonAction(ButtonLeft, "left_mod", () =>
                {
                    int max = GetMaxPage(CurrentPassportManager);
                    CurrentPage = Mathf.Max(CurrentPage - 1, 0);
                    Log.LogInfo($"[passportcustom] ← Page {CurrentPage}/{max}");
                    UpdatePage(CurrentPassportManager);
                });

                Log.LogInfo("[passportcustom] Boutons de navigation créés avec succès.");
            }

            // -----------------------------------------------------------------
            //  OpenTab — reset à la page 0 à chaque changement d'onglet
            //  On accepte tous les overloads possibles via HarmonyArgument
            // -----------------------------------------------------------------
            [HarmonyPatch(typeof(PassportManager), "OpenTab")]
            [HarmonyPostfix]
            static void OnOpenTab(PassportManager __instance)
            {
                CurrentPassportManager = __instance; // refresh au cas où
                CurrentPage = 0;
                Log.LogInfo($"[passportcustom] OpenTab — reset page 0 (type={__instance.activeType})");
                UpdatePage(__instance);
            }

            // -----------------------------------------------------------------
            //  SetButtons — CŒUR DU BRIDGE
            //  Intercepte l'appel natif pour injecter la pagination + les items extra
            // -----------------------------------------------------------------
            [HarmonyPatch(typeof(PassportManager), "SetButtons")]
            [HarmonyPrefix]
            static bool OnSetButtonsPrefix(PassportManager __instance)
            {
                // Si on est page 0 ET qu'il n'y a pas d'items extra, on laisse le jeu faire
                int typeKey = Convert.ToInt32(__instance.activeType);
                bool hasExtras = PassportPaginationAPI.HasExtraItems(typeKey);

                if (CurrentPage == 0 && !hasExtras)
                {
                    Log.LogDebug("[passportcustom] SetButtons — page 0, pas d'extras, passage au natif.");
                    // On laisse le natif tourner MAIS on met à jour les boutons nav après
                    return true; // exécuter le SetButtons natif
                }

                // Sinon : on prend la main complète
                Log.LogDebug($"[passportcustom] SetButtons — bridge actif (page={CurrentPage}, extras={hasExtras})");
                UpdatePage(__instance);
                return false; // bloquer le SetButtons natif
            }

            [HarmonyPatch(typeof(PassportManager), "SetButtons")]
            [HarmonyPostfix]
            static void OnSetButtonsPostfix(PassportManager __instance)
            {
                // Postfix : même si le natif a tourné (page 0 sans extras),
                // on met quand même à jour la visibilité des boutons nav
                RefreshNavButtons(__instance);
            }
        }

        // =====================================================================
        //  LOGIQUE DE PAGINATION
        // =====================================================================

        internal static void UpdatePage(PassportManager pm)
        {
            if (pm == null)
            {
                Log.LogError("[passportcustom] UpdatePage appelé avec pm == null !");
                return;
            }

            int typeKey     = Convert.ToInt32(pm.activeType);
            int perPage     = GetButtonsPerPage(pm);
            var nativeList  = Singleton<Customization>.Instance.GetList(pm.activeType);
            var merged      = PassportPaginationAPI.GetMergedList(typeKey, nativeList);

            int total       = merged.Length;
            int start       = CurrentPage * perPage;
            int maxPage     = GetMaxPage(pm, total, perPage);

            Log.LogDebug($"[passportcustom] UpdatePage — type={pm.activeType} page={CurrentPage}/{maxPage} " +
                         $"total={total} perPage={perPage} nativeItems={nativeList.Length} mergedItems={total}");

            for (int i = 0; i < pm.buttons.Length; i++)
            {
                int index = start + i;
                if (index < total)
                    pm.buttons[i].SetButton(merged[index], index);
                else
                    pm.buttons[i].SetButton(null, -1);
            }

            InvokeSetActiveButton(pm);
            RefreshNavButtons(pm, maxPage);

            PassportPaginationAPI.RaisePageChanged(typeKey, CurrentPage, maxPage);
        }

        internal static void RefreshNavButtons(PassportManager pm, int maxPage = -1)
        {
            if (ButtonLeft == null || ButtonRight == null) return;

            if (maxPage < 0) maxPage = GetMaxPage(pm);

            ButtonLeft.SetActive(CurrentPage > 0);
            ButtonRight.SetActive(CurrentPage < maxPage);
        }

        internal static int GetMaxPage(PassportManager pm)
        {
            int typeKey    = Convert.ToInt32(pm.activeType);
            var nativeList = Singleton<Customization>.Instance.GetList(pm.activeType);
            var merged     = PassportPaginationAPI.GetMergedList(typeKey, nativeList);
            return GetMaxPage(pm, merged.Length, GetButtonsPerPage(pm));
        }

        private static int GetMaxPage(PassportManager pm, int total, int perPage)
            => Mathf.Max((total - 1) / perPage, 0);

        // =====================================================================
        //  HELPERS UI
        // =====================================================================

        private static void TrySetArrowTexture(GameObject btn)
        {
            // Chemin connu: child(1) → child(0) → RawImage
            try
            {
                var ri = btn.transform.GetChild(1).GetChild(0).GetComponent<RawImage>();
                if (ri != null) { ri.texture = ArrowImage; return; }
            }
            catch { /* ignore */ }

            // Fallback: chercher le premier RawImage dans les enfants
            var rawImg = btn.GetComponentInChildren<RawImage>(true);
            if (rawImg != null)
                rawImg.texture = ArrowImage;
            else
                Log.LogWarning("[passportcustom] Impossible de trouver le RawImage sur le bouton cloné.");
        }

        private static void SetButtonAction(GameObject btn, string name, Action onClick)
        {
            btn.name = name;
            var uiButton = btn.GetComponent<Button>();
            if (uiButton == null)
            {
                Log.LogError($"[passportcustom] Le bouton '{name}' n'a pas de composant Button !");
                return;
            }
            uiButton.onClick.RemoveAllListeners();
            uiButton.onClick.AddListener(() => onClick());
        }

        private static void DestroyModButtons()
        {
            var old = new[] { "passportcustom_right", "passportcustom_left", "right_mod", "left_mod" };
            foreach (var n in old)
            {
                var go = GameObject.Find(n);
                if (go != null)
                {
                    Log.LogInfo($"[passportcustom] Nettoyage bouton orphelin '{n}'.");
                    UnityEngine.Object.Destroy(go);
                }
            }
            ButtonLeft = ButtonRight = null;
        }
    }
}
