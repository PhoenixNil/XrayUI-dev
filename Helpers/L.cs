namespace XrayUI.Helpers;

/// <summary>
/// Strongly-typed accessors for resource strings. Each property is the canonical
/// way to look up a localized string from C# — compiler catches typos, IDE
/// supports go-to-definition. XAML still uses <c>x:Uid</c> on the same key
/// (the resw entry key for XAML carries a property suffix like <c>.Text</c>;
/// the C# key here matches the bare name).
///
/// Entries are added incrementally as call sites are localized.
/// </summary>
public static class L
{
    // ── Generic dialog buttons ─────────────────────────────────────────────
    public static string Dialog_OK        => Loc.GetString("Dialog_OK");
    public static string Dialog_Cancel    => Loc.GetString("Dialog_Cancel");
    public static string Dialog_Save      => Loc.GetString("Dialog_Save");
    public static string Dialog_Done      => Loc.GetString("Dialog_Done");
    public static string Dialog_Confirm   => Loc.GetString("Dialog_Confirm");
    public static string Dialog_Add       => Loc.GetString("Dialog_Add");
    public static string Dialog_Delete    => Loc.GetString("Dialog_Delete");
    public static string Dialog_Replace   => Loc.GetString("Dialog_Replace");
    public static string Dialog_Preparing => Loc.GetString("Dialog_Preparing");
    public static string Dialog_On        => Loc.GetString("Dialog_On");
    public static string Dialog_Off       => Loc.GetString("Dialog_Off");

    // ── Confirmation dialogs ───────────────────────────────────────────────
    public static string Confirm_ReplaceTitle => Loc.GetString("Confirm_ReplaceTitle");
    public static string Confirm_ReplaceMsg   => Loc.GetString("Confirm_ReplaceMsg");

    // ── Import link dialog ─────────────────────────────────────────────────
    public static string Import_Title       => Loc.GetString("Import_Title");
    public static string Import_Placeholder => Loc.GetString("Import_Placeholder");
    public static string Import_SupportHint => Loc.GetString("Import_SupportHint");

    // ── Edit server dialog ─────────────────────────────────────────────────
    public static string EditServer_AddTitle         => Loc.GetString("EditServer_AddTitle");
    public static string EditServer_EditTitle        => Loc.GetString("EditServer_EditTitle");
    public static string EditServer_Name             => Loc.GetString("EditServer_Name");
    public static string EditServer_Address          => Loc.GetString("EditServer_Address");
    public static string EditServer_Port             => Loc.GetString("EditServer_Port");
    public static string EditServer_Protocol         => Loc.GetString("EditServer_Protocol");
    public static string EditServer_Encryption       => Loc.GetString("EditServer_Encryption");
    public static string EditServer_Password         => Loc.GetString("EditServer_Password");
    public static string EditServer_Transport        => Loc.GetString("EditServer_Transport");
    public static string EditServer_Path             => Loc.GetString("EditServer_Path");
    public static string EditServer_WsHost           => Loc.GetString("EditServer_WsHost");
    public static string EditServer_Security         => Loc.GetString("EditServer_Security");
    public static string EditServer_Fingerprint      => Loc.GetString("EditServer_Fingerprint");
    public static string EditServer_AllowInsecure    => Loc.GetString("EditServer_AllowInsecure");
    public static string EditServer_FlowPlaceholder  => Loc.GetString("EditServer_FlowPlaceholder");

    // ── Edit port dialog ───────────────────────────────────────────────────
    public static string EditPort_Title         => Loc.GetString("EditPort_Title");
    public static string EditPort_Header        => Loc.GetString("EditPort_Header");
    public static string EditPort_AllowLan      => Loc.GetString("EditPort_AllowLan");
    public static string EditPort_LanUnavailable => Loc.GetString("EditPort_LanUnavailable");
    public static string EditPort_CopyAddress   => Loc.GetString("EditPort_CopyAddress");

    // ── TUN mode ───────────────────────────────────────────────────────────
    public static string Tun_EnableTitle => Loc.GetString("Tun_EnableTitle");

