// === RunSpace i18n System ===
var I18N_KEY = "runspace_lang";
var I18N = {
  current: localStorage.getItem("runspace_lang") || "en",
  t: {},
  set: function(lang) {
    if (!I18N.t[lang]) return;
    I18N.current = lang;
    localStorage.setItem(I18N_KEY, lang);
    I18N.apply();
  },
  get: function(key) {
    var d = I18N.t[I18N.current];
    return (d && d[key]) || (I18N.t["en"] && I18N.t["en"][key]) || key;
  },
  apply: function() {
    document.querySelectorAll("[data-i18n]").forEach(function(el) {
      var key = el.getAttribute("data-i18n");
      var val = I18N.get(key);
      if (el.tagName === "INPUT" || el.tagName === "TEXTAREA") {
        el.placeholder = val;
      } else {
        el.textContent = val;
      }
    });
    document.querySelectorAll("[data-i18n-title]").forEach(function(el) {
      el.title = I18N.get(el.getAttribute("data-i18n-title"));
    });
    // Update language switcher if exists
    var sw = document.getElementById("langSwitcher");
    if (sw) sw.textContent = I18N.current.toUpperCase();
  }
};

// === Translations ===
I18N.t["sv"] = {
  // General
  "home": "Hem",
  "chat": "Chatt",
  "profile": "Profil",
  "settings": "Inst\u00e4llningar",
  "login": "Logga in",
  "logout": "Logga ut",
  "save": "Spara",
  "cancel": "Avbryt",
  "close": "St\u00e4ng",
  "search": "S\u00f6k",
  "delete": "Ta bort",
  "edit": "Redigera",
  "send": "Skicka",
  "file": "Fil",
  "loading": "Laddar...",
  "error": "Fel",
  "success": "Klart",

  // Auth
  "not_logged_in": "Inte inloggad",
  "login_required": "Du m\u00e5ste vara inloggad.",
  "login_link": "Logga in",

  // Chat
  "ready_to_chat": "Redo att chatta",
  "choose_convo": "V\u00e4lj en konversation eller skapa en grupp.",
  "choose_convo_short": "V\u00e4lj en konversation",
  "no_convos": "Inga konversationer \u00e4nnu.",
  "empty_convo": "Tom konversation",
  "write_to": "Skriv till",
  "type_message": "Skriv ett meddelande...",
  "new_convo": "Ny konversation",
  "username": "Anv\u00e4ndarnamn",
  "open_chat": "\u00d6ppna chatt",
  "clear": "Rensa",
  "enter_sends": "Enter skickar \u00b7 Shift+Enter ny rad",
  "no_recipient": "Ingen mottagare vald.",
  "not_connected": "Inte ansluten.",
  "to_user": "Till @",
  "connected": "Ansluten",
  "connecting": "Ansluter...",
  "reconnecting": "\u00c5teransluter...",
  "disconnected": "Fr\u00e5nkopplad",
  "connection_failed": "Anslutning misslyckades",
  "dm": "Direktmeddelanden",
  "voice_channels": "R\u00f6stkanaler",
  "conversations": "Konversationer",
  "online": "Online",

  // Groups
  "create_group": "Skapa grupp",
  "group_name": "Gruppnamn",
  "group_desc": "Beskrivning (valfritt)",
  "text_channels": "Textkanaler",
  "voice_channels_group": "R\u00f6stkanaler",
  "empty_channel": "Tom kanal",
  "be_first": "Var f\u00f6rst med att skriva!",
  "no_channels": "Inga kanaler",
  "create_channel": "Skapa kanal",
  "invite_members": "Bjud in medlemmar",
  "search_users": "S\u00f6k anv\u00e4ndare",
  "invite": "Bjud in",
  "invited": "Inbjuden \u2713",
  "group_invites": "Gruppinbjudningar",
  "join": "G\u00e5 med",
  "decline": "Nej",
  "leave_group": "L\u00e4mna grupp",
  "delete_group": "Ta bort grupp",
  "danger_zone": "Farozon",
  "cannot_undo": "Dessa \u00e5tg\u00e4rder kan inte \u00e5ngras.",
  "kick": "Sparka",
  "settings_saved": "Sparat!",

  // Voice
  "connected_voice": "Ansluten till r\u00f6st",
  "disconnect": "Koppla fr\u00e5n",
  "muted": "Mutad",
  "deafened": "D\u00f6v",
  "quality": "Kvalitet",
  "mic_denied": "Mikrofontillg\u00e5ng nekad.",
  "no_mic": "Ingen mikrofon hittad.",

  // Calls
  "calling": "Ringer...",
  "ringing": "Ringer dig...",
  "in_call": "I samtal",
  "call_ended": "Samtalet avslutades",
  "call_rejected": "Avvisade samtalet",
  "no_answer": "Inget svar",
  "hang_up": "L\u00e4gg p\u00e5",
  "answer": "Svara",
  "reject_call": "Avvisa",
  "screen_share": "Sk\u00e4rmdelning",
  "choose_quality": "V\u00e4lj kvalitet",
  "minimize": "Minimera",
  "sharing_screen": "delar sk\u00e4rm",

  // Profile
  "change_bio": "\u00c4ndra bio",
  "bio_desc": "Max 500 tecken. Syns p\u00e5 din publika profil.",
  "nationality": "Nationalitet",
  "nat_desc": "Visas med flagga p\u00e5 din profil.",
  "none_selected": "Ingen vald",
  "languages": "Spr\u00e5k",
  "lang_desc": "Vilka spr\u00e5k talar du?",
  "profile_pic": "Profilbild",
  "pic_desc": "PNG, JPG, GIF eller WebP. Max 5 MB.",
  "upload": "Ladda upp",
  "remove_pic": "Ta bort bild",
  "two_factor": "Tv\u00e5faktor (2FA)",
  "two_factor_desc": "Skydda kontot med en authenticator-app.",
  "enabled": "Aktiverat",
  "not_enabled": "Inte aktiverat",
  "activate": "Aktivera",
  "sessions": "Aktiva sessioner",
  "sessions_desc": "Enheter som \u00e4r inloggade p\u00e5 ditt konto.",
  "logout_all": "Logga ut alla",
  "account": "Konto",
  "account_desc": "L\u00f6senord och kontos\u00e4kerhet.",
  "change_pw": "Byt l\u00f6senord",
  "freeze": "Frys konto 24h",
  "member_since": "Medlem"
};

