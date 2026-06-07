// === RunSpace Group System ===
var myGroups=[],activeGroup=null,activeChannel=null,groupMessages=new Map();
var groupVoiceUsers={}; // key -> [usernames]
var myVoiceChannel=null; // channelId I'm currently in

async function loadMyGroups(){
  try{
    var r=await fetch('/api/groups',{credentials:'include'});
    if(!r.ok)return;
    myGroups=await r.json();
    renderServerList();
  }catch(e){console.error('Groups:',e)}
}

function renderServerList(){
  var sl=document.querySelector('.server-list');
  if(!sl)return;
  var h='<div class="srv '+(currentView==='dm'?'active':'')+'" id="srvDm" title="DM" onclick="switchView(\'dm\')">\ud83d\udcac</div><div class="srv-div"></div>';
  myGroups.forEach(function(g){
    var act=activeGroup&&activeGroup.groupId===g.groupId?'active':'';
    h+='<div class="srv '+act+'" title="'+esc(g.name)+'" onclick="activateGroup(\''+esc(g.groupId)+'\')">'+esc((g.name||'?')[0].toUpperCase())+'</div>';
  });
  h+='<div class="srv-div"></div><div class="srv add" title="Skapa grupp" onclick="openModal(\'createGroupModal\')">+</div>';
  sl.innerHTML=h;
}

async function activateGroup(gid){
  try{
    var r=await fetch('/api/groups/'+encodeURIComponent(gid),{credentials:'include'});
    if(!r.ok){alert('Kunde inte ladda gruppen.');return}
    activeGroup=await r.json();
    currentView='group';
    document.getElementById('dmSidebar').style.display='none';
    document.getElementById('groupSidebar').style.display='';
    renderServerList();
    renderGroupSidebar();
    var tc=(activeGroup.channels||[]).filter(function(c){return c.type==='text'});
    if(tc.length) activateChannel(tc[0].channelId);
    else el.messages.innerHTML='<div class="empty-state"><h2>Inga kanaler</h2><p>Skapa en textkanal.</p></div>';
    updateComposer();
  }catch(e){console.error(e);alert('Fel vid laddning av grupp.')}
}

function voiceKey(cid){
  return activeGroup?activeGroup.groupId+':'+cid:'';
}

function getVoiceUsers(cid){
  return groupVoiceUsers[voiceKey(cid)]||[];
}