    // ── Share link dialog ──────────────────────────────────────────────────
    public static string Share_Title    => Loc.GetString("Share_Title");
    public static string Share_CopyLink => Loc.GetString("Share_CopyLink");

    // ── Startup dialog ─────────────────────────────────────────────────────
    public static string Startup_Title       => Loc.GetString("Startup_Title");
    public static string Startup_AutoStart   => Loc.GetString("Startup_AutoStart");
    public static string Startup_AutoConnect => Loc.GetString("Startup_AutoConnect");

    // ── Edit server dialog (extras not in stash) ───────────────────────────
    public static string EditServer_SocksUsername        => Loc.GetString("EditServer_SocksUsername");
    public static string EditServer_EchPlaceholder       => Loc.GetString("EditServer_EchPlaceholder");
    public static string EditServer_FinalmaskPlaceholder => Loc.GetString("EditServer_FinalmaskPlaceholder");

    // ── Chain proxy dialog ─────────────────────────────────────────────────
    public static string ChainProxy_AddTitle  => Loc.GetString("ChainProxy_AddTitle");
    public static string ChainProxy_EditTitle => Loc.GetString("ChainProxy_EditTitle");

    // ── MainViewModel ──────────────────────────────────────────────────────
    public static string Main_NoSelection  => Loc.GetString("Main_NoSelection");
    public static string Main_NotConnected => Loc.GetString("Main_NotConnected");

    // ── ControlPanel ───────────────────────────────────────────────────────
    public static string ControlPanel_Personalize        => Loc.GetString("ControlPanel_Personalize");
    public static string ControlPanel_Start              => Loc.GetString("ControlPanel_Start");
    public static string ControlPanel_Stop               => Loc.GetString("ControlPanel_Stop");
    public static string ControlPanel_StatusApplying     => Loc.GetString("ControlPanel_StatusApplying");
    public static string ControlPanel_StatusNotRunning   => Loc.GetString("ControlPanel_StatusNotRunning");
    public static string ControlPanel_RoutingGlobal      => Loc.GetString("ControlPanel_RoutingGlobal");
    public static string ControlPanel_RoutingSmart       => Loc.GetString("ControlPanel_RoutingSmart");
    public static string ControlPanel_UpdateFound        => Loc.GetString("ControlPanel_UpdateFound");

    // ── Error dialogs ──────────────────────────────────────────────────────
    public static string Error_NoServer            => Loc.GetString("Error_NoServer");
    public static string Error_NoServerMsg         => Loc.GetString("Error_NoServerMsg");
    public static string Error_StartFailed         => Loc.GetString("Error_StartFailed");
    public static string Error_XrayStartFailed     => Loc.GetString("Error_XrayStartFailed");
    public static string Error_ReapplyFailed       => Loc.GetString("Error_ReapplyFailed");
    public static string Error_XrayReapplyFailed   => Loc.GetString("Error_XrayReapplyFailed");
    public static string Error_UpdateFailed        => Loc.GetString("Error_UpdateFailed");
    public static string Error_UpdaterLaunchFailed => Loc.GetString("Error_UpdaterLaunchFailed");

    // ── Startup / Update / TUN ─────────────────────────────────────────────
    public static string Startup_SetFailed => Loc.GetString("Startup_SetFailed");
    public static string Update_Updating   => Loc.GetString("Update_Updating");
    public static string Tun_EnableMsg     => Loc.GetString("Tun_EnableMsg.Text");

    // ── ChainProxy ─────────────────────────────────────────────────────────
    public static string ChainProxy_NameRequired   => Loc.GetString("ChainProxy_NameRequired");
    public static string ChainProxy_EntryRequired  => Loc.GetString("ChainProxy_EntryRequired");
    public static string ChainProxy_ExitRequired   => Loc.GetString("ChainProxy_ExitRequired");
    public static string ChainProxy_EntryExitSame  => Loc.GetString("ChainProxy_EntryExitSame");

    // ── TUN confirmation ───────────────────────────────────────────────────
    public static string Tun_InterfaceTooltip   => Loc.GetString("Tun_InterfaceTooltip");
    public static string Tun_AutoInterfaceLabel => Loc.GetString("Tun_AutoInterfaceLabel");