I18N.t["en"] = {
  "home": "Home",
  "chat": "Chat",
  "profile": "Profile",
  "settings": "Settings",
  "login": "Log in",
  "logout": "Log out",
  "save": "Save",
  "cancel": "Cancel",
  "close": "Close",
  "search": "Search",
  "delete": "Delete",
  "edit": "Edit",
  "send": "Send",
  "file": "File",
  "loading": "Loading...",
  "error": "Error",
  "success": "Done",
  "not_logged_in": "Not logged in",
  "login_required": "You must be logged in.",
  "login_link": "Log in",
  "ready_to_chat": "Ready to chat",
  "choose_convo": "Choose a conversation or create a group.",
  "choose_convo_short": "Choose a conversation",
  "no_convos": "No conversations yet.",
  "empty_convo": "Empty conversation",
  "write_to": "Write to",
  "type_message": "Type a message...",
  "new_convo": "New conversation",
  "username": "Username",
  "open_chat": "Open chat",
  "clear": "Clear",
  "enter_sends": "Enter sends \u00b7 Shift+Enter new line",
  "no_recipient": "No recipient selected.",
  "not_connected": "Not connected.",
  "to_user": "To @",
  "connected": "Connected",
  "connecting": "Connecting...",
  "reconnecting": "Reconnecting...",
  "disconnected": "Disconnected",
  "connection_failed": "Connection failed",
  "dm": "Direct messages",
  "voice_channels": "Voice channels",
  "conversations": "Conversations",
  "online": "Online",
  "create_group": "Create group",
  "group_name": "Group name",
  "group_desc": "Description (optional)",
  "text_channels": "Text channels",
  "voice_channels_group": "Voice channels",
  "empty_channel": "Empty channel",
  "be_first": "Be the first to write!",
  "no_channels": "No channels",
  "create_channel": "Create channel",
  "invite_members": "Invite members",
  "search_users": "Search users",
  "invite": "Invite",
  "invited": "Invited \u2713",
  "group_invites": "Group invitations",
  "join": "Join",
  "decline": "No",
  "leave_group": "Leave group",
  "delete_group": "Delete group",
  "danger_zone": "Danger zone",
  "cannot_undo": "These actions cannot be undone.",
  "kick": "Kick",
  "settings_saved": "Saved!",
  "connected_voice": "Connected to voice",
  "disconnect": "Disconnect",
  "muted": "Muted",
  "deafened": "Deafened",
  "quality": "Quality",
  "mic_denied": "Microphone access denied.",
  "no_mic": "No microphone found.",
  "calling": "Calling...",
  "ringing": "Calling you...",
  "in_call": "In call",
  "call_ended": "Call ended",
  "call_rejected": "Call rejected",
  "no_answer": "No answer",
  "hang_up": "Hang up",
  "answer": "Answer",
  "reject_call": "Reject",
  "screen_share": "Screen share",
  "choose_quality": "Choose quality",
  "minimize": "Minimize",
  "sharing_screen": "is sharing screen",
  "change_bio": "Change bio",
  "bio_desc": "Max 500 characters. Shown on your public profile.",
  "nationality": "Nationality",
  "nat_desc": "Shown with flag on your profile.",
  "none_selected": "None selected",
  "languages": "Languages",
  "lang_desc": "What languages do you speak?",
  "profile_pic": "Profile picture",
  "pic_desc": "PNG, JPG, GIF or WebP. Max 5 MB.",
  "upload": "Upload",
  "remove_pic": "Remove picture",
  "two_factor": "Two-factor (2FA)",
  "two_factor_desc": "Protect your account with an authenticator app.",
  "enabled": "Enabled",
  "not_enabled": "Not enabled",
  "activate": "Activate",
  "sessions": "Active sessions",
  "sessions_desc": "Devices logged into your account.",
  "logout_all": "Log out all",
  "account": "Account",
  "account_desc": "Password and account security.",
  "change_pw": "Change password",
  "freeze": "Freeze account 24h",
  "member_since": "Member"
};