function renderGroupSidebar(){
  if(!activeGroup)return;
  var gs=document.getElementById('groupSidebar');
  var h2=gs.querySelector('.sidebar-head h2');
  if(h2)h2.textContent=activeGroup.name||'Grupp';
  var sc=gs.querySelector('.sidebar-scroll');
  if(!sc)return;
  var tc=(activeGroup.channels||[]).filter(function(c){return c.type==='text'});
  var vc=(activeGroup.channels||[]).filter(function(c){return c.type==='voice'});
  var h='<div class="ch-cat">';
  h+='<div class="ch-cat-head" onclick="this.classList.toggle(\'collapsed\');this.nextElementSibling.style.display=this.classList.contains(\'collapsed\')?\'none\':\'\'"><span class="ch-cat-arrow">\u25be</span> Textkanaler</div><div>';
  tc.forEach(function(c){
    h+='<div class="ch-item'+(activeChannel===c.channelId?' active':'')+'" onclick="activateChannel(\''+esc(c.channelId)+'\')">';
    h+='<span class="ch-icon">#</span><span class="ch-name">'+esc(c.name)+'</span></div>';
  });
  h+='</div></div>';
  if(vc.length){
    h+='<div class="ch-cat">';
    h+='<div class="ch-cat-head" onclick="this.classList.toggle(\'collapsed\');this.nextElementSibling.style.display=this.classList.contains(\'collapsed\')?\'none\':\'\'"><span class="ch-cat-arrow">\u25be</span> R\u00f6stkanaler</div><div>';
    vc.forEach(function(c){
      var users=getVoiceUsers(c.channelId);
      var isMe=myVoiceChannel===c.channelId;
      h+='<div class="ch-item'+(isMe?' active':'')+'" onclick="toggleGroupVoice(\''+esc(c.channelId)+'\')">';
      h+='<span class="ch-icon">\ud83d\udd0a</span><span class="ch-name">'+esc(c.name)+'</span>';
      h+='<span style="font-size:10px;color:var(--muted)">'+users.length+'/6</span></div>';
      if(users.length){
        h+='<div class="vc-sub">';
        users.forEach(function(u){
          var spk=typeof GroupVoice!=='undefined'?GroupVoice.isUserSpeaking(u):false;
          var initial=(u||'?')[0].toUpperCase();
          h+='<div class="vc-sub-user'+(spk?' speaking':'')+'">';
          h+='<div style="width:20px;height:20px;border-radius:50%;background:var(--surface-3);display:flex;align-items:center;justify-content:center;font-size:9px;font-weight:700;color:var(--dim);flex-shrink:0;border:2px solid '+(spk?'var(--success)':'transparent')+';'+(spk?'box-shadow:0 0 6px rgba(34,197,94,.4)':'')+'">'+ esc(initial)+'</div>';
          h+='<span class="vc-sub-user-name" style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'+esc(u)+'</span>';
          var gvState=typeof GroupVoice!=='undefined'?GroupVoice.getState():{};
          if(u===me.username&&gvState.isMuted) h+='<span style="font-size:10px;flex-shrink:0">\ud83d\udd07</span>';
          h+='</div>';
        });
        h+='</div>';
      }
    });
    h+='</div></div>';
  }
  // Voice controls if in a channel
  if(myVoiceChannel){
    var gvs=typeof GroupVoice!=='undefined'?GroupVoice.getState():{};
    h+='<div style="padding:8px 10px;border-top:1px solid var(--border);margin-top:8px">';
    h+='<div style="font-size:11px;color:var(--success);font-weight:700;margin-bottom:6px">\ud83d\udd0a Ansluten till r\u00f6st</div>';
    h+='<div style="display:flex;gap:4px;margin-bottom:6px">';
    h+='<button class="vc-ctrl-btn'+(gvs.isMuted?' active':'')+'" onclick="GroupVoice.toggleMute()" title="Mikrofon">'+(gvs.isMuted?'\ud83d\udd07':'\ud83c\udf99\ufe0f')+'</button>';
    h+='<button class="vc-ctrl-btn'+(gvs.isDeafened?' active':'')+'" onclick="GroupVoice.toggleDeafen()" title="Ljud">'+(gvs.isDeafened?'\ud83d\udd15':'\ud83d\udd0a')+'</button>';
    h+='<button class="vc-ctrl-btn disconnect" onclick="GroupVoice.leave()" title="Koppla fr\u00e5n">\ud83d\udcde</button>';
    h+='</div>';
    h+='<details class="vc-details"><summary style="font-size:10px;color:var(--muted);cursor:pointer">Kvalitet: '+(gvs.qualityLabel||'Balanserad')+'</summary><div class="vc-quality-options" style="padding:4px 0">';
    var opts=typeof GroupVoice!=='undefined'?GroupVoice.getQualityOptions():[];
    opts.forEach(function(o){h+='<button class="vc-quality-btn'+(o.active?' active':'')+'" onclick="GroupVoice.setQuality(\''+o.id+'\')">'+o.label+'</button>'});
    h+='</div></details>';
    h+='</div>';
  }
  sc.innerHTML=h;
}

async function activateChannel(cid){
  if(!activeGroup)return;
  activeChannel=cid;
  renderGroupSidebar();
  var ch=(activeGroup.channels||[]).find(function(c){return c.channelId===cid});
  document.getElementById('chatIcon').textContent='#';
  document.getElementById('chatTitle').textContent=ch?ch.name:'kanal';
  var tp=document.getElementById('chatTopic');
  tp.textContent=activeGroup.name;
  tp.style.display='';
  if(connected){try{await connection.invoke('JoinGroupChannel',activeGroup.groupId,cid)}catch(e){}}
  try{
    var r=await fetch('/api/groups/'+encodeURIComponent(activeGroup.groupId)+'/channels/'+encodeURIComponent(cid)+'/history',{credentials:'include'});
    if(r.ok){groupMessages.set(cid,await r.json());renderGroupMessages(cid)}
  }catch(e){console.error(e)}
  updateComposer();
  setTimeout(function(){el.msg.focus()},0);
}

function renderGroupMessages(cid){
  var msgs=groupMessages.get(cid)||[];
  if(!msgs.length){
    el.messages.innerHTML='<div class="empty-state"><h2>Tom kanal</h2><p>Var f\u00f6rst med att skriva!</p></div>';
    return;
  }
  el.messages.innerHTML=msgs.map(function(m){
    var mine=m.from===me.username;
    var cls=mine?'mine':'other';
    return '<div class="msg-row '+cls+'"><div class="msg-meta">'+esc(mine?'Du':m.from)+' \u00b7 '+esc(fmtTime(m.ts))+'</div><div style="position:relative;display:inline-flex;align-items:center;'+(mine?'flex-direction:row-reverse':'')+'"><div class="msg-bubble">'+esc(m.text)+'</div></div></div>';
  }).join('');
  el.messages.scrollTop=el.messages.scrollHeight;
}

