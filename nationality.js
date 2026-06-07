// === RunSpace Nationality - ALL Countries ===
var ALL_COUNTRIES = [
["AF","Afghanistan"],["AX","\u00c5land"],["AL","Albanien"],["DZ","Algeriet"],["AS","Amerikanska Samoa"],
["AD","Andorra"],["AO","Angola"],["AI","Anguilla"],["AQ","Antarktis"],["AG","Antigua och Barbuda"],
["AR","Argentina"],["AM","Armenien"],["AW","Aruba"],["AU","Australien"],["AT","\u00d6sterrike"],
["AZ","Azerbajdzjan"],["BS","Bahamas"],["BH","Bahrain"],["BD","Bangladesh"],["BB","Barbados"],
["BY","Belarus"],["BE","Belgien"],["BZ","Belize"],["BJ","Benin"],["BM","Bermuda"],
["BT","Bhutan"],["BO","Bolivia"],["BQ","Bonaire"],["BA","Bosnien och Hercegovina"],["BW","Botswana"],
["BV","Bouvet\u00f6n"],["BR","Brasilien"],["IO","Brittiska Indiska oceanterritoriet"],["BN","Brunei"],["BG","Bulgarien"],
["BF","Burkina Faso"],["BI","Burundi"],["CV","Kap Verde"],["KH","Kambodja"],["CM","Kamerun"],
["CA","Kanada"],["KY","Cayman\u00f6arna"],["CF","Centralafrikanska republiken"],["TD","Tchad"],["CL","Chile"],
["CN","Kina"],["CX","Jul\u00f6n"],["CC","Kokos\u00f6arna"],["CO","Colombia"],["KM","Komorerna"],
["CG","Kongo-Brazzaville"],["CD","Kongo-Kinshasa"],["CK","Cook\u00f6arna"],["CR","Costa Rica"],["CI","Elfenbenskusten"],
["HR","Kroatien"],["CU","Kuba"],["CW","Cura\u00e7ao"],["CY","Cypern"],["CZ","Tjeckien"],
["DK","Danmark"],["DJ","Djibouti"],["DM","Dominica"],["DO","Dominikanska republiken"],["EC","Ecuador"],
["EG","Egypten"],["SV","El Salvador"],["GQ","Ekvatorialguinea"],["ER","Eritrea"],["EE","Estland"],
["SZ","Eswatini"],["ET","Etiopien"],["FK","Falklands\u00f6arna"],["FO","F\u00e4r\u00f6arna"],["FJ","Fiji"],
["FI","Finland"],["FR","Frankrike"],["GF","Franska Guyana"],["PF","Franska Polynesien"],["TF","Franska s\u00f6dra territorierna"],
["GA","Gabon"],["GM","Gambia"],["GE","Georgien"],["DE","Tyskland"],["GH","Ghana"],
["GI","Gibraltar"],["GR","Grekland"],["GL","Gr\u00f6nland"],["GD","Grenada"],["GP","Guadeloupe"],
["GU","Guam"],["GT","Guatemala"],["GG","Guernsey"],["GN","Guinea"],["GW","Guinea-Bissau"],
["GY","Guyana"],["HT","Haiti"],["HM","Heard\u00f6n och McDonald\u00f6arna"],["VA","Vatikanstaten"],["HN","Honduras"],
["HK","Hongkong"],["HU","Ungern"],["IS","Island"],["IN","Indien"],["ID","Indonesien"],
["IR","Iran"],["IQ","Irak"],["IE","Irland"],["IM","Isle of Man"],["IL","Israel"],
["IT","Italien"],["JM","Jamaica"],["JP","Japan"],["JE","Jersey"],["JO","Jordanien"],
["KZ","Kazakstan"],["KE","Kenya"],["KI","Kiribati"],["KP","Nordkorea"],["KR","Sydkorea"],
["KW","Kuwait"],["KG","Kirgizistan"],["LA","Laos"],["LV","Lettland"],["LB","Libanon"],
["LS","Lesotho"],["LR","Liberia"],["LY","Libyen"],["LI","Liechtenstein"],["LT","Litauen"],
["LU","Luxemburg"],["MO","Macao"],["MG","Madagaskar"],["MW","Malawi"],["MY","Malaysia"],
["MV","Maldiverna"],["ML","Mali"],["MT","Malta"],["MH","Marshall\u00f6arna"],["MQ","Martinique"],
["MR","Mauretanien"],["MU","Mauritius"],["YT","Mayotte"],["MX","Mexiko"],["FM","Mikronesien"],
["MD","Moldavien"],["MC","Monaco"],["MN","Mongoliet"],["ME","Montenegro"],["MS","Montserrat"],
["MA","Marocko"],["MZ","Mo\u00e7ambique"],["MM","Myanmar"],["NA","Namibia"],["NR","Nauru"],
["NP","Nepal"],["NL","Nederl\u00e4nderna"],["NC","Nya Kaledonien"],["NZ","Nya Zeeland"],["NI","Nicaragua"],
["NE","Niger"],["NG","Nigeria"],["NU","Niue"],["NF","Norfolk\u00f6n"],["MK","Nordmakedonien"],
["MP","Nordmarianerna"],["NO","Norge"],["OM","Oman"],["PK","Pakistan"],["PW","Palau"],
["PS","Palestina"],["PA","Panama"],["PG","Papua Nya Guinea"],["PY","Paraguay"],["PE","Peru"],
["PH","Filippinerna"],["PN","Pitcairn\u00f6arna"],["PL","Polen"],["PT","Portugal"],["PR","Puerto Rico"],
["QA","Qatar"],["RE","R\u00e9union"],["RO","Rum\u00e4nien"],["RU","Ryssland"],["RW","Rwanda"],
["BL","Saint Barth\u00e9lemy"],["SH","Saint Helena"],["KN","Saint Kitts och Nevis"],["LC","Saint Lucia"],
["MF","Saint Martin"],["PM","Saint Pierre och Miquelon"],["VC","Saint Vincent"],["WS","Samoa"],
["SM","San Marino"],["ST","S\u00e3o Tom\u00e9 och Pr\u00edncipe"],["SA","Saudiarabien"],["SN","Senegal"],
["RS","Serbien"],["SC","Seychellerna"],["SL","Sierra Leone"],["SG","Singapore"],["SX","Sint Maarten"],
["SK","Slovakien"],["SI","Slovenien"],["SB","Salomon\u00f6arna"],["SO","Somalia"],["ZA","Sydafrika"],
["GS","Sydgeorgien"],["SS","Sydsudan"],["ES","Spanien"],["LK","Sri Lanka"],["SD","Sudan"],
["SR","Surinam"],["SJ","Svalbard och Jan Mayen"],["SE","Sverige"],["CH","Schweiz"],["SY","Syrien"],
["TW","Taiwan"],["TJ","Tadzjikistan"],["TZ","Tanzania"],["TH","Thailand"],["TL","Timor-Leste"],
["TG","Togo"],["TK","Tokelau"],["TO","Tonga"],["TT","Trinidad och Tobago"],["TN","Tunisien"],
["TR","Turkiet"],["TM","Turkmenistan"],["TC","Turks- och Caicos\u00f6arna"],["TV","Tuvalu"],["UG","Uganda"],
["UA","Ukraina"],["AE","F\u00f6renade Arabemiraten"],["GB","Storbritannien"],["US","USA"],
["UM","USA:s yttre \u00f6ar"],["UY","Uruguay"],["UZ","Uzbekistan"],["VU","Vanuatu"],["VE","Venezuela"],
["VN","Vietnam"],["VG","Brittiska Jungfru\u00f6arna"],["VI","Amerikanska Jungfru\u00f6arna"],["WF","Wallis och Futuna"],
["EH","V\u00e4stsahara"],["YE","Jemen"],["ZM","Zambia"],["ZW","Zimbabwe"],["XK","Kosovo"]
];