I18N.t["fr"] = {
  "home": "Accueil",
  "chat": "Discussion",
  "profile": "Profil",
  "settings": "Param\u00e8tres",
  "login": "Connexion",
  "logout": "D\u00e9connexion",
  "save": "Enregistrer",
  "cancel": "Annuler",
  "close": "Fermer",
  "search": "Rechercher",
  "delete": "Supprimer",
  "edit": "Modifier",
  "send": "Envoyer",
  "file": "Fichier",
  "loading": "Chargement...",
  "error": "Erreur",
  "success": "Termin\u00e9",
  "not_logged_in": "Non connect\u00e9",
  "login_required": "Vous devez \u00eatre connect\u00e9.",
  "login_link": "Connexion",
  "ready_to_chat": "Pr\u00eat \u00e0 discuter",
  "choose_convo": "Choisissez une conversation ou cr\u00e9ez un groupe.",
  "choose_convo_short": "Choisissez une conversation",
  "no_convos": "Aucune conversation.",
  "empty_convo": "Conversation vide",
  "write_to": "\u00c9crire \u00e0",
  "type_message": "\u00c9crivez un message...",
  "new_convo": "Nouvelle conversation",
  "username": "Nom d'utilisateur",
  "open_chat": "Ouvrir le chat",
  "clear": "Effacer",
  "enter_sends": "Entr\u00e9e envoie \u00b7 Shift+Entr\u00e9e nouvelle ligne",
  "no_recipient": "Aucun destinataire.",
  "not_connected": "Non connect\u00e9.",
  "to_user": "\u00c0 @",
  "connected": "Connect\u00e9",
  "connecting": "Connexion...",
  "reconnecting": "Reconnexion...",
  "disconnected": "D\u00e9connect\u00e9",
  "connection_failed": "\u00c9chec de connexion",
  "dm": "Messages directs",
  "voice_channels": "Salons vocaux",
  "conversations": "Conversations",
  "online": "En ligne",
  "create_group": "Cr\u00e9er un groupe",
  "group_name": "Nom du groupe",
  "group_desc": "Description (facultatif)",
  "text_channels": "Salons textuels",
  "voice_channels_group": "Salons vocaux",
  "empty_channel": "Salon vide",
  "be_first": "Soyez le premier \u00e0 \u00e9crire !",
  "no_channels": "Aucun salon",
  "create_channel": "Cr\u00e9er un salon",
  "invite_members": "Inviter des membres",
  "search_users": "Rechercher des utilisateurs",
  "invite": "Inviter",
  "invited": "Invit\u00e9 \u2713",
  "group_invites": "Invitations de groupe",
  "join": "Rejoindre",
  "decline": "Non",
  "leave_group": "Quitter le groupe",
  "delete_group": "Supprimer le groupe",
  "danger_zone": "Zone de danger",
  "cannot_undo": "Ces actions sont irr\u00e9versibles.",
  "kick": "Expulser",
  "settings_saved": "Enregistr\u00e9 !",
  "connected_voice": "Connect\u00e9 au vocal",
  "disconnect": "D\u00e9connecter",
  "muted": "Coup\u00e9",
  "deafened": "Sourd",
  "quality": "Qualit\u00e9",
  "mic_denied": "Acc\u00e8s au micro refus\u00e9.",
  "no_mic": "Aucun micro trouv\u00e9.",
  "calling": "Appel...",
  "ringing": "Vous appelle...",
  "in_call": "En appel",
  "call_ended": "Appel termin\u00e9",
  "call_rejected": "Appel refus\u00e9",
  "no_answer": "Pas de r\u00e9ponse",
  "hang_up": "Raccrocher",
  "answer": "R\u00e9pondre",
  "reject_call": "Refuser",
  "screen_share": "Partage d'\u00e9cran",
  "choose_quality": "Choisir la qualit\u00e9",
  "minimize": "R\u00e9duire",
  "sharing_screen": "partage son \u00e9cran",
  "change_bio": "Modifier la bio",
  "bio_desc": "500 caract\u00e8res max. Visible sur votre profil public.",
  "nationality": "Nationalit\u00e9",
  "nat_desc": "Affich\u00e9 avec drapeau sur votre profil.",
  "none_selected": "Aucun s\u00e9lectionn\u00e9",
  "languages": "Langues",
  "lang_desc": "Quelles langues parlez-vous ?",
  "profile_pic": "Photo de profil",
  "pic_desc": "PNG, JPG, GIF ou WebP. Max 5 Mo.",
  "upload": "T\u00e9l\u00e9charger",
  "remove_pic": "Supprimer la photo",
  "two_factor": "Double authentification (2FA)",
  "two_factor_desc": "Prot\u00e9gez votre compte avec une app d'authentification.",
  "enabled": "Activ\u00e9",
  "not_enabled": "Non activ\u00e9",
  "activate": "Activer",
  "sessions": "Sessions actives",
  "sessions_desc": "Appareils connect\u00e9s \u00e0 votre compte.",
  "logout_all": "Tout d\u00e9connecter",
  "account": "Compte",
  "account_desc": "Mot de passe et s\u00e9curit\u00e9.",
  "change_pw": "Changer le mot de passe",
  "freeze": "Geler le compte 24h",
  "member_since": "Membre"
};