async function sendGroupMessage(){
  if(!activeGroup||!activeChannel||!connected)return;
  var text=el.msg.value.trim();
  if(!text||text.length>MAX_MSG_LEN)return;
  el.sendBtn.disabled=true;
  try{
    await connection.invoke('SendGroupMessage',activeGroup.groupId,activeChannel,text);
    el.msg.value='';
    updateCharCount();
  }catch(e){
    console.error(e);
    alert('Kunde inte skicka: '+(e.message||''));
  }finally{
    el.sendBtn.disabled=false;
    el.msg.focus();
  }
}

async function createGroup(){
  var ni=document.querySelector('#createGroupModal .form-input');
  var di=document.querySelector('#createGroupModal .form-textarea');
  var name=(ni?ni.value:'').trim();
  var desc=(di?di.value:'').trim();
  if(name.length<2){alert('Namn kr\u00e4vs (minst 2 tecken).');return}
  try{
    var r=await fetch('/api/groups',{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:name,description:desc})});
    var d=await r.json().catch(function(){return{}});
    if(!r.ok){alert(d.message||'Kunde inte skapa grupp.');return}
    closeModal('createGroupModal');
    if(ni)ni.value='';
    if(di)di.value='';
    await loadMyGroups();
    if(d.groupId) await activateGroup(d.groupId);
  }catch(e){alert('N\u00e4tverksfel.')}
}

async function toggleGroupVoice(cid){
  if(myVoiceChannel===cid){
    await leaveGroupVoice();
  }else{
    await joinGroupVoice(cid);
  }
}

async function joinGroupVoice(cid){
  if(!activeGroup||!connected)return;
  if(typeof GroupVoice!=='undefined'){
    await GroupVoice.join(activeGroup.groupId,cid);
  }else{
    // Fallback without WebRTC
    if(myVoiceChannel) await leaveGroupVoice();
    try{
      await connection.invoke('JoinGroupVoice',activeGroup.groupId,cid);
      myVoiceChannel=cid;
      var key=voiceKey(cid);
      if(!groupVoiceUsers[key])groupVoiceUsers[key]=[];
      if(groupVoiceUsers[key].indexOf(me.username)===-1)groupVoiceUsers[key].push(me.username);
      renderGroupSidebar();
    }catch(e){alert('Kunde inte ansluta: '+(e.message||''));}
  }
}

async function leaveGroupVoice(){
  if(typeof GroupVoice!=='undefined'){
    await GroupVoice.leave();
  }else{
    if(!activeGroup||!connected||!myVoiceChannel)return;
    try{await connection.invoke('LeaveGroupVoice',activeGroup.groupId,myVoiceChannel)}catch(e){console.error(e)}
    var key=voiceKey(myVoiceChannel);
    if(groupVoiceUsers[key]) groupVoiceUsers[key]=groupVoiceUsers[key].filter(function(u){return u!==me.username});
    myVoiceChannel=null;
    renderGroupSidebar();
  }
}

// SignalR listeners for groups
connection.on('ReceiveGroupMessage',function(p){
  if(!p)return;
  var cid=p.channelId;
  if(!groupMessages.has(cid))groupMessages.set(cid,[]);
  var l=groupMessages.get(cid);
  if(!l.some(function(m){return m.id===p.id})){
    l.push({id:p.id,from:p.from,text:p.text,ts:p.ts});
    if(activeChannel===cid&&currentView==='group') renderGroupMessages(cid);
    if(p.from!==me.username) playSound();
  }
});

connection.on('ReceiveGroupTyping',function(user){
  if(currentView==='group'&&user!==me.username) showTyping(user);
});

connection.on('GroupVoiceUserJoined',function(groupId,channelId,user){
  var key=groupId+':'+channelId;
  if(!groupVoiceUsers[key])groupVoiceUsers[key]=[];
  if(groupVoiceUsers[key].indexOf(user)===-1)groupVoiceUsers[key].push(user);
  if(currentView==='group'&&activeGroup&&activeGroup.groupId===groupId) renderGroupSidebar();
});

connection.on('GroupVoiceUserLeft',function(groupId,channelId,user){
  var key=groupId+':'+channelId;
  if(groupVoiceUsers[key]){
    groupVoiceUsers[key]=groupVoiceUsers[key].filter(function(u){return u!==user});
  }
  if(user===me.username) myVoiceChannel=null;
  if(currentView==='group'&&activeGroup&&activeGroup.groupId===groupId) renderGroupSidebar();
});

// Override sendMessage for group context
var _origSendMessage=sendMessage;
sendMessage=async function(){
  if(currentView==='group'){await sendGroupMessage();return}
  await _origSendMessage();
};