    // ── Subscription dialog ────────────────────────────────────────────────
    public static string Subscription_DialogTitle_Add    => Loc.GetString("Subscription_DialogTitle_Add");
    public static string Subscription_DialogTitle_Manage => Loc.GetString("Subscription_DialogTitle_Manage");
    public static string Subscription_AddTooltip         => Loc.GetString("Subscription_AddTooltip");
    public static string Subscription_ManageTooltip      => Loc.GetString("Subscription_ManageTooltip");
    public static string Subscription_Refresh            => Loc.GetString("Subscription_Refresh");
    public static string Subscription_DeleteTooltip      => Loc.GetString("Subscription_DeleteTooltip");
    public static string Subscription_EditTooltip        => Loc.GetString("Subscription_EditTooltip");
    public static string Subscription_NeverUpdated       => Loc.GetString("Subscription_NeverUpdated");
    public static string Subscription_JustNow            => Loc.GetString("Subscription_JustNow");

    // ── Personalize ────────────────────────────────────────────────────────
    public static string Personalize_ExportSuccess         => Loc.GetString("Personalize_ExportSuccess");
    public static string Personalize_ImportFailed          => Loc.GetString("Personalize_ImportFailed");
    public static string Personalize_ImportSuccess         => Loc.GetString("Personalize_ImportSuccess");
    public static string Personalize_ImportAdvancedSuffix  => Loc.GetString("Personalize_ImportAdvancedSuffix");
    public static string Personalize_PresetMissingTitle    => Loc.GetString("Personalize_PresetMissingTitle");
    public static string Personalize_PresetMissingMsg      => Loc.GetString("Personalize_PresetMissingMsg");
    public static string Personalize_ExportTooltip         => Loc.GetString("Personalize_ExportTooltip");
    public static string Personalize_ImportTooltip         => Loc.GetString("Personalize_ImportTooltip");
    public static string Personalize_LanguageRegionExpanderAutomationName => Loc.GetString("Personalize_LanguageRegionExpanderAutomationName");
    public static string Personalize_ClashImportSuccess        => Loc.GetString("Personalize_ClashImportSuccess");
    public static string Personalize_ClashImportNoNodesTitle   => Loc.GetString("Personalize_ClashImportNoNodesTitle");
    public static string Personalize_ClashImportNoNodesMsg     => Loc.GetString("Personalize_ClashImportNoNodesMsg");
    public static string Personalize_ClashImportFailed         => Loc.GetString("Personalize_ClashImportFailed");
    public static string Personalize_ImportBlockedTitle        => Loc.GetString("Personalize_ImportBlockedTitle");
    public static string Personalize_ImportBlockedMsg          => Loc.GetString("Personalize_ImportBlockedMsg");
    public static string Personalize_HotkeyNotSet              => Loc.GetString("Personalize_HotkeyNotSet");
    public static string Personalize_HotkeyConflictTitle       => Loc.GetString("Personalize_HotkeyConflictTitle");
    public static string Personalize_HotkeyConflictMsg         => Loc.GetString("Personalize_HotkeyConflictMsg");
    public static string Personalize_HotkeySavedMsg            => Loc.GetString("Personalize_HotkeySavedMsg");
    public static string Personalize_HotkeyClearedMsg          => Loc.GetString("Personalize_HotkeyClearedMsg");
    public static string Personalize_HotkeyInvalidTitle        => Loc.GetString("Personalize_HotkeyInvalidTitle");
    public static string Personalize_HotkeyInvalidMsg          => Loc.GetString("Personalize_HotkeyInvalidMsg");
    public static string Personalize_HotkeyRecordTooltip       => Loc.GetString("Personalize_HotkeyRecordTooltip");
    public static string Personalize_HotkeyDialogClear         => Loc.GetString("Personalize_HotkeyDialogClear");
    public static string Personalize_HotkeyRecorderPlaceholder => Loc.GetString("Personalize_HotkeyRecorderPlaceholder");
    public static string Personalize_HotkeyToggleAutomationName  => Loc.GetString("Personalize_HotkeyToggleAutomationName");
    public static string Personalize_HotkeyRestoreAutomationName => Loc.GetString("Personalize_HotkeyRestoreAutomationName");
    public static string Personalize_HotkeysDialogTitle         => Loc.GetString("Personalize_HotkeysDialogTitle");
    public static string Error_ExportFailed                => Loc.GetString("Error_ExportFailed");