I18N.t["ru"] = {
  "home": "\u0413\u043b\u0430\u0432\u043d\u0430\u044f",
  "chat": "\u0427\u0430\u0442",
  "profile": "\u041f\u0440\u043e\u0444\u0438\u043b\u044c",
  "settings": "\u041d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438",
  "login": "\u0412\u043e\u0439\u0442\u0438",
  "logout": "\u0412\u044b\u0439\u0442\u0438",
  "save": "\u0421\u043e\u0445\u0440\u0430\u043d\u0438\u0442\u044c",
  "cancel": "\u041e\u0442\u043c\u0435\u043d\u0430",
  "close": "\u0417\u0430\u043a\u0440\u044b\u0442\u044c",
  "search": "\u041f\u043e\u0438\u0441\u043a",
  "delete": "\u0423\u0434\u0430\u043b\u0438\u0442\u044c",
  "edit": "\u0420\u0435\u0434\u0430\u043a\u0442\u0438\u0440\u043e\u0432\u0430\u0442\u044c",
  "send": "\u041e\u0442\u043f\u0440\u0430\u0432\u0438\u0442\u044c",
  "file": "\u0424\u0430\u0439\u043b",
  "loading": "\u0417\u0430\u0433\u0440\u0443\u0437\u043a\u0430...",
  "error": "\u041e\u0448\u0438\u0431\u043a\u0430",
  "success": "\u0413\u043e\u0442\u043e\u0432\u043e",
  "not_logged_in": "\u041d\u0435 \u0430\u0432\u0442\u043e\u0440\u0438\u0437\u043e\u0432\u0430\u043d",
  "login_required": "\u041d\u0435\u043e\u0431\u0445\u043e\u0434\u0438\u043c\u043e \u0432\u043e\u0439\u0442\u0438.",
  "login_link": "\u0412\u043e\u0439\u0442\u0438",
  "ready_to_chat": "\u0413\u043e\u0442\u043e\u0432 \u043a \u0447\u0430\u0442\u0443",
  "choose_convo": "\u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u0431\u0435\u0441\u0435\u0434\u0443 \u0438\u043b\u0438 \u0441\u043e\u0437\u0434\u0430\u0439\u0442\u0435 \u0433\u0440\u0443\u043f\u043f\u0443.",
  "choose_convo_short": "\u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u0431\u0435\u0441\u0435\u0434\u0443",
  "no_convos": "\u041d\u0435\u0442 \u0431\u0435\u0441\u0435\u0434.",
  "empty_convo": "\u041f\u0443\u0441\u0442\u0430\u044f \u0431\u0435\u0441\u0435\u0434\u0430",
  "write_to": "\u041d\u0430\u043f\u0438\u0441\u0430\u0442\u044c",
  "type_message": "\u041d\u0430\u043f\u0438\u0448\u0438\u0442\u0435 \u0441\u043e\u043e\u0431\u0449\u0435\u043d\u0438\u0435...",
  "new_convo": "\u041d\u043e\u0432\u0430\u044f \u0431\u0435\u0441\u0435\u0434\u0430",
  "username": "\u0418\u043c\u044f \u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u044f",
  "open_chat": "\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0447\u0430\u0442",
  "clear": "\u041e\u0447\u0438\u0441\u0442\u0438\u0442\u044c",
  "enter_sends": "Enter \u043e\u0442\u043f\u0440\u0430\u0432\u043b\u044f\u0435\u0442 \u00b7 Shift+Enter \u043d\u043e\u0432\u0430\u044f \u0441\u0442\u0440\u043e\u043a\u0430",
  "no_recipient": "\u041f\u043e\u043b\u0443\u0447\u0430\u0442\u0435\u043b\u044c \u043d\u0435 \u0432\u044b\u0431\u0440\u0430\u043d.",
  "not_connected": "\u041d\u0435\u0442 \u0441\u043e\u0435\u0434\u0438\u043d\u0435\u043d\u0438\u044f.",
  "to_user": "\u041a\u043e\u043c\u0443 @",
  "connected": "\u041f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u043e",
  "connecting": "\u041f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u0435...",
  "reconnecting": "\u041f\u0435\u0440\u0435\u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u0435...",
  "disconnected": "\u041e\u0442\u043a\u043b\u044e\u0447\u0435\u043d\u043e",
  "connection_failed": "\u041e\u0448\u0438\u0431\u043a\u0430 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u044f",
  "dm": "\u041b\u0438\u0447\u043d\u044b\u0435 \u0441\u043e\u043e\u0431\u0449\u0435\u043d\u0438\u044f",
  "voice_channels": "\u0413\u043e\u043b\u043e\u0441\u043e\u0432\u044b\u0435 \u043a\u0430\u043d\u0430\u043b\u044b",
  "conversations": "\u0411\u0435\u0441\u0435\u0434\u044b",
  "online": "\u0412 \u0441\u0435\u0442\u0438",
  "create_group": "\u0421\u043e\u0437\u0434\u0430\u0442\u044c \u0433\u0440\u0443\u043f\u043f\u0443",
  "group_name": "\u041d\u0430\u0437\u0432\u0430\u043d\u0438\u0435 \u0433\u0440\u0443\u043f\u043f\u044b",
  "group_desc": "\u041e\u043f\u0438\u0441\u0430\u043d\u0438\u0435 (\u043d\u0435\u043e\u0431\u044f\u0437\u0430\u0442\u0435\u043b\u044c\u043d\u043e)",
  "text_channels": "\u0422\u0435\u043a\u0441\u0442\u043e\u0432\u044b\u0435 \u043a\u0430\u043d\u0430\u043b\u044b",
  "voice_channels_group": "\u0413\u043e\u043b\u043e\u0441\u043e\u0432\u044b\u0435 \u043a\u0430\u043d\u0430\u043b\u044b",
  "empty_channel": "\u041f\u0443\u0441\u0442\u043e\u0439 \u043a\u0430\u043d\u0430\u043b",
  "be_first": "\u0411\u0443\u0434\u044c\u0442\u0435 \u043f\u0435\u0440\u0432\u044b\u043c!",
  "no_channels": "\u041d\u0435\u0442 \u043a\u0430\u043d\u0430\u043b\u043e\u0432",
  "create_channel": "\u0421\u043e\u0437\u0434\u0430\u0442\u044c \u043a\u0430\u043d\u0430\u043b",
  "invite_members": "\u041f\u0440\u0438\u0433\u043b\u0430\u0441\u0438\u0442\u044c",
  "search_users": "\u041f\u043e\u0438\u0441\u043a \u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u0435\u0439",
  "invite": "\u041f\u0440\u0438\u0433\u043b\u0430\u0441\u0438\u0442\u044c",
  "invited": "\u041f\u0440\u0438\u0433\u043b\u0430\u0448\u0435\u043d \u2713",
  "group_invites": "\u041f\u0440\u0438\u0433\u043b\u0430\u0448\u0435\u043d\u0438\u044f",
  "join": "\u0412\u0441\u0442\u0443\u043f\u0438\u0442\u044c",
  "decline": "\u041d\u0435\u0442",
  "leave_group": "\u041f\u043e\u043a\u0438\u043d\u0443\u0442\u044c \u0433\u0440\u0443\u043f\u043f\u0443",
  "delete_group": "\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u0433\u0440\u0443\u043f\u043f\u0443",
  "danger_zone": "\u041e\u043f\u0430\u0441\u043d\u0430\u044f \u0437\u043e\u043d\u0430",
  "cannot_undo": "\u042d\u0442\u0438 \u0434\u0435\u0439\u0441\u0442\u0432\u0438\u044f \u043d\u0435\u043b\u044c\u0437\u044f \u043e\u0442\u043c\u0435\u043d\u0438\u0442\u044c.",
  "kick": "\u0412\u044b\u0433\u043d\u0430\u0442\u044c",
  "settings_saved": "\u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u043e!",
  "connected_voice": "\u041f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d \u043a \u0433\u043e\u043b\u043e\u0441\u0443",
  "disconnect": "\u041e\u0442\u043a\u043b\u044e\u0447\u0438\u0442\u044c\u0441\u044f",
  "muted": "\u0417\u0432\u0443\u043a \u0432\u044b\u043a\u043b.",
  "deafened": "\u0413\u043b\u0443\u0445\u043e\u0439",
  "quality": "\u041a\u0430\u0447\u0435\u0441\u0442\u0432\u043e",
  "mic_denied": "\u0414\u043e\u0441\u0442\u0443\u043f \u043a \u043c\u0438\u043a\u0440\u043e\u0444\u043e\u043d\u0443 \u0437\u0430\u043f\u0440\u0435\u0449\u0435\u043d.",
  "no_mic": "\u041c\u0438\u043a\u0440\u043e\u0444\u043e\u043d \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d.",
  "calling": "\u0417\u0432\u043e\u043d\u043e\u043a...",
  "ringing": "\u0412\u0430\u043c \u0437\u0432\u043e\u043d\u044f\u0442...",
  "in_call": "\u0412 \u0437\u0432\u043e\u043d\u043a\u0435",
  "call_ended": "\u0417\u0432\u043e\u043d\u043e\u043a \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d",
  "call_rejected": "\u0417\u0432\u043e\u043d\u043e\u043a \u043e\u0442\u043a\u043b\u043e\u043d\u0435\u043d",
  "no_answer": "\u041d\u0435\u0442 \u043e\u0442\u0432\u0435\u0442\u0430",
  "hang_up": "\u041f\u043e\u043b\u043e\u0436\u0438\u0442\u044c \u0442\u0440\u0443\u0431\u043a\u0443",
  "answer": "\u041e\u0442\u0432\u0435\u0442\u0438\u0442\u044c",
  "reject_call": "\u041e\u0442\u043a\u043b\u043e\u043d\u0438\u0442\u044c",
  "screen_share": "\u0414\u0435\u043c\u043e\u043d\u0441\u0442\u0440\u0430\u0446\u0438\u044f \u044d\u043a\u0440\u0430\u043d\u0430",
  "choose_quality": "\u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u043a\u0430\u0447\u0435\u0441\u0442\u0432\u043e",
  "minimize": "\u0421\u0432\u0435\u0440\u043d\u0443\u0442\u044c",
  "sharing_screen": "\u0434\u0435\u043c\u043e\u043d\u0441\u0442\u0440\u0438\u0440\u0443\u0435\u0442 \u044d\u043a\u0440\u0430\u043d",
  "change_bio": "\u0418\u0437\u043c\u0435\u043d\u0438\u0442\u044c \u0431\u0438\u043e",
  "bio_desc": "\u041c\u0430\u043a\u0441. 500 \u0441\u0438\u043c\u0432\u043e\u043b\u043e\u0432.",
  "nationality": "\u041d\u0430\u0446\u0438\u043e\u043d\u0430\u043b\u044c\u043d\u043e\u0441\u0442\u044c",
  "nat_desc": "\u041e\u0442\u043e\u0431\u0440\u0430\u0436\u0430\u0435\u0442\u0441\u044f \u0441 \u0444\u043b\u0430\u0433\u043e\u043c.",
  "none_selected": "\u041d\u0435 \u0432\u044b\u0431\u0440\u0430\u043d\u043e",
  "languages": "\u042f\u0437\u044b\u043a\u0438",
  "lang_desc": "\u041d\u0430 \u043a\u0430\u043a\u0438\u0445 \u044f\u0437\u044b\u043a\u0430\u0445 \u0432\u044b \u0433\u043e\u0432\u043e\u0440\u0438\u0442\u0435?",
  "profile_pic": "\u0424\u043e\u0442\u043e \u043f\u0440\u043e\u0444\u0438\u043b\u044f",
  "pic_desc": "PNG, JPG, GIF \u0438\u043b\u0438 WebP. \u041c\u0430\u043a\u0441. 5 \u041c\u0411.",
  "upload": "\u0417\u0430\u0433\u0440\u0443\u0437\u0438\u0442\u044c",
  "remove_pic": "\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u0444\u043e\u0442\u043e",
  "two_factor": "\u0414\u0432\u0443\u0445\u0444\u0430\u043a\u0442\u043e\u0440\u043d\u0430\u044f (2FA)",
  "two_factor_desc": "\u0417\u0430\u0449\u0438\u0442\u0438\u0442\u0435 \u0430\u043a\u043a\u0430\u0443\u043d\u0442.",
  "enabled": "\u0412\u043a\u043b\u044e\u0447\u0435\u043d\u043e",
  "not_enabled": "\u041d\u0435 \u0432\u043a\u043b\u044e\u0447\u0435\u043d\u043e",
  "activate": "\u0410\u043a\u0442\u0438\u0432\u0438\u0440\u043e\u0432\u0430\u0442\u044c",
  "sessions": "\u0410\u043a\u0442\u0438\u0432\u043d\u044b\u0435 \u0441\u0435\u0441\u0441\u0438\u0438",
  "sessions_desc": "\u0423\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u0430, \u0432\u043e\u0448\u0435\u0434\u0448\u0438\u0435 \u0432 \u0430\u043a\u043a\u0430\u0443\u043d\u0442.",
  "logout_all": "\u0412\u044b\u0439\u0442\u0438 \u043e\u0442\u043e\u0432\u0441\u044e\u0434\u0443",
  "account": "\u0410\u043a\u043a\u0430\u0443\u043d\u0442",
  "account_desc": "\u041f\u0430\u0440\u043e\u043b\u044c \u0438 \u0431\u0435\u0437\u043e\u043f\u0430\u0441\u043d\u043e\u0441\u0442\u044c.",
  "change_pw": "\u0421\u043c\u0435\u043d\u0438\u0442\u044c \u043f\u0430\u0440\u043e\u043b\u044c",
  "freeze": "\u0417\u0430\u043c\u043e\u0440\u043e\u0437\u0438\u0442\u044c 24\u0447",
  "member_since": "\u0423\u0447\u0430\u0441\u0442\u043d\u0438\u043a"
};

