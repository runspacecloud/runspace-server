(function(){
  let username = '';
  const ITER = 310000;
  const $ = id => document.getElementById(id);

  function status(text, cls){
    const el = $('status');
    el.textContent = text || '';
    el.className = cls || '';
  }

  function b64(bytes){
    let s = '';
    for(let i = 0; i < bytes.length; i += 32768){
      s += String.fromCharCode.apply(null, bytes.subarray(i, i + 32768));
    }
    return btoa(s);
  }

  async function deriveWrapKey(passphrase, salt, usages){
    const enc = new TextEncoder();
    const base = await crypto.subtle.importKey(
      'raw',
      enc.encode(passphrase),
      { name: 'PBKDF2' },
      false,
      ['deriveKey']
    );

    return crypto.subtle.deriveKey(
      { name: 'PBKDF2', salt, iterations: ITER, hash: 'SHA-256' },
      base,
      { name: 'AES-GCM', length: 256 },
      false,
      usages
    );
  }

  async function encryptPrivateKey(privateJwk, passphrase){
    const salt = crypto.getRandomValues(new Uint8Array(16));
    const nonce = crypto.getRandomValues(new Uint8Array(12));
    const key = await deriveWrapKey(passphrase, salt, ['encrypt']);
    const plain = new TextEncoder().encode(JSON.stringify(privateJwk));
    const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce }, key, plain);

    return {
      encryptedPrivateKey: b64(new Uint8Array(cipher)),
      salt: b64(salt),
      nonce: b64(nonce),
      kdf: 'PBKDF2-SHA256',
      iterations: ITER,
      version: 1
    };
  }

  async function loadMe(){
    try {
      const r = await fetch('/api/me', { credentials: 'include' });

      if (!r.ok) {
        $('btn').disabled = true;
        status('Sign in first, then come back here.', 'bad');
        return;
      }

      const me = await r.json();
      username = (me.username || '').toLowerCase();

      if (!username) {
        $('btn').disabled = true;
        status('Could not detect your account.', 'bad');
        return;
      }

      status('Signed in as ' + username + '.', 'ok');
    } catch(e) {
      $('btn').disabled = true;
      status('Could not check your account.', 'bad');
    }
  }

  function validate(){
    const p1 = $('p1').value || '';
    const p2 = $('p2').value || '';
    const confirmText = ($('confirm').value || '').trim();

    if (!username) return status('No signed-in account found.', 'bad'), false;
    if (p1.length < 8) return status('Passphrase must be at least 8 characters.', 'bad'), false;
    if (p1 !== p2) return status('Passphrases do not match.', 'bad'), false;
    if (!$('ok').checked) return status('Confirm the warning before continuing.', 'bad'), false;
    if (confirmText !== 'RESET') return status('Type RESET to continue.', 'bad'), false;

    return true;
  }

  async function reset(){
    if (!validate()) return;

    const passphrase = $('p1').value;

    if (!confirm('Create a new encryption passphrase for future messages? Some older encrypted messages may no longer open.')) {
      return;
    }

    $('btn').disabled = true;
    status('Creating your new encrypted message keys...');

    try {
      const pair = await crypto.subtle.generateKey(
        {
          name: 'RSA-OAEP',
          modulusLength: 2048,
          publicExponent: new Uint8Array([1, 0, 1]),
          hash: 'SHA-256'
        },
        true,
        ['encrypt', 'decrypt']
      );

      const privateJwk = await crypto.subtle.exportKey('jwk', pair.privateKey);
      const publicSpki = await crypto.subtle.exportKey('spki', pair.publicKey);
      const envelope = await encryptPrivateKey(privateJwk, passphrase);

      const save = await fetch('/api/e2ee/account-key', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          publicKey: b64(new Uint8Array(publicSpki)),
          encryptedPrivateKey: envelope.encryptedPrivateKey,
          salt: envelope.salt,
          nonce: envelope.nonce,
          kdf: envelope.kdf,
          iterations: envelope.iterations,
          version: envelope.version,
          reset: true
        })
      });

      if (!save.ok) {
        throw new Error(await save.text());
      }

      try {
        sessionStorage.setItem('rs_e2ee_passphrase_' + username, passphrase);
      } catch(e) {}

      $('p1').value = '';
      $('p2').value = '';
      $('confirm').value = '';
      $('ok').checked = false;

      status('Done. Your new encryption passphrase is ready. For safety, sign in again before returning to chat.', 'ok');
    } catch(e) {
      $('btn').disabled = false;
      status('Reset failed: ' + (e.message || e), 'bad');
    }
  }

  $('btn').addEventListener('click', reset);
  loadMe();
})();