    // ── CustomRules / AddRule ──────────────────────────────────────────────
    public static string CustomRules_Title                  => Loc.GetString("CustomRules_Title");
    public static string CustomRules_UpdateGeoTooltip       => Loc.GetString("CustomRules_UpdateGeoTooltip");
    public static string CustomRules_AdvancedEditorTooltip  => Loc.GetString("CustomRules_AdvancedEditorTooltip");
    public static string CustomRules_EditRowTooltip         => Loc.GetString("CustomRules_EditRowTooltip");
    public static string CustomRules_DeleteRowTooltip       => Loc.GetString("CustomRules_DeleteRowTooltip");
    public static string CustomRules_PrepFailedTitle        => Loc.GetString("CustomRules_PrepFailedTitle");
    public static string CustomRules_OpenEditorFailedTitle  => Loc.GetString("CustomRules_OpenEditorFailedTitle");

    public static string AddRule_Title       => Loc.GetString("AddRule_Title");
    public static string AddRule_EditTitle   => Loc.GetString("AddRule_EditTitle");
    public static string AddRule_ErrorEmpty  => Loc.GetString("AddRule_ErrorEmpty");
    public static string AddRule_BrowseExe   => Loc.GetString("AddRule_BrowseExe");
    public static string AddRule_BrowseFolder => Loc.GetString("AddRule_BrowseFolder");
    public static string AddRule_PlaceholderDomain  => Loc.GetString("AddRule_PlaceholderDomain");
    public static string AddRule_PlaceholderIp      => Loc.GetString("AddRule_PlaceholderIp");
    public static string AddRule_PlaceholderProcess => Loc.GetString("AddRule_PlaceholderProcess");
    public static string AddRule_HintDomain  => Loc.GetString("AddRule_HintDomain");
    public static string AddRule_HintIp      => Loc.GetString("AddRule_HintIp");
    public static string AddRule_HintProcess => Loc.GetString("AddRule_HintProcess");

    public static string GeoUpdate_Updating         => Loc.GetString("GeoUpdate_Updating");
    public static string GeoUpdate_AlreadyLatest    => Loc.GetString("GeoUpdate_AlreadyLatest");
    public static string GeoUpdate_AlreadyLatestMsg => Loc.GetString("GeoUpdate_AlreadyLatestMsg");
    public static string GeoUpdate_TunRestart       => Loc.GetString("GeoUpdate_TunRestart");
    public static string GeoUpdate_ReloadedOk       => Loc.GetString("GeoUpdate_ReloadedOk");
    public static string GeoUpdate_RestartRequired  => Loc.GetString("GeoUpdate_RestartRequired");
    public static string GeoUpdate_NextStart        => Loc.GetString("GeoUpdate_NextStart");
    public static string GeoUpdate_Success          => Loc.GetString("GeoUpdate_Success");

    // ── LogWindow ──────────────────────────────────────────────────────────
    public static string Log_Title         => Loc.GetString("Log_Title");
    public static string Log_Running       => Loc.GetString("Log_Running");
    public static string Log_NotRunning    => Loc.GetString("Log_NotRunning");
    public static string Log_PrivacyTitle  => Loc.GetString("Log_PrivacyTitle");
    public static string Log_PrivacySaved  => Loc.GetString("Log_PrivacySaved");
    public static string Log_IpMask        => Loc.GetString("Log_IpMask");
    public static string Log_MaskOff       => Loc.GetString("Log_MaskOff");
    public static string Log_Level         => Loc.GetString("Log_Level");
    public static string Log_DnsLog        => Loc.GetString("Log_DnsLog");
    public static string Log_DnsLogOn      => Loc.GetString("Log_DnsLogOn");
    public static string Log_AutoScroll    => Loc.GetString("Log_AutoScroll");
    public static string Log_CopyAll       => Loc.GetString("Log_CopyAll");
    public static string Log_Clear         => Loc.GetString("Log_Clear");
    public static string Log_PrivacyTooltip => Loc.GetString("Log_PrivacyTooltip");