// Auto-apply on load
if (document.readyState === "complete" || document.readyState === "interactive") {
  setTimeout(function() { I18N.apply(); }, 100);
} else {
  document.addEventListener("DOMContentLoaded", function() { I18N.apply(); });
}

// Extra keys for index.html
I18N.t["sv"]["menu"] = "Meny";
I18N.t["sv"]["quick_nav"] = "Snabbnavigering";
I18N.t["sv"]["latest_news"] = "Senaste nytt";
I18N.t["sv"]["search_users"] = "S\u00f6k anv\u00e4ndare";
I18N.t["sv"]["logout"] = "Logga ut";
I18N.t["en"]["menu"] = "Menu";
I18N.t["en"]["quick_nav"] = "Quick navigation";
I18N.t["en"]["latest_news"] = "Latest news";
I18N.t["en"]["search_users"] = "Search users";
I18N.t["en"]["logout"] = "Log out";
I18N.t["fr"]["menu"] = "Menu";
I18N.t["fr"]["quick_nav"] = "Navigation rapide";
I18N.t["fr"]["latest_news"] = "Derni\u00e8res nouvelles";
I18N.t["fr"]["search_users"] = "Rechercher des utilisateurs";
I18N.t["fr"]["logout"] = "D\u00e9connexion";
I18N.t["ru"]["menu"] = "\u041c\u0435\u043d\u044e";
I18N.t["ru"]["quick_nav"] = "\u0411\u044b\u0441\u0442\u0440\u0430\u044f \u043d\u0430\u0432\u0438\u0433\u0430\u0446\u0438\u044f";
I18N.t["ru"]["latest_news"] = "\u041f\u043e\u0441\u043b\u0435\u0434\u043d\u0438\u0435 \u043d\u043e\u0432\u043e\u0441\u0442\u0438";
I18N.t["ru"]["search_users"] = "\u041f\u043e\u0438\u0441\u043a \u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u0435\u0439";
I18N.t["ru"]["logout"] = "\u0412\u044b\u0439\u0442\u0438";

