(function(){
  var THEMES = {
    dark: {
      '--bg':'#080812','--bg-2':'#0d0d1a','--bg-3':'#111125','--bg-4':'#161630',
      '--bg-card':'#0d0d1a','--bg-hover':'#13131f','--bg2':'#0d0d1a','--bg3':'#111125','--bg4':'#161630',
      '--surface':'#0b0b14','--surface-2':'#0f0f1a','--surface-3':'#131320',
      '--text':'#e8e8f0','--text-2':'#8888a8','--text-3':'#55556a',
      '--text-primary':'#e8e8f0','--text-secondary':'#8888a8','--text-muted':'#55556a',
      '--border':'rgba(255,255,255,.06)','--border-2':'rgba(255,255,255,.10)','--border-hover':'rgba(255,255,255,.11)',
      '--muted':'#4e617a','--dim':'#7a90a8','--ghost':'rgba(255,255,255,.04)',
      '--line':'rgba(255,255,255,.06)','--line-soft':'rgba(255,255,255,.03)',
      '--primary':'#3b7dff','--accent':'#3b7dff','--accent-glow':'rgba(59,125,255,.18)',
      '--accent-hover':'#5590ff','--accent-soft':'rgba(59,125,255,.12)','--accent-subtle':'rgba(59,125,255,.06)',
      '--danger':'#ef4444','--danger-bg':'rgba(239,68,68,.08)','--danger-dim':'rgba(239,68,68,.08)','--danger-soft':'rgba(239,68,68,.12)',
      '--success':'#10b981','--warn':'#f59e0b','--warn-dim':'rgba(245,158,11,.10)',
      '--purple':'rgba(167,139,250,1)','--blue':'#3b7dff','--blue-dim':'rgba(59,125,255,.10)','--blue-glow':'rgba(59,125,255,.18)',
      '--status-online':'#10b981'
    },
    midnight: {
      '--bg':'#050510','--bg-2':'#08080f','--bg-3':'#0c0c18','--bg-4':'#101020',
      '--bg-card':'#08080f','--bg-hover':'#0d0d18','--bg2':'#08080f','--bg3':'#0c0c18','--bg4':'#101020',
      '--surface':'#080810','--surface-2':'#0c0c16','--surface-3':'#10101c',
      '--text':'#c8c8e8','--text-2':'#6666aa','--text-3':'#444466',
      '--text-primary':'#c8c8e8','--text-secondary':'#6666aa','--text-muted':'#444466',
      '--border':'rgba(255,255,255,.05)','--border-2':'rgba(255,255,255,.09)','--border-hover':'rgba(255,255,255,.09)',
      '--muted':'#444466','--dim':'#6666aa','--ghost':'rgba(255,255,255,.03)',
      '--line':'rgba(255,255,255,.05)','--line-soft':'rgba(255,255,255,.02)',
      '--primary':'#3b7dff','--accent':'#3b7dff','--accent-glow':'rgba(59,125,255,.15)',
      '--accent-hover':'#5590ff','--accent-soft':'rgba(59,125,255,.10)','--accent-subtle':'rgba(59,125,255,.05)',
      '--danger':'#ef4444','--danger-bg':'rgba(239,68,68,.08)','--danger-dim':'rgba(239,68,68,.08)','--danger-soft':'rgba(239,68,68,.10)',
      '--success':'#10b981','--warn':'#f59e0b','--warn-dim':'rgba(245,158,11,.08)',
      '--purple':'rgba(167,139,250,1)','--blue':'#3b7dff','--blue-dim':'rgba(59,125,255,.08)','--blue-glow':'rgba(59,125,255,.15)',
      '--status-online':'#10b981'
    },
    oled: {
      '--bg':'#000000','--bg-2':'#050505','--bg-3':'#0a0a0a','--bg-4':'#0f0f0f',
      '--bg-card':'#050505','--bg-hover':'#0a0a0a','--bg2':'#050505','--bg3':'#0a0a0a','--bg4':'#0f0f0f',
      '--surface':'#050505','--surface-2':'#080808','--surface-3':'#0c0c0c',
      '--text':'#ffffff','--text-2':'#888888','--text-3':'#444444',
      '--text-primary':'#ffffff','--text-secondary':'#888888','--text-muted':'#444444',
      '--border':'rgba(255,255,255,.08)','--border-2':'rgba(255,255,255,.12)','--border-hover':'rgba(255,255,255,.14)',
      '--muted':'#555555','--dim':'#888888','--ghost':'rgba(255,255,255,.05)',
      '--line':'rgba(255,255,255,.08)','--line-soft':'rgba(255,255,255,.04)',
      '--primary':'#3b7dff','--accent':'#3b7dff','--accent-glow':'rgba(59,125,255,.20)',
      '--accent-hover':'#5590ff','--accent-soft':'rgba(59,125,255,.14)','--accent-subtle':'rgba(59,125,255,.07)',
      '--danger':'#ef4444','--danger-bg':'rgba(239,68,68,.10)','--danger-dim':'rgba(239,68,68,.10)','--danger-soft':'rgba(239,68,68,.14)',
      '--success':'#10b981','--warn':'#f59e0b','--warn-dim':'rgba(245,158,11,.10)',
      '--purple':'rgba(167,139,250,1)','--blue':'#3b7dff','--blue-dim':'rgba(59,125,255,.10)','--blue-glow':'rgba(59,125,255,.20)',
      '--status-online':'#10b981'
    },
    light: {
      '--bg':'#f4f4f8','--bg-2':'#ebebf0','--bg-3':'#e0e0ea','--bg-4':'#d5d5e0',
      '--bg-card':'#ffffff','--bg-hover':'#e8e8f0','--bg2':'#ebebf0','--bg3':'#e0e0ea','--bg4':'#d5d5e0',
      '--surface':'#ffffff','--surface-2':'#f0f0f5','--surface-3':'#e8e8f0',
      '--text':'#111120','--text-2':'#555566','--text-3':'#999aaa',
      '--text-primary':'#111120','--text-secondary':'#555566','--text-muted':'#999aaa',
      '--border':'rgba(0,0,0,.08)','--border-2':'rgba(0,0,0,.12)','--border-hover':'rgba(0,0,0,.14)',
      '--muted':'#777788','--dim':'#555566','--ghost':'rgba(0,0,0,.03)',
      '--line':'rgba(0,0,0,.08)','--line-soft':'rgba(0,0,0,.04)',
      '--primary':'#3b7dff','--accent':'#3b7dff','--accent-glow':'rgba(59,125,255,.15)',
      '--accent-hover':'#2266ee','--accent-soft':'rgba(59,125,255,.10)','--accent-subtle':'rgba(59,125,255,.06)',
      '--danger':'#dc2626','--danger-bg':'rgba(220,38,38,.08)','--danger-dim':'rgba(220,38,38,.08)','--danger-soft':'rgba(220,38,38,.12)',
      '--success':'#059669','--warn':'#d97706','--warn-dim':'rgba(217,119,6,.10)',
      '--purple':'rgba(124,58,237,1)','--blue':'#3b7dff','--blue-dim':'rgba(59,125,255,.10)','--blue-glow':'rgba(59,125,255,.15)',
      '--status-online':'#059669'
    }
  };
  function applyTheme(t){
    var vars = THEMES[t] || THEMES.dark;
    var r = document.documentElement;
    Object.keys(vars).forEach(function(k){ r.style.setProperty(k, vars[k]); });
    r.setAttribute('data-theme', t);
  }
  var saved = localStorage.getItem('rs_theme') || 'dark';
  applyTheme(saved);
  window.__applyTheme = applyTheme;
})();