    // ── MainWindow / Tray ──────────────────────────────────────────────────
    public static string MainWindow_Title      => Loc.GetString("MainWindow_Title");
    public static string MainWindow_ToggleMini => Loc.GetString("MainWindow_ToggleMini");
    public static string MainWindow_ExpandFull => Loc.GetString("MainWindow_ExpandFull");
    public static string MainWindow_Close      => Loc.GetString("MainWindow_Close");
    public static string Tray_Open             => Loc.GetString("Tray_Open");
    public static string Tray_Exit             => Loc.GetString("Tray_Exit");
    // Tray_TooltipRunning is parameterized ("XrayUI - {0}") — use Loc.Format at the call site.
    public static string Tray_TooltipIdle      => Loc.GetString("Tray_TooltipIdle");

    // ── ServerList ─────────────────────────────────────────────────────────
    public static string ServerList_AllServers       => Loc.GetString("ServerList_AllServers");
    public static string ServerList_Ungrouped        => Loc.GetString("ServerList_Ungrouped");
    public static string ServerList_Favorites        => Loc.GetString("ServerList_Favorites");
    public static string ServerList_UnnamedSub       => Loc.GetString("ServerList_UnnamedSub");
    public static string ServerList_OrphanSub        => Loc.GetString("ServerList_OrphanSub");
    public static string ServerList_Edit             => Loc.GetString("ServerList_Edit");
    public static string ServerList_Delete           => Loc.GetString("ServerList_Delete");
    public static string ServerList_Share            => Loc.GetString("ServerList_Share");
    public static string ServerList_AddFavorite      => Loc.GetString("ServerList_AddFavorite");
    public static string ServerList_RemoveFavorite   => Loc.GetString("ServerList_RemoveFavorite");
    public static string ServerList_FilterTooltip    => Loc.GetString("ServerList_FilterTooltip");
    public static string ServerList_SortTooltip      => Loc.GetString("ServerList_SortTooltip");
    public static string ServerList_TestLatencyTooltip => Loc.GetString("ServerList_TestLatencyTooltip");
    public static string ServerList_SortActiveHint   => Loc.GetString("ServerList_SortActiveHint");

    // ── ServerDetail ──────────────────────────────────────────────────────
    public static string ServerDetail_NoServer       => Loc.GetString("ServerDetail_NoServer");
    public static string ServerDetail_Address        => Loc.GetString("ServerDetail_Address");
    public static string ServerDetail_Port           => Loc.GetString("ServerDetail_Port");
    public static string ServerDetail_Encryption     => Loc.GetString("ServerDetail_Encryption");
    public static string ServerDetail_Security       => Loc.GetString("ServerDetail_Security");
    public static string ServerDetail_Entry          => Loc.GetString("ServerDetail_Entry");
    public static string ServerDetail_Exit           => Loc.GetString("ServerDetail_Exit");
    public static string ServerDetail_EntryMissing   => Loc.GetString("ServerDetail_EntryMissing");
    public static string ServerDetail_ExitMissing    => Loc.GetString("ServerDetail_ExitMissing");
    public static string ServerDetail_AuthLabel      => Loc.GetString("ServerDetail_AuthLabel");
    public static string ServerDetail_ChainLabel     => Loc.GetString("ServerDetail_ChainLabel");
    public static string ServerDetail_NoAuth         => Loc.GetString("ServerDetail_NoAuth");
    public static string ServerDetail_UserPass       => Loc.GetString("ServerDetail_UserPass");
    public static string ServerDetail_NotTested      => Loc.GetString("ServerDetail_NotTested");
    public static string ServerDetail_Testing        => Loc.GetString("ServerDetail_Testing");
    public static string ServerDetail_Timeout        => Loc.GetString("ServerDetail_Timeout");
    public static string ServerDetail_Failed         => Loc.GetString("ServerDetail_Failed");
    public static string ServerDetail_RetestLatency  => Loc.GetString("ServerDetail_RetestLatency");
    public static string ServerDetail_CopyShareLink  => Loc.GetString("ServerDetail_CopyShareLink");