// Extra keys
I18N.t["sv"]["role"]="Roll";I18N.t["sv"]["age"]="Ålder";I18N.t["sv"]["search_stat"]="Sökning";I18N.t["sv"]["session"]="Session";
I18N.t["sv"]["edit_profile"]="Redigera din profil";I18N.t["sv"]["open_messages"]="Öppna meddelanden";
I18N.t["sv"]["account_prefs"]="Konto & preferenser";I18N.t["sv"]["latest_changes"]="Senaste ändringarna";
I18N.t["sv"]["patch_notes"]="Patch notes";I18N.t["sv"]["type_to_search"]="Skriv för att söka.";
I18N.t["sv"]["no_results"]="Inga resultat.";I18N.t["sv"]["created"]="Skapad";I18N.t["sv"]["status"]="Status";
I18N.t["sv"]["active"]="Aktiv";I18N.t["sv"]["ready"]="Redo";I18N.t["sv"]["searching"]="Söker";
I18N.t["sv"]["view"]="Visa";I18N.t["sv"]["message"]="Skriv";I18N.t["sv"]["search_placeholder"]="Användarnamn...";
I18N.t["sv"]["top_search_placeholder"]="Sök användare...";

I18N.t["en"]["role"]="Role";I18N.t["en"]["age"]="Age";I18N.t["en"]["search_stat"]="Search";I18N.t["en"]["session"]="Session";
I18N.t["en"]["edit_profile"]="Edit your profile";I18N.t["en"]["open_messages"]="Open messages";
I18N.t["en"]["account_prefs"]="Account & preferences";I18N.t["en"]["latest_changes"]="Latest changes";
I18N.t["en"]["patch_notes"]="Patch notes";I18N.t["en"]["type_to_search"]="Type to search.";
I18N.t["en"]["no_results"]="No results.";I18N.t["en"]["created"]="Created";I18N.t["en"]["status"]="Status";
I18N.t["en"]["active"]="Active";I18N.t["en"]["ready"]="Ready";I18N.t["en"]["searching"]="Searching";
I18N.t["en"]["view"]="View";I18N.t["en"]["message"]="Message";I18N.t["en"]["search_placeholder"]="Username...";
I18N.t["en"]["top_search_placeholder"]="Search users...";