function codeToFlag(code) {
  if (!code || code.length !== 2) return "";
  var a = 0x1F1E6;
  var c0 = code.toUpperCase().charCodeAt(0) - 65;
  var c1 = code.toUpperCase().charCodeAt(1) - 65;
  if (c0 < 0 || c0 > 25 || c1 < 0 || c1 > 25) return "";
  var cp0 = a + c0, cp1 = a + c1;
  function toSurr(cp) {
    var c = cp - 0x10000;
    return String.fromCharCode(0xD800 + (c >> 10), 0xDC00 + (c & 0x3FF));
  }
  return toSurr(cp0) + toSurr(cp1);
}

function getCountryName(code) {
  var found = ALL_COUNTRIES.find(function(c) { return c[0] === code; });
  return found ? found[1] : "";
}

function populateNatSelect(selId, currentVal) {
  var sel = document.getElementById(selId);
  if (!sel) return;
  sel.innerHTML = "";
  var def = document.createElement("option");
  def.value = "";
  def.textContent = "Ingen vald";
  sel.appendChild(def);
  ALL_COUNTRIES.forEach(function(c) {
    var opt = document.createElement("option");
    opt.value = c[0];
    opt.textContent = codeToFlag(c[0]) + " " + c[1];
    if (c[0] === currentVal) opt.selected = true;
    sel.appendChild(opt);
  });
}

async function saveNat() {
  var sel = document.getElementById("natSelect");
  if (!sel) return;
  var val = sel.value;
  var bioEl = document.getElementById("bioInput");
  var bio = bioEl ? bioEl.value.trim() : "";
  try {
    var r = await fetch("/api/profile/update", {
      method: "POST", credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ bio: bio, nationality: val })
    });
    if (!r.ok) { if (typeof toast === "function") toast("Kunde inte spara", "err"); else alert("Fel"); return; }
    if (typeof toast === "function") toast("Nationalitet sparad");
    var mn = document.getElementById("metaNat");
    if (mn) mn.textContent = val ? codeToFlag(val) + " " + getCountryName(val) : "";
  } catch(e) { if (typeof toast === "function") toast("N\u00e4tverksfel", "err"); else alert("N\u00e4tverksfel"); }
}

// Auto-init
(function() {
  function tryInit() {
    if (typeof me !== "undefined" && me) {
      populateNatSelect("natSelect", me.nationality || "");
      var mn = document.getElementById("metaNat");
      if (mn && me.nationality) mn.textContent = codeToFlag(me.nationality) + " " + getCountryName(me.nationality);
      var btn = document.getElementById("saveNatBtn");
      if (btn) btn.onclick = saveNat;
    } else {
      setTimeout(tryInit, 500);
    }
  }
  if (document.readyState === "complete") setTimeout(tryInit, 500);
  else document.addEventListener("DOMContentLoaded", function() { setTimeout(tryInit, 1500); });
})();
