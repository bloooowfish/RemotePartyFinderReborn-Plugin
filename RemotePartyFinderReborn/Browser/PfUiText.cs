#nullable enable

using System;

namespace RemotePartyFinderReborn;

internal enum PfUiLanguage {
    English = 0,
    Japanese = 1,
    German = 2,
    French = 3,
}

internal static class PfUiText {
    private static readonly string[] SortLabelsEn = [
        "Native Order",
        "Time Remaining",
        "Slots Filled",
        "Min Item Level",
    ];

    private static readonly string[] SortLabelsJa = [
        "ゲーム標準",
        "残り時間",
        "参加人数",
        "平均IL",
    ];

    private static readonly string[] SortLabelsDe = [
        "Standard",
        "Restzeit",
        "Plätze belegt",
        "Min. Gegenstandsstufe",
    ];

    private static readonly string[] SortLabelsFr = [
        "Ordre natif",
        "Temps restant",
        "Places remplies",
        "NIV obj. min.",
    ];

    private static readonly string[] BlockedLabelsEn = [
        "Blocked: Dim",
        "Blocked: Hide",
    ];

    private static readonly string[] BlockedLabelsJa = [
        "BL: 暗く表示",
        "BL: 非表示",
    ];

    private static readonly string[] BlockedLabelsDe = [
        "Blockiert: Abdunkeln",
        "Blockiert: Ausblenden",
    ];

    private static readonly string[] BlockedLabelsFr = [
        "Bloqués : Atténuer",
        "Bloqués : Masquer",
    ];

    internal static string Pick(PfUiLanguage lang, string en, string ja, string de, string fr) => lang switch {
        PfUiLanguage.Japanese => ja,
        PfUiLanguage.German => de,
        PfUiLanguage.French => fr,
        _ => en,
    };

    internal static string BrowserWindowTitle(PfUiLanguage lang) => Pick(
        lang,
        "Party Finder (RFP Reborn)##PfBrowserWindow",
        "パーティ募集 (RFP Reborn)##PfBrowserWindow",
        "Gruppensuche (RFP Reborn)##PfBrowserWindow",
        "Recherche d'équipe (RFP Reborn)##PfBrowserWindow");

    internal static string DetailWindowTitle(PfUiLanguage lang) => Pick(
        lang,
        "Party Finder Detail (RFP Reborn)##PfDetailWindow",
        "パーティ募集詳細 (RFP Reborn)##PfDetailWindow",
        "Gruppensuche-Details (RFP Reborn)##PfDetailWindow",
        "Détails de recherche d'équipe (RFP Reborn)##PfDetailWindow");

    internal static string NoneSpecified(PfUiLanguage lang) => Pick(
        lang,
        "None Specified",
        "設定なし",
        "Keine Angabe",
        "Non spécifié");

    internal static string FormatRemainingTime(ushort secondsRemaining, PfUiLanguage lang) {
        if (secondsRemaining <= 0) {
            return Pick(lang, "<1m", "<1分", "<1m", "<1m");
        }

        var minutes = (secondsRemaining + 59) / 60;
        if (minutes >= 60) {
            return lang == PfUiLanguage.Japanese
                ? $"{minutes / 60}h{minutes % 60:00}m"
                : $"{minutes / 60}h{minutes % 60:00}m";
        }

        return lang == PfUiLanguage.Japanese ? $"{minutes}分" : $"{minutes}m";
    }

    internal static string AreaTabLabel(PfUiLanguage lang, PfSearchAreaTab tab) => tab switch {
        PfSearchAreaTab.World => Pick(lang, "World", "ワールド内", "Welt", "Monde"),
        PfSearchAreaTab.Private => Pick(lang, "Private", "プライベート", "Privat", "Privé"),
        _ => Pick(lang, "All", "すべて", "Alle", "Tous"),
    };

    internal static string AreaTabTooltip(PfUiLanguage lang, string label, int serverCount, int localCount) => Pick(
        lang,
        $"{label} — Server: {serverCount} | Page: {localCount}",
        $"{label} — サーバー合計: {serverCount} | 現ページ: {localCount}",
        $"{label} — Server: {serverCount} | Seite: {localCount}",
        $"{label} — Serveur : {serverCount} | Page : {localCount}");

    internal static string MyJobLabel(PfUiLanguage lang, string? jobAbbr) {
        var baseLabel = Pick(lang, "My Job", "現ジョブ可", "Mein Job", "Mon job");
        return string.IsNullOrWhiteSpace(jobAbbr) ? baseLabel : $"{baseLabel} ({jobAbbr})";
    }