I18N.t["fr"]["role"]="R\u00f4le";I18N.t["fr"]["age"]="\u00c2ge";I18N.t["fr"]["search_stat"]="Recherche";I18N.t["fr"]["session"]="Session";
I18N.t["fr"]["edit_profile"]="Modifier votre profil";I18N.t["fr"]["open_messages"]="Ouvrir les messages";
I18N.t["fr"]["account_prefs"]="Compte & pr\u00e9f\u00e9rences";I18N.t["fr"]["latest_changes"]="Derni\u00e8res modifications";
I18N.t["fr"]["patch_notes"]="Notes de mise \u00e0 jour";I18N.t["fr"]["type_to_search"]="Tapez pour chercher.";
I18N.t["fr"]["no_results"]="Aucun r\u00e9sultat.";I18N.t["fr"]["created"]="Cr\u00e9\u00e9";I18N.t["fr"]["status"]="Statut";
I18N.t["fr"]["active"]="Actif";I18N.t["fr"]["ready"]="Pr\u00eat";I18N.t["fr"]["searching"]="Recherche";
I18N.t["fr"]["view"]="Voir";I18N.t["fr"]["message"]="Message";I18N.t["fr"]["search_placeholder"]="Nom d'utilisateur...";
I18N.t["fr"]["top_search_placeholder"]="Rechercher...";