    // ── Import link dialog ────────────────────────────────────────────────
    public static string Import_ParseFailed    => Loc.GetString("Import_ParseFailed");
    public static string Import_ParseFailedMsg => Loc.GetString("Import_ParseFailedMsg");

    // ── Subscription ──────────────────────────────────────────────────────
    public static string Subscription_FetchFailed       => Loc.GetString("Subscription_FetchFailed");
    public static string Subscription_NoParsed          => Loc.GetString("Subscription_NoParsed");
    public static string Subscription_StopFirst_Delete  => Loc.GetString("Subscription_StopFirst_Delete");
    public static string Subscription_UnknownError      => Loc.GetString("Subscription_UnknownError");

    // ── Share dialog ──────────────────────────────────────────────────────
    public static string Share_NotSupported    => Loc.GetString("Share_NotSupported");
    public static string Share_NotSupportedMsg => Loc.GetString("Share_NotSupportedMsg");

    // ── Confirm delete ────────────────────────────────────────────────────
    public static string Confirm_DeleteTitle => Loc.GetString("Confirm_DeleteTitle");

    // ── DNS settings dialog ────────────────────────────────────────────────
    public static string Dns_DialogTitle        => Loc.GetString("Dns_DialogTitle");
    public static string Dns_ResetDefaults      => Loc.GetString("Dns_ResetDefaults");
    public static string Dns_ServerPlaceholder  => Loc.GetString("Dns_ServerPlaceholder");
    public static string Dns_QueryStrategyLabel => Loc.GetString("Dns_QueryStrategyLabel");
    public static string Dns_EnableCacheLabel   => Loc.GetString("Dns_EnableCacheLabel");
    public static string Dns_DirectTitle        => Loc.GetString("Dns_DirectTitle");
    public static string Dns_DirectDesc         => Loc.GetString("Dns_DirectDesc");
    public static string Dns_ProxyTitle         => Loc.GetString("Dns_ProxyTitle");
    public static string Dns_ProxyDesc          => Loc.GetString("Dns_ProxyDesc");
    public static string Dns_TunOnlyHint        => Loc.GetString("Dns_TunOnlyHint");
    public static string Dns_Experimental       => Loc.GetString("Dns_Experimental");
    public static string Dns_Provider_Ali       => Loc.GetString("Dns_Provider_Ali");
    public static string Dns_Provider_Tencent   => Loc.GetString("Dns_Provider_Tencent");
    public static string Dns_Provider_Google    => Loc.GetString("Dns_Provider_Google");
    public static string Dns_Strategy_V4Only    => Loc.GetString("Dns_Strategy_V4Only");
    public static string Dns_Strategy_V6Only    => Loc.GetString("Dns_Strategy_V6Only");
    public static string Dns_Strategy_Auto      => Loc.GetString("Dns_Strategy_Auto");

    // ── Xray engine / chain / TUN preflight (parameterless; parameterized keys
    //    like Xray_ExeNotFound / XrayLog_Started use Loc.Format at the call site) ──
    public static string XrayLog_Stopped         => Loc.GetString("XrayLog_Stopped");
    public static string XrayLog_ProcessExited   => Loc.GetString("XrayLog_ProcessExited");
    public static string XrayLog_Shutdown        => Loc.GetString("XrayLog_Shutdown");
    public static string Chain_NeedServerList    => Loc.GetString("Chain_NeedServerList");
    public static string Chain_EndpointMissing   => Loc.GetString("Chain_EndpointMissing");
    public static string Chain_NoNesting         => Loc.GetString("Chain_NoNesting");
    public static string Tun_PreflightErrorTitle => Loc.GetString("Tun_PreflightErrorTitle");
}