    internal static string MyJobTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Show only parties with an open slot matching your current job.",
        "現在のジョブで参加できる空き枠がある募集のみ表示します。",
        "Nur Gruppen mit offenem Platz für deinen aktuellen Job anzeigen.",
        "Afficher uniquement les équipes ayant une place libre pour votre job actuel.");

    internal static string HighEndCheckbox(PfUiLanguage lang) => Pick(lang, "High-End", "高難易度", "Schwere", "Extrême+");
    internal static string SamplesCheckbox(PfUiLanguage lang) => Pick(lang, "Samples", "サンプル", "Beispiele", "Exemples");

    internal static string SamplesTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Include sample PF listings for previewing layout without opening the retail PF.",
        "ゲーム内募集とは別にレイアウト確認用のサンプル募集を含めます。",
        "Beispiel-Einträge zur Layout-Vorschau einbeziehen.",
        "Inclure des annonces d'exemple pour prévisualiser l'interface.");

    internal static string SampleStatsCheckbox(PfUiLanguage lang) => Pick(lang, "Sample Stats", "統計プレビュー", "Beispiel-Stats", "Stats d'exemple");

    internal static string SampleStatsTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Preview FFLogs & Tomestone badges using deterministic sample enrichment states.",
        "FFLogs・Tomestoneバッジの表示例をプレビューします。",
        "FFLogs- und Tomestone-Abzeichen mit Beispielwerten vorschauen.",
        "Prévisualiser les badges FFLogs et Tomestone avec des données d'exemple.");

    internal static string CategoryAllShort(PfUiLanguage lang) => Pick(lang, "ALL", "すべて", "ALLE", "TOUS");
    internal static string CategoryOtherShort(PfUiLanguage lang) => Pick(lang, "OTH", "その他", "SONST", "AUTRE");

    internal static string CategoryLabel(PfUiLanguage lang, string tabId, string fallbackLabel) => tabId switch {
        "all" => Pick(lang, "All", "すべて", "Alle", "Tous"),
        "roulette" => Pick(lang, "Duty Roulette", "コンテンツルーレット", "Zufallsinhalte", "Missions aléatoires"),
        "dungeons" => Pick(lang, "Dungeons", "ダンジョン", "Dungeons", "Donjons"),
        "guildhests" => Pick(lang, "Guildhests", "ギルドオーダー", "Gildengeheiße", "Opérations de guilde"),
        "trials" => Pick(lang, "Trials", "討伐・討滅戦", "Prüfungen", "Défis"),
        "raids" => Pick(lang, "Raids", "レイド", "Raids", "Raids"),
        "highend" => Pick(lang, "High-end Duty", "高難易度コンテンツ", "Schwere Inhalte", "Missions à haute difficulté"),
        "pvp" => Pick(lang, "PvP", "PvP", "PvP", "JcJ"),
        "goldsaucer" => Pick(lang, "Gold Saucer", "ゴールドソーサー", "Gold Saucer", "Gold Saucer"),
        "fates" => Pick(lang, "FATEs", "F.A.T.E.", "FATEs", "ALÉA"),
        "treasure" => Pick(lang, "Treasure Hunt", "トレジャーハント", "Schatzjagd", "Chasse aux trésors"),
        "hunt" => Pick(lang, "The Hunt", "モブハント", "Hohe Jagd", "Chasse"),
        "gathering" => Pick(lang, "Gathering Forays", "採集活動", "Sammelausflüge", "Récolte"),
        "deep" => Pick(lang, "Deep Dungeons", "ディープダンジョン", "Tiefe Gewölbe", "Donjons sans fond"),
        "field" => Pick(lang, "Field Operations", "特殊フィールド探索", "Feldoperationen", "Opérations sur le terrain"),
        "vc" => Pick(lang, "V&C Dungeon", "ヴァリアント&アナザーダンジョン", "Gewölbeerkundung", "Donjons spéciaux"),
        "other" => Pick(lang, "Other", "その他", "Sonstiges", "Autres"),
        _ => fallbackLabel,
    };

    internal static string CategoryTooltip(PfUiLanguage lang, string label, int serverCount, int localCount, bool useServerCounts) {
        if (!useServerCounts) {
            return $"{label} ({localCount})";
        }

        return Pick(
            lang,
            $"{label} — Server: {serverCount} | Page: {localCount}",
            $"{label} — サーバー合計: {serverCount} | 現ページ: {localCount}",
            $"{label} — Server: {serverCount} | Seite: {localCount}",
            $"{label} — Serveur : {serverCount} | Page : {localCount}");
    }

    internal static string DetailedSearchTopFallback(PfUiLanguage lang) => Pick(lang, "FLT", "検索", "FLT", "FLT");
    internal static string DetailedSearchBottomLabel(PfUiLanguage lang, bool active) => active
        ? Pick(lang, "ON", "有効", "AN", "ACT")
        : Pick(lang, "FLT", "詳細", "FLT", "FLT");

    internal static string DetailedSearchTooltip(PfUiLanguage lang, bool active) => active
        ? Pick(
            lang,
            "Native Detailed Search (Active)\nClick to edit search conditions.",
            "詳細検索 (適用中)\nクリックして検索条件を変更します。",
            "Detailsuche (Aktiv)\nKlicken zum Bearbeiten der Suchkriterien.",
            "Recherche détaillée (Active)\nCliquez pour modifier les critères.")
        : Pick(
            lang,
            "Open native PF Detailed Search dialog.",
            "ゲーム内の詳細検索ウィンドウを開きます。",
            "Detailsuche der Gruppensuche öffnen.",
            "Ouvrir la recherche détaillée native.");

    internal static string DetailedSearchResetBottomLabel(PfUiLanguage lang) => Pick(lang, "CLR", "解除", "AUS", "RAZ");

    internal static string DetailedSearchResetTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Clear Detailed Search and return to All listings.",
        "詳細検索を解除して全募集一覧に戻ります。",
        "Detailsuche zurücksetzen und zu allen Einträgen zurückkehren.",
        "Réinitialiser la recherche détaillée.");

    internal static string SearchInputHint(PfUiLanguage lang) => Pick(
        lang,
        "Search (space=AND, -exclude)...",
        "検索 (スペース=AND, -除外)...",
        "Suche (Leer=UND, -Ausschließen)...",
        "Recherche (espace=ET, -exclure)...");

    internal static string SearchInputTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Search duty, comment, leader, world, or DC.\n- Space separates multiple required keywords (AND)\n- Prefix with - to exclude a keyword (e.g. practice -cleared)\n- Fullwidth/halfwidth and Hiragana/Katakana are matched interchangeably.",
        "コンテンツ名・コメント・募集主・ワールド・DCを検索します。\n・スペース区切りでAND検索（すべて含む）\n・先頭に - を付けて除外検索（例: りりど -済）\n・全角/半角、ひらがな/カタカナは区別せず一致します。",
        "Inhalt, Kommentar, Leiter, Welt oder DC durchsuchen.\n- Leerzeichen für UND-Suche\n- '-' voranstellen zum Ausschließen\n- Voll-/Halbbreite und Kana werden vereinheitlicht.",
        "Rechercher mission, commentaire, chef, monde ou DC.\n- Espace pour recherche ET\n- Préfixe - pour exclure un mot-clé\n- Pleine/demi-chasse et Kana unifiés.");

    internal static string DcComboPreview(PfUiLanguage lang, string selectedDc) => string.IsNullOrEmpty(selectedDc)
        ? Pick(lang, "DC: All", "DC: すべて", "DC: Alle", "DC : Tous")
        : $"DC: {selectedDc}";

    internal static string DcComboAllOption(PfUiLanguage lang) => Pick(
        lang,
        "All Data Centers",
        "すべてのデータセンター",
        "Alle Datenzentren",
        "Tous les centres de données");

    internal static string DcComboTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Optional filter by Data Center (All shows region-wide listings).",
        "データセンターで絞り込みます（すべてでリージョン全体表示）。",
        "Optional nach Datenzentrum filtern (Alle zeigt gesamte Region).",
        "Filtrer par centre de données (Tous affiche toute la région).");

    internal static string[] GetSortLabels(PfUiLanguage lang) => lang switch {
        PfUiLanguage.Japanese => SortLabelsJa,
        PfUiLanguage.German => SortLabelsDe,
        PfUiLanguage.French => SortLabelsFr,
        _ => SortLabelsEn,
    };

    internal static string SortTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Native Order mirrors FFXIV's Party Finder sorting (category, duty order, and eligibility). Other options sort by remaining time, slots filled, or min item level.",
        "ゲーム標準はFF14のパーティ募集と同じ並び順（未開放を下・カテゴリ順・コンテンツ順）です。残り時間・参加人数・平均IL順にも切り替えられます。",
        "Standard entspricht der FFXIV-Sortierung. Weitere Optionen sortieren nach Restzeit, belegten Plätzen oder Gegenstandsstufe.",
        "Ordre natif suit le tri FFXIV. Autres options : temps restant, places ou NIV obj. min.");

    internal static string[] GetBlockedLabels(PfUiLanguage lang) => lang switch {
        PfUiLanguage.Japanese => BlockedLabelsJa,
        PfUiLanguage.German => BlockedLabelsDe,
        PfUiLanguage.French => BlockedLabelsFr,
        _ => BlockedLabelsEn,
    };

    internal static string BlockedDisplayTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Choose whether party listings from blacklisted players are dimmed at the bottom or hidden.",
        "ブラックリスト登録プレイヤーの募集を末尾に暗く表示するか、完全に非表示にするかを選びます。",
        "Einträge blockierter Spieler unten abdunkeln oder komplett ausblenden.",
        "Atténuer en bas ou masquer les annonces des joueurs bloqués.");

    internal static string ViewModeButton(PfUiLanguage lang, bool isCompact) => isCompact
        ? Pick(lang, "Compact##PfMode", "コンパクト##PfMode", "Kompakt##PfMode", "Compact##PfMode")
        : Pick(lang, "Standard##PfMode", "スタンダード##PfMode", "Standard##PfMode", "Standard##PfMode");

    internal static string ViewModeTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Toggle between 2-line Standard (48px) and 1-line Compact (24px) density.",
        "2行表示（スタンダード）と1行表示（コンパクト）を切り替えます。",
        "Zwischen 2-zeiliger Standard- und 1-zeiliger Kompaktansicht wechseln.",
        "Basculer entre l'affichage Standard (2 lignes) et Compact (1 ligne).");

    internal static string InitializingMessage(PfUiLanguage lang) => Pick(
        lang,
        "Initializing Party Finder...",
        "パーティ募集データを読み込み中...",
        "Gruppensuche wird initialisiert...",
        "Initialisation de la recherche d'équipe...");

    internal static string NotInitializedMessage(PfUiLanguage lang) => Pick(
        lang,
        "Native Party Finder is not initialized for this login session yet.",
        "このセッションではまだパーティ募集が初期化されていません。",
        "Die native Gruppensuche wurde in dieser Sitzung noch nicht initialisiert.",
        "La recherche d'équipe native n'est pas encore initialisée pour cette session.");

    internal static string OpenNativePfToInitButton(PfUiLanguage lang) => Pick(
        lang,
        "Open Native PF to Initialize##InitNativePf",
        "ゲーム内パーティ募集を開いて初期化##InitNativePf",
        "Native Gruppensuche zum Initialisieren öffnen##InitNativePf",
        "Ouvrir la recherche native pour initialiser##InitNativePf");

    internal static string NoMatchingListings(PfUiLanguage lang) => Pick(
        lang,
        "No matching listings. Click Refresh or adjust filters.",
        "条件に一致する募集がありません。更新するかフィルターを調整してください。",
        "Keine passenden Einträge. Aktualisieren oder Filter anpassen.",
        "Aucune annonce correspondante. Actualisez ou ajustez les filtres.");

    internal static string ObjectiveCompShort(PfUiLanguage lang) => Pick(lang, "Comp", "コンプ目的", "Abschluss", "Objectif");
    internal static string ObjectivePracShort(PfUiLanguage lang) => Pick(lang, "Prac", "練習", "Übung", "Entraîn.");
    internal static string ObjectiveLootShort(PfUiLanguage lang) => Pick(lang, "Loot", "周回", "Beute", "Butin");
    internal static string ConditionClearedShort(PfUiLanguage lang) => Pick(lang, "Cleared", "コンプ済", "Abgeschl.", "Accomplie");
    internal static string ConditionUnclearShort(PfUiLanguage lang) => Pick(lang, "Unclear", "未コンプ", "Unabgeschl.", "Incomplète");
    internal static string SampleTag(PfUiLanguage lang) => Pick(lang, "[Sample] ", "[サンプル] ", "[Beispiel] ", "[Exemple] ");
    internal static string NoComment(PfUiLanguage lang) => Pick(lang, "(No comment)", "(コメントなし)", "(Kein Kommentar)", "(Aucun commentaire)");
    internal static string UnknownLeader(PfUiLanguage lang) => Pick(lang, "(Unknown Leader)", "(不明な募集主)", "(Unbekannter Leiter)", "(Chef inconnu)");

    internal static string MinIlTooltip(PfUiLanguage lang, ushort minIl) => minIl > 0
        ? Pick(lang, $"Minimum Item Level: {minIl}", $"必要平均アイテムレベル: {minIl}", $"Min. Gegenstandsstufe: {minIl}", $"NIV objet min. : {minIl}")
        : Pick(lang, "Minimum Item Level: None", "必要平均アイテムレベル: 設定なし", "Min. Gegenstandsstufe: Keine", "NIV objet min. : Aucun");

    internal static string PrivateStatusTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Private listing (password required).",
        "プライベート募集（パスワードが必要）",
        "Privater Eintrag (Passwort erforderlich).",
        "Annonce privée (mot de passe requis).");

    internal static string UnopenedStatusTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Duty not unlocked yet — you cannot join this listing.",
        "未開放のコンテンツです — この募集には参加できません。",
        "Inhalt noch nicht freigeschaltet — Teilnahme nicht möglich.",
        "Mission non débloquée — vous ne pouvez pas rejoindre.");

    internal static string DutyCompleteStatusTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Duty Complete requirement.",
        "参加条件: コンプリート済み",
        "Voraussetzung: Inhalt abgeschlossen.",
        "Condition : Mission accomplie.");

    internal static string BlockedBadgeLabel(PfUiLanguage lang) => Pick(lang, "Blocked", "BL", "Blockiert", "Bloqué");
    internal static string UnopenedBadgeLabel(PfUiLanguage lang) => Pick(lang, "Locked", "未開放", "Gesperrt", "Verrouillé");
    internal static string UnopenedBadgeTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Duty not unlocked yet.",
        "未開放のコンテンツです。",
        "Inhalt noch nicht freigeschaltet.",
        "Mission non débloquée.");

    internal static string HiddenBadge(PfUiLanguage lang) => Pick(lang, "Hidden", "非公開", "Versteckt", "Masqué");
    internal static string ClearedBadge(PfUiLanguage lang) => Pick(lang, "Cleared", "済", "Abgeschl.", "Accomplie");

    internal static string ParseUnlockedTooltip(PfUiLanguage lang) => Pick(
        lang,
        "FFLogs percentile is not locked in yet (Today's %; may shift after historical lock-in).",
        "FFLogs統計がまだ確定(Lock In)されていません（Today's %・後日変動する可能性があります）。",
        "FFLogs-Wert noch nicht fixiert (Today's %; kann sich noch ändern).",
        "Percentile FFLogs non verrouillé (Today's %; susceptible d'évoluer).");

    internal static string ParseEstimatedTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Estimated from candidate servers / name match.",
        "候補ワールド・同名キャラクターから推定した値です。",
        "Geschätzt über mögliche Welten / Namensabgleich.",
        "Estimé via correspondance de monde / nom.");

    internal static string ContextOpenLeaderFflogs(PfUiLanguage lang) => Pick(lang, "Open Leader on FFLogs", "募集主のFFLogsを開く", "Leiter auf FFLogs öffnen", "Ouvrir le chef sur FFLogs");
    internal static string ContextOpenLeaderTomestone(PfUiLanguage lang) => Pick(lang, "Open Leader on Tomestone", "募集主のTomestoneを開く", "Leiter auf Tomestone öffnen", "Ouvrir le chef sur Tomestone");
    internal static string ContextCopyLeader(PfUiLanguage lang) => Pick(lang, "Copy Leader Name@World", "募集主名@ワールドをコピー", "Leiter Name@Welt kopieren", "Copier Nom@Monde du chef");
    internal static string ContextCopyComment(PfUiLanguage lang) => Pick(lang, "Copy Comment", "コメントをコピー", "Kommentar kopieren", "Copier le commentaire");
    internal static string ContextOpenNativeDetail(PfUiLanguage lang) => Pick(lang, "Open Native Detail / Join", "詳細ウィンドウを開く / 参加", "Natives Detail / Beitreten öffnen", "Ouvrir détails natifs / Rejoindre");

    internal static string RecruitButton(PfUiLanguage lang) => Pick(lang, "Recruit", "募集登録", "Rekrutieren", "Recruter");
    internal static string RecruitTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Open native PF Recruit Members dialog.",
        "ゲーム内のパーティ募集登録ウィンドウを開きます。",
        "Gruppensuche-Rekrutierung öffnen.",
        "Ouvrir la création d'annonce native.");

    internal static string FooterShowing(PfUiLanguage lang, int shownCount, int pageLiveCount) {
        var same = shownCount == pageLiveCount || pageLiveCount == 0;
        return lang switch {
            PfUiLanguage.Japanese => same ? $"表示 {shownCount}" : $"表示 {shownCount}/{pageLiveCount}",
            PfUiLanguage.German => same ? $"Angezeigt {shownCount}" : $"Angezeigt {shownCount}/{pageLiveCount}",
            PfUiLanguage.French => same ? $"Affiché {shownCount}" : $"Affiché {shownCount}/{pageLiveCount}",
            _ => same ? $"Showing {shownCount}" : $"Showing {shownCount}/{pageLiveCount}",
        };
    }

    internal static string PreviousPageTooltip(PfUiLanguage lang) => Pick(lang, "Previous PF page", "前のページへ", "Vorherige Seite", "Page précédente");
    internal static string NextPageTooltip(PfUiLanguage lang) => Pick(lang, "Next PF page", "次のページへ", "Nächste Seite", "Page suivante");
    internal static string PagePrefix(PfUiLanguage lang, int pageOneBased) => Pick(
        lang,
        $"Page {pageOneBased}",
        $"ページ {pageOneBased}",
        $"Seite {pageOneBased}",
        $"Page {pageOneBased}");

    internal static string StatusActive(PfUiLanguage lang) => Pick(lang, "Active", "稼働中", "Aktiv", "Actif");
    internal static string StatusPreview(PfUiLanguage lang) => Pick(lang, "Preview", "プレビュー", "Vorschau", "Aperçu");
    internal static string RefreshButtonText(PfUiLanguage lang) => Pick(lang, "Refresh", "更新", "Aktualisieren", "Actualiser");
    internal static string RefreshTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Request live PF list refresh via native AgentLookingForGroup.",
        "募集リストを再取得します。",
        "Gruppensuche-Liste aktualisieren.",
        "Actualiser la liste de recherche d'équipe.");

    internal static string HideNativePfButton(PfUiLanguage lang) => Pick(lang, "Hide Native PF", "純正PFを隠す", "Natives PF ausbl.", "Masquer PF natif");
    internal static string HideNativePfTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Hide FFXIV's native Party Finder window while keeping live PF data active.",
        "データ取得を維持したまま、ゲーム内パーティ募集ウィンドウを非表示にします。",
        "FFXIV-Gruppensuche ausblenden, Live-Daten bleiben aktiv.",
        "Masquer la fenêtre native tout en gardant les données actives.");

    internal static string ShowNativePfButton(PfUiLanguage lang) => Pick(lang, "Show Native PF", "純正PFを表示", "Natives PF zeigen", "Afficher PF natif");
    internal static string ShowNativePfTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Show FFXIV's native Party Finder window.",
        "ゲーム内パーティ募集ウィンドウを表示します。",
        "FFXIV-Gruppensuche anzeigen.",
        "Afficher la fenêtre native de recherche d'équipe.");

    // Detail Pane
    internal static string DetailEmptySelection(PfUiLanguage lang) => Pick(
        lang,
        "Select a listing from the PF list to inspect party members and logs.",
        "募集リストからパーティを選択するとメンバー構成とログを確認できます。",
        "Wähle einen Eintrag aus, um Mitglieder und Logs anzuzeigen.",
        "Sélectionnez une annonce pour voir les membres et les logs.");

    internal static string DetailListingExpired(PfUiLanguage lang, ulong listingId) => Pick(
        lang,
        $"Listing #{listingId} is no longer in the local mirror cache (expired or closed).",
        $"募集 #{listingId} は期限切れまたは終了したためキャッシュにありません。",
        $"Eintrag #{listingId} ist nicht mehr im Cache (abgelaufen oder geschlossen).",
        $"L'annonce #{listingId} n'est plus dans le cache (expirée ou fermée).");

    internal static string DetailHeaderSubtitle(PfUiLanguage lang, string leaderName, string homeWorldName, ushort minIl) {
        var worldPart = string.IsNullOrWhiteSpace(homeWorldName) ? string.Empty : $" @ {homeWorldName}";
        var ilValue = minIl > 0 ? minIl.ToString() : "-";
        return Pick(
            lang,
            $"Leader: {leaderName}{worldPart}   |   Min IL: {ilValue}",
            $"募集主: {leaderName}{worldPart}   |   平均IL: {ilValue}",
            $"Leiter: {leaderName}{worldPart}   |   Min. GS: {ilValue}",
            $"Chef : {leaderName}{worldPart}   |   NIV min. : {ilValue}");
    }

    internal static string DetailHeaderRemaining(PfUiLanguage lang, string formattedRemaining) => Pick(
        lang,
        $"Remaining: {formattedRemaining}",
        $"残り時間: {formattedRemaining}",
        $"Restzeit: {formattedRemaining}",
        $"Temps restant : {formattedRemaining}");

    internal static string DetailPillDutyCompletion(PfUiLanguage lang) => Pick(lang, "Duty Completion", "コンプリート目的", "Abschluss", "Objectif : finir");
    internal static string DetailPillPractice(PfUiLanguage lang) => Pick(lang, "Practice", "練習", "Übung", "Entraînement");
    internal static string DetailPillLoot(PfUiLanguage lang) => Pick(lang, "Loot", "周回", "Beute", "Butin");
    internal static string DetailPillDutyComplete(PfUiLanguage lang) => Pick(lang, "Duty Complete", "コンプリート済み", "Abgeschlossen", "Accomplie");
    internal static string DetailPillDutyIncomplete(PfUiLanguage lang) => Pick(lang, "Duty Incomplete", "未コンプリート", "Unabgeschlossen", "Incomplète");
    internal static string DetailPillWeeklyUnclaimed(PfUiLanguage lang) => Pick(lang, "Weekly Unclaimed", "週制限報酬未取得", "Wöchentl. offen", "Récompense hebdo");
    internal static string DetailPillOnePerJob(PfUiLanguage lang) => Pick(lang, "1 Player/Job", "ジョブ重複なし", "1 Spieler/Job", "1 joueur/job");
    internal static string DetailPillUnrestricted(PfUiLanguage lang) => Pick(lang, "Unrestricted", "制限解除", "Uneingeschränkt", "Sans restriction");
    internal static string DetailPillMinIl(PfUiLanguage lang) => Pick(lang, "Min IL", "下限IL", "Min. GS", "NIV min.");
    internal static string DetailPillSilenceEcho(PfUiLanguage lang) => Pick(lang, "Silence Echo", "超える力無効", "Ohne Echo", "Sans Écho");
    internal static string DetailPillGreedOnly(PfUiLanguage lang) => Pick(lang, "Greed Only", "GREED限定", "Nur Gier", "Cupidité seul.");
    internal static string DetailPillLootmaster(PfUiLanguage lang) => Pick(lang, "Lootmaster", "ルートマスター", "Lootmeister", "Maître du butin");

    internal static string DetailSummaryMembers(PfUiLanguage lang, byte filled, byte available) => Pick(
        lang,
        $"Members {filled}/{available}",
        $"メンバー {filled}/{available}",
        $"Mitglieder {filled}/{available}",
        $"Membres {filled}/{available}");

    internal static string DetailSummaryCleared(PfUiLanguage lang, int cleared, int filled) => Pick(
        lang,
        $"Cleared {cleared}/{Math.Max(filled, 1)}",
        $"踏破 {cleared}/{Math.Max(filled, 1)}",
        $"Abgeschl. {cleared}/{Math.Max(filled, 1)}",
        $"Accompli {cleared}/{Math.Max(filled, 1)}");

    internal static string DetailSummaryCache(PfUiLanguage lang, int cached, int filled, int loading) => loading > 0
        ? Pick(
            lang,
            $"Cache {cached}/{filled} ({loading} loading)",
            $"取得 {cached}/{filled} ({loading} 読込中)",
            $"Cache {cached}/{filled} ({loading} lädt)",
            $"Cache {cached}/{filled} ({loading} chargement)")
        : Pick(
            lang,
            $"Cache {cached}/{filled}",
            $"取得 {cached}/{filled}",
            $"Cache {cached}/{filled}",
            $"Cache {cached}/{filled}");

    internal static string DetailSummaryOpenForJob(PfUiLanguage lang, int eligibleOpen) => Pick(
        lang,
        $"Open for your job ({eligibleOpen})",
        $"現ジョブ参加可 ({eligibleOpen})",
        $"Offen für Job ({eligibleOpen})",
        $"Ouvert pour votre job ({eligibleOpen})");

    internal static string DetailSummaryBlocked(PfUiLanguage lang, int blockedCount) => Pick(
        lang,
        $"Blocked ({blockedCount})",
        $"BL該当 ({blockedCount})",
        $"Blockiert ({blockedCount})",
        $"Bloqués ({blockedCount})");

    internal static string DetailPartyTab(PfUiLanguage lang, char partyLetter, int filled, int count) => Pick(
        lang,
        $"Party {partyLetter} ({filled}/{count})",
        $"パーティ {partyLetter} ({filled}/{count})",
        $"Gruppe {partyLetter} ({filled}/{count})",
        $"Équipe {partyLetter} ({filled}/{count})");

    internal static string DetailColJob(PfUiLanguage lang) => Pick(lang, "Job", "ジョブ", "Job", "Job");
    internal static string DetailColCharacterWorld(PfUiLanguage lang) => Pick(lang, "Character @ World", "キャラクター @ ワールド", "Charakter @ Welt", "Personnage @ Monde");
    internal static string DetailColClears(PfUiLanguage lang) => Pick(lang, "Clears", "踏破数", "Siege", "Succès");
    internal static string DetailColStatus(PfUiLanguage lang) => Pick(lang, "Status", "状態", "Status", "Statut");

    internal static string DetailOpenMatching(PfUiLanguage lang, string summary) => Pick(
        lang,
        $"(Open — Your job matches)  [{summary}]",
        $"(空き — 現ジョブ参加可)  [{summary}]",
        $"(Offen — Job passt)  [{summary}]",
        $"(Libre — Votre job convient)  [{summary}]");

    internal static string DetailOpenSummary(PfUiLanguage lang, string summary) => Pick(
        lang,
        $"(Open: {summary})",
        $"(空き: {summary})",
        $"(Offen: {summary})",
        $"(Libre : {summary})");

    internal static string DetailUnresolvedMember(PfUiLanguage lang, ulong contentId) => Pick(
        lang,
        $"(Unresolved #{contentId % 10000:D4})",
        $"(未特定 #{contentId % 10000:D4})",
        $"(Unbekannt #{contentId % 10000:D4})",
        $"(Non résolu #{contentId % 10000:D4})");

    internal static string DetailUnknownPlayer(PfUiLanguage lang) => Pick(lang, "(Unknown Player)", "(不明なプレイヤー)", "(Unbekannter Spieler)", "(Joueur inconnu)");
    internal static string DetailLeaderPrefix(PfUiLanguage lang) => Pick(lang, "[Leader] ", "[募集主] ", "[Leiter] ", "[Chef] ");
    internal static string DetailBlockedPrefix(PfUiLanguage lang) => Pick(lang, "[Blocked] ", "[BL] ", "[Blockiert] ", "[Bloqué] ");
    internal static string ContextOpenOnFflogs(PfUiLanguage lang) => Pick(lang, "Open on FFLogs", "FFLogsを開く", "Auf FFLogs öffnen", "Ouvrir sur FFLogs");
    internal static string ContextOpenOnTomestone(PfUiLanguage lang) => Pick(lang, "Open on Tomestone", "Tomestoneを開く", "Auf Tomestone öffnen", "Ouvrir sur Tomestone");
    internal static string ContextCopyIdentity(PfUiLanguage lang, string fullIdentity) => Pick(
        lang,
        $"Copy '{fullIdentity}'",
        $"'{fullIdentity}' をコピー",
        $"'{fullIdentity}' kopieren",
        $"Copier '{fullIdentity}'");
    internal static string ContextCopyNameWorld(PfUiLanguage lang) => Pick(lang, "Copy Name@World", "名前@ワールドをコピー", "Name@Welt kopieren", "Copier Nom@Monde");

    internal static string DetailStatusEligible(PfUiLanguage lang) => Pick(lang, "Eligible", "参加可", "Passend", "Éligible");
    internal static string DetailStatusOpen(PfUiLanguage lang) => Pick(lang, "Open", "空き", "Offen", "Libre");
    internal static string DetailStatusCached(PfUiLanguage lang) => Pick(lang, "Cached", "取得済", "Geladen", "En cache");
    internal static string DetailStatusLoading(PfUiLanguage lang) => Pick(lang, "Loading", "読込中", "Lädt", "Chargement");
    internal static string DetailStatusLoadingEllipsis(PfUiLanguage lang) => Pick(lang, "Loading...", "読込中...", "Lädt...", "Chargement...");
    internal static string DetailStatusUnresolved(PfUiLanguage lang) => Pick(lang, "Unresolved", "未特定", "Offen", "Non résolu");
    internal static string DetailStatusReady(PfUiLanguage lang) => Pick(lang, "Ready", "準備完了", "Bereit", "Prêt");
    internal static string DetailStatusLocal(PfUiLanguage lang) => Pick(lang, "Local", "ローカル", "Lokal", "Local");
    internal static string PhasePullsTooltip(PfUiLanguage lang, string phaseName, string progressText, int pulls) => Pick(
        lang,
        $"{phaseName}: {progressText} ({pulls} pulls)",
        $"{phaseName}: {progressText} ({pulls} ワイプ)",
        $"{phaseName}: {progressText} ({pulls} Pulls)",
        $"{phaseName} : {progressText} ({pulls} essais)");

    internal static string DetailJoinOpenNativeButton(PfUiLanguage lang) => Pick(
        lang,
        "Join / Open Native Detail",
        "参加 / 純正詳細を開く",
        "Beitreten / Natives Detail",
        "Rejoindre / Détails natifs");

    internal static string DetailJoinSampleTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Sample listings do not exist on retail servers. Select a live PF listing to open native detail/join.",
        "サンプル募集はサーバー上に存在しません。実際の募集を選択してください。",
        "Beispiele existieren nicht auf Live-Servern. Wähle einen echten Eintrag.",
        "Les exemples n'existent pas en jeu. Sélectionnez une annonce réelle.");

    internal static string DetailJoinLiveTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Delegate to native LookingForGroupDetail window so joining uses FFXIV's built-in flow.",
        "ゲーム内のパーティ募集詳細ウィンドウを開いて参加手続きを行います。",
        "Öffnet das native Gruppensuche-Detailfenster zum Beitreten.",
        "Ouvre la fenêtre native de détails pour rejoindre via le flux du jeu.");

    internal static string DetailCopySummaryButton(PfUiLanguage lang) => Pick(lang, "Copy Summary", "概要をコピー", "Zusammenfassung kopieren", "Copier résumé");
    internal static string DetailRefreshButton(PfUiLanguage lang) => Pick(lang, "Refresh Missing", "未取得を更新", "Fehlende aktualisieren", "Actualiser manquants");
    internal static string DetailRefreshTooltip(PfUiLanguage lang) => Pick(
        lang,
        "Force re-query FFLogs & Tomestone enrichment for this party.",
        "このパーティのFFLogs・Tomestone情報を再取得します。",
        "FFLogs- & Tomestone-Daten für diese Gruppe neu abfragen.",
        "Forcer le rafraîchissement FFLogs & Tomestone pour ce groupe.");
    internal static string CloseButton(PfUiLanguage lang) => Pick(lang, "Close", "閉じる", "Schließen", "Fermer");

    // Slot bar tooltip & job flags
    internal static string SlotsFilledHeader(PfUiLanguage lang, byte filled, byte available) => Pick(
        lang,
        $"{filled}/{available} Slots Filled",
        $"{filled}/{available} 枠参加中",
        $"{filled}/{available} Plätze belegt",
        $"{filled}/{available} places remplies");

    internal static string SlotFilledLabel(PfUiLanguage lang) => Pick(lang, "[Filled]", "[参加中]", "[Belegt]", "[Occupé]");
    internal static string SlotOpenLabel(PfUiLanguage lang) => Pick(lang, "Open", "空き", "Offen", "Libre");

    internal static string RoleNone(PfUiLanguage lang) => Pick(lang, "None", "なし", "Keine", "Aucun");
    internal static string RoleEmpty(PfUiLanguage lang) => Pick(lang, "Empty", "空", "Leer", "Vide");
    internal static string RoleAllRoles(PfUiLanguage lang) => Pick(lang, "All Roles", "全ロール", "Alle Rollen", "Tous rôles");
    internal static string RoleAnyJob(PfUiLanguage lang) => Pick(lang, "Any Job", "どれでも可", "Alle Jobs", "Tous jobs");
    internal static string RoleAllCombat(PfUiLanguage lang) => Pick(lang, "All Combat Jobs", "全戦闘職", "Alle Kampfklassen", "Tous combat");
    internal static string RoleAllTanks(PfUiLanguage lang) => Pick(lang, "All Tanks", "全タンク", "Alle Verteidiger", "Tous tanks");
    internal static string RoleAllHealers(PfUiLanguage lang) => Pick(lang, "All Healers", "全ヒーラー", "Alle Heiler", "Tous soigneurs");
    internal static string RoleAllDps(PfUiLanguage lang) => Pick(lang, "All DPS", "全DPS", "Alle Angreifer", "Tous DPS");
    internal static string RoleMelee(PfUiLanguage lang) => Pick(lang, "Melee", "メレー", "Nahkämpfer", "Mêlée");
    internal static string RolePhysRanged(PfUiLanguage lang) => Pick(lang, "Phys Ranged", "レンジ", "Physisch Fern", "Distance phys.");
    internal static string RoleCasters(PfUiLanguage lang) => Pick(lang, "Casters", "キャスター", "Magier", "Magie");
    internal static string RoleTankGroup(PfUiLanguage lang) => Pick(lang, "Tank", "タンク", "Verteidiger", "Tank");
    internal static string RoleHealerGroup(PfUiLanguage lang) => Pick(lang, "Healer", "ヒーラー", "Heiler", "Soigneur");
    internal static string RoleDpsGroup(PfUiLanguage lang) => Pick(lang, "DPS", "DPS", "Angreifer", "DPS");
    internal static string RoleOtherGroup(PfUiLanguage lang) => Pick(lang, "Other", "その他", "Sonstige", "Autre");

    // NativePfBridge status messages
    internal static string BridgeActionFailed(PfUiLanguage lang, string msg) => Pick(lang, $"Native PF action failed: {msg}", $"PF操作エラー: {msg}", $"Gruppensuche-Fehler: {msg}", $"Échec de l'action : {msg}");
    internal static string BridgeAgentUnavailable(PfUiLanguage lang) => Pick(lang, "AgentLookingForGroup is unavailable.", "パーティ募集エージェントを利用できません。", "AgentLookingForGroup nicht verfügbar.", "AgentLookingForGroup indisponible.");
    internal static string BridgeOpenedDetail(PfUiLanguage lang, ulong id) => Pick(lang, $"Opened native detail for #{id}.", $"詳細ウィンドウを開きました (#{id})。", $"Detailansicht für #{id} geöffnet.", $"Détails ouverts pour #{id}.");
    internal static string BridgeOpenDetailFailed(PfUiLanguage lang, ulong id) => Pick(lang, $"Could not open native listing #{id}.", $"募集 #{id} を開けませんでした。", $"Eintrag #{id} konnte nicht geöffnet werden.", $"Impossible d'ouvrir #{id}.");
    internal static string BridgeReinitializing(PfUiLanguage lang) => Pick(lang, "Re-initializing Party Finder...", "パーティ募集を再初期化中...", "Gruppensuche wird neu initialisiert...", "Réinitialisation...");
    internal static string BridgeInitializing(PfUiLanguage lang) => Pick(lang, "Initializing Party Finder...", "パーティ募集を初期化中...", "Gruppensuche wird initialisiert...", "Initialisation...");
    internal static string BridgeRefreshed(PfUiLanguage lang) => Pick(lang, "Requested PF listing refresh.", "募集リストを更新しました。", "Gruppensuche aktualisiert.", "Liste actualisée.");
    internal static string BridgeRefreshBusy(PfUiLanguage lang) => Pick(lang, "Could not refresh PF right now (wait a moment or check zone).", "現在更新できません（少し待ってから再試行してください）。", "Aktualisierung derzeit nicht möglich.", "Actualisation impossible pour le moment.");
    internal static string BridgeRequestedCategory(PfUiLanguage lang, byte cat) => Pick(lang, $"Requested PF category {cat}.", $"カテゴリ({cat})を切り替えました。", $"Kategorie {cat} angefordert.", $"Catégorie {cat} demandée.");
    internal static string BridgeSwitchedArea(PfUiLanguage lang, byte area) => Pick(lang, $"Switched PF area tab {area}.", $"募集範囲({area})を切り替えました。", $"Suchbereich {area} gewechselt.", $"Zone {area} sélectionnée.");
    internal static string BridgeNoNextPage(PfUiLanguage lang) => Pick(lang, "No next PF page available.", "次のページはありません。", "Keine nächste Seite verfügbar.", "Aucune page suivante.");
    internal static string BridgeRequestedNextPage(PfUiLanguage lang) => Pick(lang, "Requested next PF page.", "次のページを取得しました。", "Nächste Seite angefordert.", "Page suivante demandée.");
    internal static string BridgeNoPrevPage(PfUiLanguage lang) => Pick(lang, "No previous PF page available.", "前のページはありません。", "Keine vorherige Seite verfügbar.", "Aucune page précédente.");
    internal static string BridgeRequestedPrevPage(PfUiLanguage lang) => Pick(lang, "Requested previous PF page.", "前のページを取得しました。", "Vorherige Seite angefordert.", "Page précédente demandée.");
    internal static string BridgeOpenedRecruit(PfUiLanguage lang) => Pick(lang, "Opened native PF recruitment setup.", "募集登録ウィンドウを開きました。", "Rekrutierungs-Einstellungen geöffnet.", "Création d'annonce ouverte.");
    internal static string BridgeOpenRecruitFailed(PfUiLanguage lang) => Pick(lang, "Could not open PF recruitment setup.", "募集登録を開けませんでした。", "Rekrutierung konnte nicht geöffnet werden.", "Impossible d'ouvrir le recrutement.");
    internal static string BridgeInitializingRecruit(PfUiLanguage lang) => Pick(lang, "Initializing Party Finder recruitment...", "募集登録を準備中...", "Rekrutierung wird vorbereitet...", "Initialisation du recrutement...");
    internal static string BridgeOpenedDetailedSearch(PfUiLanguage lang) => Pick(lang, "Opened native PF Detailed Search.", "詳細検索ウィンドウを開きました。", "Detailsuche geöffnet.", "Recherche détaillée ouverte.");
    internal static string BridgeOpenDetailedSearchFailed(PfUiLanguage lang) => Pick(lang, "Could not open PF Detailed Search.", "詳細検索を開けませんでした。", "Detailsuche konnte nicht geöffnet werden.", "Impossible d'ouvrir la recherche détaillée.");
    internal static string BridgeInitializingDetailedSearch(PfUiLanguage lang) => Pick(lang, "Initializing Party Finder detailed search...", "詳細検索を準備中...", "Detailsuche wird vorbereitet...", "Initialisation de la recherche...");
    internal static string BridgeLoadedData(PfUiLanguage lang) => Pick(lang, "Loaded Party Finder data.", "パーティ募集データを読み込みました。", "Gruppensuche-Daten geladen.", "Données chargées.");
    internal static string BridgeOpenedAlready(PfUiLanguage lang) => Pick(lang, "Party Finder opened.", "パーティ募集を接続しました。", "Gruppensuche verbunden.", "Recherche d'équipe prête.");
    internal static string BridgeShowingNative(PfUiLanguage lang) => Pick(lang, "Showing native Party Finder window.", "ゲーム内パーティ募集を表示しました。", "Natives Gruppensuche-Fenster angezeigt.", "Fenêtre native affichée.");
    internal static string BridgeHiddenNative(PfUiLanguage lang) => Pick(lang, "Hidden native Party Finder window.", "ゲーム内パーティ募集を非表示にしました。", "Natives Gruppensuche-Fenster ausgeblendet.", "Fenêtre native masquée.");
    internal static string BridgeAlreadyClosed(PfUiLanguage lang) => Pick(lang, "Native Party Finder window is already closed.", "ゲーム内パーティ募集はすでに閉じています。", "Natives Fenster bereits geschlossen.", "Fenêtre native déjà fermée.");
}