I18N.t["ru"]["role"]="\u0420\u043e\u043b\u044c";I18N.t["ru"]["age"]="\u0412\u043e\u0437\u0440\u0430\u0441\u0442";I18N.t["ru"]["search_stat"]="\u041f\u043e\u0438\u0441\u043a";I18N.t["ru"]["session"]="\u0421\u0435\u0441\u0441\u0438\u044f";
I18N.t["ru"]["edit_profile"]="\u0420\u0435\u0434\u0430\u043a\u0442\u0438\u0440\u043e\u0432\u0430\u0442\u044c \u043f\u0440\u043e\u0444\u0438\u043b\u044c";I18N.t["ru"]["open_messages"]="\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0441\u043e\u043e\u0431\u0449\u0435\u043d\u0438\u044f";
I18N.t["ru"]["account_prefs"]="\u0410\u043a\u043a\u0430\u0443\u043d\u0442 \u0438 \u043d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438";I18N.t["ru"]["latest_changes"]="\u041f\u043e\u0441\u043b\u0435\u0434\u043d\u0438\u0435 \u0438\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f";
I18N.t["ru"]["patch_notes"]="\u041e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u044f";I18N.t["ru"]["type_to_search"]="\u041d\u0430\u0447\u043d\u0438\u0442\u0435 \u0432\u0432\u043e\u0434\u0438\u0442\u044c.";
I18N.t["ru"]["no_results"]="\u041d\u0435\u0442 \u0440\u0435\u0437\u0443\u043b\u044c\u0442\u0430\u0442\u043e\u0432.";I18N.t["ru"]["created"]="\u0421\u043e\u0437\u0434\u0430\u043d";I18N.t["ru"]["status"]="\u0421\u0442\u0430\u0442\u0443\u0441";
I18N.t["ru"]["active"]="\u0410\u043a\u0442\u0438\u0432\u043d\u043e";I18N.t["ru"]["ready"]="\u0413\u043e\u0442\u043e\u0432\u043e";I18N.t["ru"]["searching"]="\u041f\u043e\u0438\u0441\u043a";
I18N.t["ru"]["view"]="\u041f\u0440\u043e\u0444\u0438\u043b\u044c";I18N.t["ru"]["message"]="\u041d\u0430\u043f\u0438\u0441\u0430\u0442\u044c";I18N.t["ru"]["search_placeholder"]="\u0418\u043c\u044f \u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u044f...";
I18N.t["ru"]["top_search_placeholder"]="\u041f\u043e\u0438\u0441\u043a \u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u0435\u0439...";

// Override I18N.apply to also update index.html dynamic elements
var _origApply = I18N.apply;
I18N.apply = function() {
  _origApply();
  // Stat labels
  document.querySelectorAll(".stat-label").forEach(function(el) {
    var t = el.textContent.trim().toUpperCase();
    if (t === "ROLL" || t === "ROLE" || t === "R\u00d4LE" || t === "\u0420\u041e\u041b\u042c") el.textContent = I18N.get("role").toUpperCase();
    if (t === "\u00c5LDER" || t === "AGE" || t === "\u00c2GE" || t === "\u0412\u041e\u0417\u0420\u0410\u0421\u0422") el.textContent = I18N.get("age").toUpperCase();
    if (t === "S\u00d6KNING" || t === "SEARCH" || t === "RECHERCHE" || t === "\u041f\u041e\u0418\u0421\u041a") el.textContent = I18N.get("search_stat").toUpperCase();
    if (t === "SESSION" || t === "\u0421\u0415\u0421\u0421\u0418\u042f") el.textContent = I18N.get("session").toUpperCase();
  });
  // Quick grid descriptions
  document.querySelectorAll(".quick-item span").forEach(function(el) {
    var p = el.parentElement;
    if (!p) return;
    var href = p.getAttribute("href") || "";
    if (href.indexOf("profile") >= 0) el.textContent = I18N.get("edit_profile");
    if (href.indexOf("chatt") >= 0) el.textContent = I18N.get("open_messages");
    if (href.indexOf("settings") >= 0) el.textContent = I18N.get("account_prefs");
    if (href.indexOf("patch") >= 0) el.textContent = I18N.get("latest_changes");
  });
  // Quick grid titles
  document.querySelectorAll(".quick-item").forEach(function(el) {
    var href = el.getAttribute("href") || "";
    var first = el.childNodes[0];
    if (!first || first.nodeType !== 3) return;
    if (href.indexOf("settings") >= 0) first.textContent = I18N.get("settings");
    if (href.indexOf("patch") >= 0) first.textContent = I18N.get("patch_notes");
  });
  // Search placeholders
  var si = document.getElementById("searchInput");
  if (si) si.placeholder = I18N.get("search_placeholder");
  var ts = document.getElementById("topSearchInput");
  if (ts) ts.placeholder = I18N.get("top_search_placeholder");
  // Search empty text
  var se = document.getElementById("searchEmpty");
  if (se && se.classList.contains("search-empty")) {
    var t = se.textContent.trim();
    if (t) se.textContent = I18N.get("type_to_search");
  }
  // Search button
  var sb = document.getElementById("searchBtn");
  if (sb) sb.textContent = I18N.get("search");
  if(typeof window._refreshDashNews==="function")window._refreshDashNews();
};
I18N.t["sv"]["open_chat"]="Öppna";
I18N.t["en"]["open_chat"]="Open";
I18N.t["fr"]["open_chat"]="Ouvrir";
I18N.t["ru"]["open_chat"]="\u041e\u0442\u043a\u0440.";
