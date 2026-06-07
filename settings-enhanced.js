// === RunSpace Enhanced Settings v2 ===
(function(){
var SK={fontSize:"runspace_font_size",msgLayout:"runspace_msg_layout",showOnline:"runspace_show_online",soundActive:"runspace_active_sound",soundVol:"runspace_notis_volume",customSounds:"runspace_custom_sounds",chatBubbleStyle:"runspace_bubble_style"};
var BUILTIN=[{id:"default",name:"Standard"},{id:"soft",name:"Mjuk ping"},{id:"chime",name:"Klockspel"},{id:"pop",name:"Pop"},{id:"bell",name:"Klocka"},{id:"drop",name:"Droppe"},{id:"none",name:"Ingen"}];

function go(){injectSoundUI();initTabs();loadSavedToggles()}
if(document.readyState==="loading")document.addEventListener("DOMContentLoaded",go);else setTimeout(go,150);

function injectSoundUI(){
  var tab=document.getElementById("tab-notis");if(!tab||tab.querySelector("#notifSoundSection"))return;
  var s=document.createElement("div");s.id="notifSoundSection";s.style.cssText="margin-top:20px;padding-top:16px;border-top:1px solid var(--border)";
  s.innerHTML='<div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:12px">Notisljud</div><div style="display:flex;align-items:center;gap:10px;margin-bottom:14px"><label style="font-size:11px;color:var(--dim);width:55px;flex-shrink:0">Volym</label><input type="range" id="notisVolumeSlider" min="0" max="100" value="50" style="flex:1;accent-color:var(--accent)"><span id="notisVolumeLabel" style="font-size:11px;color:var(--muted);width:32px;text-align:right">50%</span></div><div id="notifSoundList" style="margin-bottom:14px"></div><div style="display:flex;gap:6px;align-items:center"><button class="btn-secondary" style="font-size:11px;padding:5px 10px" id="uploadSoundBtn">Ladda upp ljud</button><span style="font-size:10px;color:var(--muted)">MP3, WAV, OGG — max 1 MB</span></div><input type="file" id="soundUploadInput" accept=".mp3,.wav,.ogg" style="display:none">';
  tab.appendChild(s);
}

function initTabs(){
  var nav=document.querySelector(".settings-nav"),content=document.querySelector(".settings-content");if(!nav||!content)return;
  var notis=nav.querySelector('[onclick*="tab-notis"]');if(!notis)return;
  notis.insertAdjacentHTML("afterend",['<div class="settings-nav-item" onclick="showTab(this,\'tab-profile\')">Profil</div>','<div class="settings-nav-item" onclick="showTab(this,\'tab-account\')">Konto & Säkerhet</div>','<div class="settings-nav-item" onclick="showTab(this,\'tab-appearance\')">Utseende</div>','<div class="settings-nav-item" onclick="showTab(this,\'tab-chat\')">Chatt</div>','<div class="settings-nav-item" onclick="showTab(this,\'tab-accessibility\')">Tillgänglighet</div>','<div class="settings-nav-item" onclick="showTab(this,\'tab-privacy\')">Sekretess</div>','<div class="settings-nav-item" onclick="showTab(this,\'tab-advanced\')">Avancerat</div>'].join(""));
  content.insertAdjacentHTML("beforeend",buildTabs());

  // Nationality
  var ns=document.getElementById("settingsNationality");if(ns)["SE:Sverige","NO:Norge","DK:Danmark","FI:Finland","DE:Tyskland","US:USA","GB:Storbritannien","RU:Ryssland","FR:Frankrike","ES:Spanien","IT:Italien","PL:Polen","NL:Nederländerna","BR:Brasilien","JP:Japan","KR:Sydkorea","CN:Kina","IN:Indien","AU:Australien","CA:Kanada","UA:Ukraina","TR:Turkiet","MX:Mexiko","TH:Thailand","VN:Vietnam","PH:Filippinerna"].forEach(function(c){var p=c.split(":"),o=document.createElement("option");o.value=p[0];o.textContent=p[1];ns.appendChild(o)});

  // Bio counter
  var bio=document.getElementById("settingsBio");if(bio)bio.addEventListener("input",function(){var c=document.getElementById("settingsBioCount");if(c)c.textContent=bio.value.length});

  // Avatar
  var avIn=document.getElementById("settingsAvatarInput");if(avIn)avIn.addEventListener("change",async function(){var f=avIn.files[0];if(!f)return;if(f.size>5e6){alert("Max 5 MB.");return}var fd=new FormData();fd.append("avatar",f);try{var r=await fetch("/api/profile/avatar/upload",{method:"POST",credentials:"include",body:fd});var d=await r.json();if(r.ok&&d.avatarUrl){document.getElementById("settingsAvatar").innerHTML='<img src="'+d.avatarUrl+'" style="width:100%;height:100%;object-fit:cover;border-radius:50%">';ss("profileSaveStatus","Avatar uppdaterad!","var(--success)")}else alert(d.error||"Fel.")}catch(e){alert("Fel: "+e.message)}});

  // Sound upload
  var ub=document.getElementById("uploadSoundBtn"),ui=document.getElementById("soundUploadInput");
  if(ub&&ui){ub.onclick=function(){ui.click()};ui.onchange=function(){if(ui.files[0])handleSoundUpload(ui.files[0]);ui.value=""}}

  // Volume slider
  var vs=document.getElementById("notisVolumeSlider"),vl=document.getElementById("notisVolumeLabel");
  if(vs&&vl){var sv=Math.round(gVol()*100);vs.value=sv;vl.textContent=sv+"%";vs.oninput=function(){vl.textContent=vs.value+"%";sVol(parseInt(vs.value)/100)}}

  // Hook showTab
  var _st=window.showTab;window.showTab=function(n,t){_st(n,t);
    if(t==="tab-profile")loadProfile();if(t==="tab-account"){load2FA();loadSessions();loadHistory()}
    if(t==="tab-privacy")loadKeys();if(t==="tab-notis")renderSounds();if(t==="tab-advanced")loadAdvanced()};

  renderSounds();
}

function TR(l,d,c){return'<div class="toggle-row"><div><div class="toggle-label">'+l+'</div><div class="toggle-desc">'+d+'</div></div>'+c+'</div>'}
function TG(id){return'<div class="toggle" id="'+id+'" onclick="toggleSetting(this,\''+id.replace("toggle","").replace(/^./,function(c){return c.toLowerCase()})+'\')"></div>'}
function TGon(id){return'<div class="toggle on" id="'+id+'" onclick="toggleSetting(this,\''+id.replace("toggle","").replace(/^./,function(c){return c.toLowerCase()})+'\')"></div>'}

function buildTabs(){return[
// Profile
'<div id="tab-profile" style="display:none"><h3>Profil</h3><div class="form-field"><label class="form-label">Avatar</label><div style="display:flex;align-items:center;gap:12px"><div id="settingsAvatar" style="width:64px;height:64px;border-radius:50%;background:var(--surface-3);display:flex;align-items:center;justify-content:center;font-size:24px;font-weight:800;color:var(--dim);overflow:hidden;flex-shrink:0"></div><div><button class="btn" style="font-size:11px;padding:6px 12px" onclick="document.getElementById(\'settingsAvatarInput\').click()">Byt avatar</button><input type="file" id="settingsAvatarInput" accept=".png,.jpg,.jpeg,.gif,.webp" style="display:none"><div class="form-hint">Max 5 MB</div></div></div></div><div class="form-field"><label class="form-label">Bio</label><textarea class="form-textarea" id="settingsBio" placeholder="Berätta om dig själv..." maxlength="500" style="min-height:80px"></textarea><div class="form-hint"><span id="settingsBioCount">0</span>/500</div></div><div class="form-field"><label class="form-label">Nationalitet</label><select class="form-select" id="settingsNationality"><option value="">Välj...</option></select></div><div class="form-field"><label class="form-label">Språk</label><input class="form-input" id="settingsLanguages" placeholder="t.ex. Svenska, Engelska" maxlength="50"></div><button class="btn" onclick="saveProfileSettings()">Spara profil</button><div id="profileSaveStatus" style="font-size:11px;margin-top:6px;display:none"></div></div>',
// Account
'<div id="tab-account" style="display:none"><h3>Konto & Säkerhet</h3><div style="margin-bottom:20px"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Ändra lösenord</div><div class="form-field"><input class="form-input" type="password" id="settingsOldPw" placeholder="Nuvarande lösenord"></div><div class="form-field"><input class="form-input" type="password" id="settingsNewPw" placeholder="Nytt lösenord"></div><div class="form-field"><input class="form-input" type="password" id="settingsNewPw2" placeholder="Bekräfta nytt lösenord"></div><button class="btn" onclick="changePassword()">Ändra lösenord</button><div id="pwChangeStatus" style="font-size:11px;margin-top:6px;display:none"></div></div><div style="margin-bottom:20px;padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Tvåfaktorsautentisering (2FA)</div><div id="twoFaStatus" style="display:flex;align-items:center;gap:8px;margin-bottom:8px"><div id="twoFaIndicator" style="width:8px;height:8px;border-radius:50%;background:var(--muted)"></div><span id="twoFaLabel" style="font-size:12px;color:var(--muted)">Laddar...</span></div><div id="twoFaActions"></div></div><div style="margin-bottom:20px;padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Aktiva sessioner</div><div id="sessionsList" style="font-size:11px;color:var(--muted)">Laddar...</div><button class="btn-secondary" style="margin-top:8px;font-size:11px;padding:5px 10px" onclick="logoutAllSessions()">Logga ut alla enheter</button></div><div style="padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Senaste inloggningar</div><div id="loginHistoryList" style="font-size:11px;color:var(--muted);max-height:200px;overflow-y:auto">Laddar...</div></div></div>',
// Appearance
'<div id="tab-appearance" style="display:none"><h3>Utseende</h3>'+TR("Textstorlek","Storlek på meddelandetext",'<select class="form-select" id="settingsFontSize" style="width:auto;padding:5px 10px;font-size:11px" onchange="applyFontSize()"><option value="12">Liten</option><option value="13.5" selected>Normal</option><option value="15">Stor</option><option value="16">Extra stor</option></select>')+TR("Kompakt läge","Mindre mellanrum",TG("toggleCompact"))+TR("Visa tidsstämplar","Tid på varje meddelande",TGon("toggleTimestamps"))+TR("Bubbel-stil","Form på meddelandebubblor",'<select class="form-select" id="settingsBubbleStyle" style="width:auto;padding:5px 10px;font-size:11px" onchange="applyBubbleStyle()"><option value="rounded">Avrundade</option><option value="square">Kantiga</option><option value="minimal">Minimal</option></select>')+TR("Visa avatarer","Profilbilder i meddelanden",TGon("toggleAvatars"))+'<div style="margin-top:16px;padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Språk</div><select class="form-select" id="settingsUiLang" style="width:auto;padding:5px 10px;font-size:12px" onchange="changeUiLanguage()"><option value="sv">🇸🇪 Svenska</option><option value="en">🇬🇧 English</option><option value="fr">🇫🇷 Français</option><option value="ru">🇷🇺 Русский</option></select></div></div>',
// Chat
'<div id="tab-chat" style="display:none"><h3>Chatt</h3>'+TR("Enter skickar","Enter = skicka, Shift+Enter = ny rad",TGon("toggleEnterSend"))+TR("Emoji-storlek","Storlek på emojis",'<select class="form-select" id="settingsEmojiSize" style="width:auto;padding:5px 10px;font-size:11px" onchange="applyEmojiSize()"><option value="16">Liten</option><option value="20" selected>Normal</option><option value="28">Stor</option><option value="36">Extra stor</option></select>')+TR("Länkförhandsvisning","Klickbara länkar",TGon("toggleLinkPreview"))+TR("Ljud vid skickat","Ljud när du skickar",TG("toggleSendSound"))+TR("Skriv-indikator","Visa att du skriver",TGon("toggleTypingIndicator"))+'<div style="margin-top:16px;padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--danger);margin-bottom:8px">Radera</div><button class="btn-secondary" style="color:var(--danger);border-color:rgba(239,68,68,.25);font-size:11px;padding:5px 10px" onclick="clearLocalChatData()">Rensa lokal chattdata</button><div class="form-hint">Raderar lokalt cachad historik.</div></div></div>',
// Accessibility
'<div id="tab-accessibility" style="display:none"><h3>Tillgänglighet</h3>'+TR("Reducera animationer","Stäng av rörelser",TG("toggleReduceMotion"))+TR("Hög kontrast","Bättre läsbarhet",TG("toggleHighContrast"))+TR("Större klickyta","Större knappar",TG("toggleLargeTargets"))+TR("Dyslexi-typsnitt","OpenDyslexic-font",TG("toggleDyslexia"))+'</div>',
// Privacy
'<div id="tab-privacy" style="display:none"><h3>Sekretess</h3>'+TR("Visa online-status","Andra ser när du är online",TGon("toggleOnline"))+TR("Läsbekräftelser","Visa när du läst",TGon("toggleReadReceipts"))+'<div style="margin-top:20px;padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Krypteringsnycklar</div><div id="deviceKeysList" style="font-size:11px;color:var(--muted);max-height:200px;overflow-y:auto">Laddar...</div></div><div style="margin-top:20px;padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--danger);margin-bottom:8px">Farozon</div><button class="btn-secondary" style="color:var(--danger);border-color:rgba(239,68,68,.25);font-size:11px;padding:6px 12px" onclick="freezeAccount()">Frys konto</button><div class="form-hint">Låser kontot temporärt.</div></div></div>',
// Advanced
'<div id="tab-advanced" style="display:none"><h3>Avancerat</h3><div style="margin-bottom:16px"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Enhetsinformation</div><div id="advancedDeviceInfo" style="font-family:monospace;font-size:11px;color:var(--muted);background:var(--bg);border:1px solid var(--border);border-radius:7px;padding:10px;line-height:1.8"></div></div>'+TR("Debug-läge","Extra loggar i konsolen",TG("toggleDebug"))+'<div style="margin-top:16px;padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Exportera</div><button class="btn-secondary" style="font-size:11px;padding:5px 10px;margin-right:6px" onclick="exportChatData()">Chatthistorik</button><button class="btn-secondary" style="font-size:11px;padding:5px 10px" onclick="exportSettings()">Inställningar</button></div><div style="margin-top:16px;padding-top:16px;border-top:1px solid var(--border)"><div style="font-size:12px;font-weight:700;color:var(--dim);margin-bottom:8px">Återställ</div><button class="btn-secondary" style="color:var(--warn);border-color:rgba(245,158,11,.25);font-size:11px;padding:5px 10px" onclick="resetAllSettings()">Återställ alla inställningar</button></div></div>'
].join("")}

// === SOUNDS ===
function gCS(){try{return JSON.parse(localStorage.getItem(SK.customSounds)||"[]")}catch(e){return[]}}
function sCS(a){localStorage.setItem(SK.customSounds,JSON.stringify(a.slice(0,10)))}
function gVol(){return parseFloat(localStorage.getItem(SK.soundVol)||"0.5")}
function sVol(v){localStorage.setItem(SK.soundVol,String(v))}
function gAS(){return localStorage.getItem(SK.soundActive)||"default"}
function sAS(id){localStorage.setItem(SK.soundActive,id)}

function playB(id,v){if(id==="none")return;var vol=typeof v==="number"?v:gVol();try{var c=new(window.AudioContext||window.webkitAudioContext)(),g=c.createGain();g.connect(c.destination);g.gain.setValueAtTime(vol*.3,c.currentTime);
  if(id==="default"){var o=c.createOscillator();o.connect(g);o.type="sine";o.frequency.setValueAtTime(880,c.currentTime);o.frequency.setValueAtTime(1200,c.currentTime+.08);g.gain.exponentialRampToValueAtTime(.001,c.currentTime+.25);o.start();o.stop(c.currentTime+.25)}
  else if(id==="soft"){var o=c.createOscillator();o.connect(g);o.type="sine";o.frequency.setValueAtTime(600,c.currentTime);o.frequency.exponentialRampToValueAtTime(800,c.currentTime+.15);g.gain.exponentialRampToValueAtTime(.001,c.currentTime+.4);o.start();o.stop(c.currentTime+.4)}
  else if(id==="chime"){[523,659,784].forEach(function(f,i){var o2=c.createOscillator(),g2=c.createGain();o2.connect(g2);g2.connect(c.destination);o2.type="sine";o2.frequency.value=f;g2.gain.setValueAtTime(vol*.2,c.currentTime+i*.1);g2.gain.exponentialRampToValueAtTime(.001,c.currentTime+i*.1+.3);o2.start(c.currentTime+i*.1);o2.stop(c.currentTime+i*.1+.3)})}
  else if(id==="pop"){var o=c.createOscillator();o.connect(g);o.type="sine";o.frequency.setValueAtTime(1400,c.currentTime);o.frequency.exponentialRampToValueAtTime(400,c.currentTime+.06);g.gain.exponentialRampToValueAtTime(.001,c.currentTime+.12);o.start();o.stop(c.currentTime+.12)}
  else if(id==="bell"){var o=c.createOscillator();o.connect(g);o.type="triangle";o.frequency.setValueAtTime(1200,c.currentTime);g.gain.exponentialRampToValueAtTime(.001,c.currentTime+.6);o.start();o.stop(c.currentTime+.6)}
  else if(id==="drop"){var o=c.createOscillator();o.connect(g);o.type="sine";o.frequency.setValueAtTime(1000,c.currentTime);o.frequency.exponentialRampToValueAtTime(200,c.currentTime+.15);g.gain.exponentialRampToValueAtTime(.001,c.currentTime+.3);o.start();o.stop(c.currentTime+.3)}
}catch(e){}}
function playC(data,v){try{var a=new Audio(data);a.volume=typeof v==="number"?v:gVol();a.play().catch(function(){})}catch(e){}}
function playActive(){var id=gAS();if(id==="none")return;var c=gCS().find(function(s){return s.id===id});if(c&&c.data)playC(c.data);else playB(id)}

function renderSounds(){
  var el=document.getElementById("notifSoundList");if(!el)return;
  var active=gAS(),customs=gCS(),all=BUILTIN.concat(customs);
  el.innerHTML=all.map(function(s){var a=s.id===active,c=!!s.data;
    return'<div style="display:flex;align-items:center;gap:8px;padding:6px 10px;border-radius:7px;border:1px solid '+(a?'rgba(59,130,246,.3)':'var(--border)')+';background:'+(a?'rgba(59,130,246,.06)':'rgba(255,255,255,.02)')+';cursor:pointer;margin-bottom:4px" data-sid="'+E(s.id)+'" class="sr">'+'<div style="width:16px;height:16px;border-radius:50%;border:2px solid '+(a?'var(--accent)':'var(--border)')+';display:flex;align-items:center;justify-content:center;flex-shrink:0">'+(a?'<div style="width:8px;height:8px;border-radius:50%;background:var(--accent)"></div>':'')+'</div>'+'<span style="flex:1;font-size:12px;font-weight:600;color:'+(a?'var(--text)':'var(--dim)')+'">'+E(s.name)+'</span>'+'<button class="sp" data-sid="'+E(s.id)+'" style="border:none;background:none;color:var(--muted);cursor:pointer;font-size:13px;padding:2px 4px;border-radius:4px" title="Testa">▶</button>'+(c?'<button class="sd" data-sid="'+E(s.id)+'" style="border:none;background:none;color:rgba(239,68,68,.5);cursor:pointer;font-size:11px;padding:2px 4px" title="Ta bort">✕</button>':'')+'</div>'}).join("");
  el.querySelectorAll(".sr").forEach(function(r){r.onclick=function(e){if(e.target.closest(".sp")||e.target.closest(".sd"))return;sAS(r.dataset.sid);renderSounds()}});
  el.querySelectorAll(".sp").forEach(function(b){b.onclick=function(e){e.stopPropagation();var c=customs.find(function(s){return s.id===b.dataset.sid});if(c&&c.data)playC(c.data);else playB(b.dataset.sid)}});
  el.querySelectorAll(".sd").forEach(function(b){b.onclick=function(e){e.stopPropagation();sCS(gCS().filter(function(s){return s.id!==b.dataset.sid}));if(gAS()===b.dataset.sid)sAS("default");renderSounds()}});
}

function handleSoundUpload(f){if(f.size>1048576){alert("Max 1 MB.");return}if(!f.name.match(/\.(mp3|wav|ogg)$/i)){alert("Bara MP3/WAV/OGG.");return}
  var r=new FileReader();r.onload=function(){var a=gCS();if(a.length>=10){alert("Max 10.");return}a.push({id:"c_"+Date.now(),name:f.name.replace(/\.[^.]+$/,"").slice(0,30),data:r.result});sCS(a);sAS(a[a.length-1].id);renderSounds();playC(r.result)};r.readAsDataURL(f)}

// === PROFILE ===
async function loadProfile(){try{var r=await fetch("/api/profile",{credentials:"include"});if(!r.ok)return;var d=await r.json();
  var b=document.getElementById("settingsBio");if(b){b.value=d.bio||"";document.getElementById("settingsBioCount").textContent=b.value.length}
  var n=document.getElementById("settingsNationality");if(n&&d.nationality)n.value=d.nationality;
  var l=document.getElementById("settingsLanguages");if(l)l.value=d.languages||"";
  var a=document.getElementById("settingsAvatar");if(a){if(d.avatarUrl)a.innerHTML='<img src="'+d.avatarUrl+'" style="width:100%;height:100%;object-fit:cover;border-radius:50%">';else if(typeof me!=="undefined"&&me&&me.username)a.textContent=me.username[0].toUpperCase()}}catch(e){}}

window.saveProfileSettings=async function(){try{var r=await fetch("/api/profile/update",{method:"POST",credentials:"include",headers:{"Content-Type":"application/json"},body:JSON.stringify({bio:(document.getElementById("settingsBio")||{}).value||"",nationality:(document.getElementById("settingsNationality")||{}).value||"",languages:(document.getElementById("settingsLanguages")||{}).value||""})});if(r.ok)ss("profileSaveStatus","Sparad!","var(--success)");else ss("profileSaveStatus","Fel.","var(--danger)")}catch(e){ss("profileSaveStatus","Fel.","var(--danger)")}};

// === PASSWORD / 2FA / SESSIONS / HISTORY ===
window.changePassword=async function(){var o=V("settingsOldPw"),p=V("settingsNewPw"),p2=V("settingsNewPw2");if(!o||!p){ss("pwChangeStatus","Fyll i fälten.","var(--danger)");return}if(p!==p2){ss("pwChangeStatus","Matchar inte.","var(--danger)");return}try{var r=await fetch("/api/auth/change-password",{method:"POST",credentials:"include",headers:{"Content-Type":"application/json"},body:JSON.stringify({oldPassword:o,newPassword:p})});var d=await r.json().catch(function(){return{}});if(r.ok){ss("pwChangeStatus","Ändrat!","var(--success)");setTimeout(function(){location.href="/login.html"},2000)}else ss("pwChangeStatus",d.message||"Fel.","var(--danger)")}catch(e){ss("pwChangeStatus","Fel.","var(--danger)")}};

async function load2FA(){try{var r=await fetch("/api/me",{credentials:"include"});if(!r.ok)return;var d=await r.json();var i=document.getElementById("twoFaIndicator"),l=document.getElementById("twoFaLabel"),a=document.getElementById("twoFaActions");
  if(d.twoFactorEnabled){i.style.background="var(--success)";l.textContent="Aktiverad";l.style.color="var(--success)";a.innerHTML='<button class="btn-secondary" style="font-size:11px;padding:5px 10px" onclick="disable2FA()">Inaktivera</button>'}
  else{i.style.background="var(--muted)";l.textContent="Ej aktiverad";l.style.color="var(--muted)";a.innerHTML='<button class="btn" style="font-size:11px;padding:5px 10px" onclick="setup2FA()">Aktivera</button>'}}catch(e){}}

window.setup2FA=async function(){try{var r=await fetch("/api/auth/2fa/setup",{method:"POST",credentials:"include"});var d=await r.json();if(!r.ok){alert(d.message||"Fel.");return}document.getElementById("twoFaActions").innerHTML='<div style="background:var(--surface-2);border:1px solid var(--border);border-radius:8px;padding:12px;margin-bottom:8px"><div style="font-size:11px;color:var(--dim);margin-bottom:6px">Nyckel:</div><div style="font-family:monospace;font-size:13px;color:var(--accent);word-break:break-all;margin-bottom:8px">'+d.secret+'</div><div style="font-size:10px;color:var(--muted);margin-bottom:4px">Backup-koder:</div><div style="font-family:monospace;font-size:11px;color:var(--warn);line-height:1.8">'+(d.backupCodes||[]).join(" ")+'</div></div><div style="display:flex;gap:6px"><input class="form-input" id="twoFaCode" placeholder="6-siffrig kod" style="width:140px;font-size:12px" maxlength="6"><button class="btn" style="font-size:11px;padding:5px 10px" onclick="verify2FA()">Verifiera</button></div>'}catch(e){alert("Fel.")}};
window.verify2FA=async function(){var c=V("twoFaCode");if(!c)return;try{var r=await fetch("/api/auth/2fa/verify",{method:"POST",credentials:"include",headers:{"Content-Type":"application/json"},body:JSON.stringify({code:c})});if(r.ok){alert("2FA aktiverad!");load2FA()}else alert("Ogiltig kod.")}catch(e){alert("Fel.")}};
window.disable2FA=async function(){var c=prompt("2FA-kod:");if(!c)return;try{var r=await fetch("/api/auth/2fa/disable",{method:"POST",credentials:"include",headers:{"Content-Type":"application/json"},body:JSON.stringify({code:c})});if(r.ok){alert("2FA av.");load2FA()}else alert("Ogiltig.")}catch(e){alert("Fel.")}};

async function loadSessions(){try{var r=await fetch("/api/auth/sessions",{credentials:"include"});if(!r.ok)return;var d=await r.json(),el=document.getElementById("sessionsList");if(!d.length){el.innerHTML="Inga.";return}el.innerHTML=d.map(function(s){var b=s.userAgent||"",br=b.indexOf("Firefox")>=0?"Firefox":b.indexOf("Chrome")>=0?"Chrome":b.indexOf("Safari")>=0?"Safari":"Okänd";return'<div style="display:flex;align-items:center;gap:8px;padding:6px 0;border-bottom:1px solid rgba(255,255,255,.04)"><div style="width:6px;height:6px;border-radius:50%;background:var(--success);flex-shrink:0"></div><div style="flex:1"><div style="font-size:11px;font-weight:600;color:var(--text)">'+br+'</div><div style="font-size:10px;color:var(--muted)">'+E(s.ip||"")+" — "+fA(s.lastActivity)+'</div></div></div>'}).join("")}catch(e){}}
window.logoutAllSessions=async function(){if(!confirm("Logga ut alla?"))return;try{await fetch("/api/auth/logout-all",{method:"POST",credentials:"include"});location.href="/login.html"}catch(e){alert("Fel.")}};

async function loadHistory(){try{var r=await fetch("/api/auth/login-history",{credentials:"include"});if(!r.ok)return;var d=await r.json(),el=document.getElementById("loginHistoryList");if(!d.length){el.innerHTML="Ingen.";return}el.innerHTML=d.slice(0,20).map(function(h){return'<div style="display:flex;align-items:center;gap:6px;padding:4px 0;border-bottom:1px solid rgba(255,255,255,.03)"><span style="color:'+(h.success?'var(--success)':'var(--danger)')+'">'+(h.success?"✓":"✕")+'</span><span style="flex:1">'+E(h.ip||"?")+'</span><span style="color:var(--muted);font-size:10px">'+fA(h.timestamp)+'</span></div>'}).join("")}catch(e){}}

// === KEYS / PRIVACY ===
async function loadKeys(){try{var r=await fetch("/api/chat/device-keys/me",{credentials:"include"});if(!r.ok)return;var d=await r.json(),keys=d.keys||[],el=document.getElementById("deviceKeysList");if(!keys.length){el.innerHTML="Inga.";return}el.innerHTML=keys.map(function(k){var cur=k.deviceId===(typeof devId!=="undefined"?devId:"");return'<div style="display:flex;align-items:center;gap:8px;padding:6px 0;border-bottom:1px solid rgba(255,255,255,.04)"><div style="flex:1"><div style="font-size:11px;font-weight:600;color:var(--text)">'+E(k.deviceName||k.deviceId)+(cur?' <span style="color:var(--accent);font-size:9px">(denna)</span>':'')+'</div><div style="font-size:10px;color:var(--muted)">'+fA(k.lastUsedAt)+'</div></div>'+(cur?'':'<button style="border:none;background:none;color:var(--danger);cursor:pointer;font-size:10px;padding:3px 6px;border-radius:4px;border:1px solid rgba(239,68,68,.2)" onclick="removeDeviceKey(\''+E(k.deviceId)+'\')">Ta bort</button>')+'</div>'}).join("")}catch(e){}}
window.removeDeviceKey=async function(d){if(!confirm("Ta bort?"))return;try{await fetch("/api/chat/device-key/"+encodeURIComponent(d),{method:"DELETE",credentials:"include"});loadKeys()}catch(e){}};
window.freezeAccount=async function(){var h=prompt("Timmar (1-720):","24");if(!h)return;h=parseInt(h);if(isNaN(h)||h<1)return;if(!confirm("Frysa "+h+"h?"))return;try{await fetch("/api/auth/freeze",{method:"POST",credentials:"include",headers:{"Content-Type":"application/json"},body:JSON.stringify({hours:h})});location.href="/login.html"}catch(e){}};

// === APPEARANCE / CHAT / ACCESSIBILITY ===
window.toggleSetting=function(el,k){el.classList.toggle("on");var on=el.classList.contains("on");localStorage.setItem("runspace_"+k,on?"1":"0");applyS(k,on)};
function applyS(k,on){var s;
  if(k==="compact"){s=gS("rsCompact");s.textContent=on?".msg-row{padding:0!important}.msg-row+.msg-row{margin-top:0!important}":""}
  if(k==="timestamps"){s=gS("rsTs");s.textContent=on?"":".msg-meta{display:none!important}"}
  if(k==="reduceMotion"){s=gS("rsMotion");s.textContent=on?"*,*::before,*::after{animation-duration:0s!important;transition-duration:0s!important}":""}
  if(k==="highContrast"){s=gS("rsContrast");s.textContent=on?":root{--text:#fff;--dim:#d1d5db;--muted:#9ca3af;--border:#4b5563}":""}
  if(k==="largeTargets"){s=gS("rsTargets");s.textContent=on?".btn,.btn-secondary,.btn-send,.up-btn,.chat-head-btn,.composer-file-btn{min-height:40px;min-width:40px;font-size:14px}":""}
  if(k==="dyslexia"){s=gS("rsDys");s.textContent=on?"@import url('https://fonts.cdnfonts.com/css/opendyslexic');*{font-family:'OpenDyslexic',sans-serif!important}":""}
}
window.applyFontSize=function(){var v=V("settingsFontSize")||"13.5";localStorage.setItem(SK.fontSize,v);gS("rsFont").textContent=".msg-bubble{font-size:"+v+"px!important}"};
window.applyBubbleStyle=function(){var v=V("settingsBubbleStyle")||"rounded";localStorage.setItem(SK.chatBubbleStyle,v);var s=gS("rsBubble");s.textContent=v==="square"?".msg-bubble{border-radius:4px!important}":v==="minimal"?".msg-bubble{border-radius:2px!important;border:none!important;background:transparent!important;padding:4px 0!important}.msg-row.mine .msg-bubble{color:var(--accent)!important}":""};
window.applyEmojiSize=function(){var v=V("settingsEmojiSize")||"20";localStorage.setItem("runspace_emoji_size",v);gS("rsEmoji").textContent=".reaction-chip{font-size:"+v+"px!important}.reaction-picker-btn{font-size:"+Math.round(v*1.2)+"px!important}"};
window.changeUiLanguage=function(){var v=V("settingsUiLang");if(v&&typeof I18N!=="undefined")I18N.set(v)};
window.clearLocalChatData=function(){if(!confirm("Rensa?"))return;var n=0;Object.keys(localStorage).filter(function(k){return k.startsWith("runspace_chat_")}).forEach(function(k){localStorage.removeItem(k);n++});alert("Raderat "+n+" nycklar.");location.reload()};
window.exportChatData=function(){if(typeof convos==="undefined")return;var d={};convos.forEach(function(m,p){d[p]=m});dl("runspace-chat.json",JSON.stringify(d,null,2))};
window.exportSettings=function(){var d={};Object.keys(SK).forEach(function(k){var v=localStorage.getItem(SK[k]);if(v)d[k]=v});dl("runspace-settings.json",JSON.stringify(d,null,2))};
window.resetAllSettings=function(){if(!confirm("Återställa allt?"))return;Object.values(SK).forEach(function(k){localStorage.removeItem(k)});["runspace_emoji_size","runspace_sendSound","runspace_enterSend","runspace_typingIndicator","runspace_compact","runspace_timestamps","runspace_reduceMotion","runspace_highContrast","runspace_largeTargets","runspace_dyslexia","runspace_debug","runspace_showOnline","runspace_readReceipts","runspace_linkPreview","runspace_avatars"].forEach(function(k){localStorage.removeItem(k)});document.querySelectorAll("style[id^='rs']").forEach(function(s){s.textContent=""});alert("Klart!");location.reload()};

function loadAdvanced(){var el=document.getElementById("advancedDeviceInfo");if(!el)return;el.innerHTML=["Device ID: "+(typeof devId!=="undefined"?devId:"?"),"Device Name: "+(typeof devName!=="undefined"?devName:"?"),"Browser: "+navigator.userAgent.slice(0,80),"Screen: "+screen.width+"×"+screen.height+" @"+devicePixelRatio+"x","Language: "+navigator.language,"Platform: "+navigator.platform,"Online: "+navigator.onLine,"SignalR: "+(typeof connection!=="undefined"?connection.state:"?"),"Conversations: "+(typeof convos!=="undefined"?convos.size:0),"LocalStorage: "+Object.keys(localStorage).filter(function(k){return k.startsWith("runspace")}).length+" keys"].join("<br>")}

function loadSavedToggles(){
  var map={compact:"toggleCompact",timestamps:"toggleTimestamps",reduceMotion:"toggleReduceMotion",highContrast:"toggleHighContrast",largeTargets:"toggleLargeTargets",dyslexia:"toggleDyslexia",debug:"toggleDebug",online:"toggleOnline",readReceipts:"toggleReadReceipts",enterSend:"toggleEnterSend",linkPreview:"toggleLinkPreview",typingIndicator:"toggleTypingIndicator",sendSound:"toggleSendSound",avatars:"toggleAvatars"};
  Object.keys(map).forEach(function(k){var v=localStorage.getItem("runspace_"+k),el=document.getElementById(map[k]);if(el&&v!==null){if(v==="1")el.classList.add("on");else el.classList.remove("on");applyS(k,v==="1")}});
  var fs=localStorage.getItem(SK.fontSize);if(fs){var sel=document.getElementById("settingsFontSize");if(sel){sel.value=fs}applyFontSize()}
  var bs=localStorage.getItem(SK.chatBubbleStyle);if(bs){var sel2=document.getElementById("settingsBubbleStyle");if(sel2)sel2.value=bs;applyBubbleStyle()}
  var es=localStorage.getItem("runspace_emoji_size");if(es){var sel3=document.getElementById("settingsEmojiSize");if(sel3)sel3.value=es;applyEmojiSize()}
  // Override playSound
  if(typeof window.playSound==="function"){window._origPS=window.playSound;window.playSound=function(){if(typeof soundEnabled!=="undefined"&&!soundEnabled)return;playActive()}}
}

// Helpers
function gS(id){var s=document.getElementById(id);if(!s){s=document.createElement("style");s.id=id;document.head.appendChild(s)}return s}
function ss(id,m,c){var el=document.getElementById(id);if(!el)return;el.textContent=m;el.style.color=c;el.style.display="";setTimeout(function(){el.style.display="none"},4000)}
function fA(t){if(!t)return"";var d=Date.now()-new Date(t).getTime();if(d<6e4)return"just nu";if(d<36e5)return Math.floor(d/6e4)+"m sedan";if(d<864e5)return Math.floor(d/36e5)+"h sedan";return Math.floor(d/864e5)+"d sedan"}
function E(s){return String(s||"").replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;")}
function V(id){return(document.getElementById(id)||{}).value||""}
function dl(n,d){var a=document.createElement("a");a.href="data:application/json;charset=utf-8,"+encodeURIComponent(d);a.download=n;a.click()}
})();