// Override updateComposer for group context
var _origUpdateComposer=updateComposer;
updateComposer=function(){
  if(currentView==='group'){
    var en=!!me&&connected&&!!activeChannel;
    el.msg.disabled=!en;
    el.sendBtn.disabled=!en;
    el.imageBtn.disabled=true;
    el.imageInput.disabled=true;
    el.clearImageBtn.disabled=true;
    var chName='kanal';
    if(activeGroup&&activeGroup.channels){
      var found=activeGroup.channels.find(function(c){return c.channelId===activeChannel});
      if(found)chName=found.name;
    }
    el.msg.placeholder='Skicka i #'+chName;
    el.composerHint.textContent=activeGroup?activeGroup.name:'Grupp';
    return;
  }
  _origUpdateComposer();
};

// Override switchView for proper cleanup
var _origSwitchView=switchView;
switchView=function(v){
  currentView=v;
  document.getElementById('dmSidebar').style.display=v==='dm'?'':'none';
  document.getElementById('groupSidebar').style.display=v==='group'?'':'none';
  renderServerList();
  if(v==='dm'){
    activeGroup=null;
    activeChannel=null;
    document.getElementById('chatIcon').textContent='@';
    document.getElementById('chatTopic').style.display='none';
    if(currentPeer){
      renderMessages(currentPeer);
      document.getElementById('chatTitle').textContent='@'+currentPeer;
    }else{
      document.getElementById('chatTitle').textContent='V\u00e4lj en konversation';
      el.messages.innerHTML='<div class="empty-state"><h2>Redo att chatta</h2><p>V\u00e4lj en konversation eller skapa en grupp.</p></div>';
    }
    updateComposer();
  }
};
// Load groups on startup

async function saveGroupSettings(){
  if(!activeGroup)return;
  var ni=document.querySelector('#tab-overview .form-input');
  var di=document.querySelector('#tab-overview .form-textarea');
  var name=(ni?ni.value:'').trim();
  var desc=(di?di.value:'').trim();
  try{
    var r=await fetch('/api/groups/'+encodeURIComponent(activeGroup.groupId),{method:'PATCH',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:name||undefined,description:desc})});
    if(!r.ok){var d=await r.json().catch(function(){return{}});alert(d.message||'Fel.');return}
    alert('Sparat!');activeGroup.name=name||activeGroup.name;activeGroup.description=desc;renderGroupSidebar();renderServerList();
  }catch(e){alert('N\u00e4tverksfel.')}
}

async function createChannel(){
  var name=prompt('Kanalnamn:');if(!name||name.trim().length<2)return;
  var type=confirm('R\u00f6stkanal? (OK=r\u00f6st, Avbryt=text)')?'voice':'text';
  if(!activeGroup)return;
  try{
    var r=await fetch('/api/groups/'+encodeURIComponent(activeGroup.groupId)+'/channels',{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:name.trim(),type:type})});
    var d=await r.json().catch(function(){return{}});if(!r.ok){alert(d.message||'Fel.');return}
    await activateGroup(activeGroup.groupId);
  }catch(e){alert('N\u00e4tverksfel.')}
}

async function kickMember(username){
  if(!activeGroup||!confirm('Sparka '+username+'?'))return;
  try{
    var r=await fetch('/api/groups/'+encodeURIComponent(activeGroup.groupId)+'/kick/'+encodeURIComponent(username),{method:'POST',credentials:'include'});
    if(!r.ok){var d=await r.json().catch(function(){return{}});alert(d.message||'Fel.');return}
    alert('Sparkad.');await activateGroup(activeGroup.groupId);
  }catch(e){alert('N\u00e4tverksfel.')}
}

async function leaveGroup(){
  if(!activeGroup||!confirm('L\u00e4mna gruppen?'))return;
  try{
    var r=await fetch('/api/groups/'+encodeURIComponent(activeGroup.groupId)+'/leave',{method:'POST',credentials:'include'});
    if(!r.ok){var d=await r.json().catch(function(){return{}});alert(d.message||'Fel.');return}
    closeModal('settingsModal');activeGroup=null;activeChannel=null;switchView('dm');await loadMyGroups();
  }catch(e){alert('N\u00e4tverksfel.')}
}

async function deleteGroup(){
  if(!activeGroup||!confirm('Ta bort gruppen permanent? Detta kan inte \u00e5ngras.'))return;
  try{
    var r=await fetch('/api/groups/'+encodeURIComponent(activeGroup.groupId),{method:'DELETE',credentials:'include'});
    if(!r.ok){var d=await r.json().catch(function(){return{}});alert(d.message||'Fel.');return}
    closeModal('settingsModal');activeGroup=null;activeChannel=null;switchView('dm');await loadMyGroups();
  }catch(e){alert('N\u00e4tverksfel.')}
}
