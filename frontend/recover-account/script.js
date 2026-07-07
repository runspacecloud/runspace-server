(function () {
  var form = document.getElementById('recover-form');
  var btn = document.getElementById('btn-recover');
  var btnAgain = document.getElementById('btn-download-again');
  var statusEl = document.getElementById('status');

  var lastKeyFileBytes = null;
  var lastPreview = '';

  function setStatus(text, color) {
    statusEl.textContent = text || '';
    statusEl.style.color = color || '#8fa3bd';
  }

  function uuidv4() {
    var bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);

    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;

    var hex = Array.from(bytes).map(function (b) {
      return b.toString(16).padStart(2, '0');
    });

    return [
      hex.slice(0, 4).join(''),
      hex.slice(4, 6).join(''),
      hex.slice(6, 8).join(''),
      hex.slice(8, 10).join(''),
      hex.slice(10, 16).join('')
    ].join('-');
  }

  async function encryptAccountKeyFile(accountKey, passphrase) {
    var enc = new TextEncoder();

    var salt = new Uint8Array(16);
    crypto.getRandomValues(salt);

    var iv = new Uint8Array(12);
    crypto.getRandomValues(iv);

    var baseKey = await crypto.subtle.importKey(
      'raw',
      enc.encode(passphrase),
      'PBKDF2',
      false,
      ['deriveKey']
    );

    var aesKey = await crypto.subtle.deriveKey(
      {
        name: 'PBKDF2',
        salt: salt,
        iterations: 100000,
        hash: 'SHA-256'
      },
      baseKey,
      {
        name: 'AES-GCM',
        length: 256
      },
      false,
      ['encrypt']
    );

    var ciphertext = new Uint8Array(await crypto.subtle.encrypt(
      {
        name: 'AES-GCM',
        iv: iv
      },
      aesKey,
      enc.encode(accountKey)
    ));

    var out = new Uint8Array(4 + salt.length + iv.length + ciphertext.length);

    out.set([0x52, 0x53, 0x4b, 0x31], 0); // RSK1
    out.set(salt, 4);
    out.set(iv, 20);
    out.set(ciphertext, 32);

    return out;
  }

  function downloadKeyFile(bytes) {
    if (!bytes) {
      alert('No .key file is available yet.');
      return;
    }

    var blob = new Blob([bytes], { type: 'application/octet-stream' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');

    var date = new Date().toISOString().slice(0, 10);
    a.href = url;
    a.download = 'runspace-account-key-' + date + '.key';

    document.body.appendChild(a);
    a.click();
    a.remove();

    URL.revokeObjectURL(url);
  }

  if (btnAgain) {
    btnAgain.addEventListener('click', function () {
      downloadKeyFile(lastKeyFileBytes);
    });
  }

  form.addEventListener('submit', async function (e) {
    e.preventDefault();

    var username = document.getElementById('username').value.trim().toLowerCase();
    var recoveryCode = document.getElementById('recovery-code').value.trim().toLowerCase();
    var passphrase = document.getElementById('passphrase').value;
    var passphrase2 = document.getElementById('passphrase2').value;

    lastKeyFileBytes = null;
    lastPreview = '';
    if (btnAgain) btnAgain.style.display = 'none';

    if (!username || !recoveryCode || !passphrase || !passphrase2) {
      setStatus('Fill in all fields.', '#fca5a5');
      return;
    }

    if (passphrase.length < 8) {
      setStatus('Passphrase must be at least 8 characters.', '#fca5a5');
      return;
    }

    if (passphrase !== passphrase2) {
      setStatus('Passphrases do not match.', '#fca5a5');
      return;
    }

    if (!/^rsr-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}$/i.test(recoveryCode)) {
      setStatus('Recovery code format looks wrong.', '#fca5a5');
      return;
    }

    btn.disabled = true;
    setStatus('Creating encrypted .key file locally...', '#8fa3bd');

    try {
      var newAccountKey = uuidv4();
      var encryptedFile = await encryptAccountKeyFile(newAccountKey, passphrase);

      setStatus('Activating new Account Key on server...', '#8fa3bd');

      var res = await fetch('/api/auth/account-recovery/redeem', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          username: username,
          recoveryCode: recoveryCode,
          accountKey: newAccountKey
        })
      });

      var data = {};
      try { data = await res.json(); } catch (_) {}

      if (!res.ok || !data.ok) {
        throw new Error(data.message || 'Could not recover account.');
      }

      lastKeyFileBytes = encryptedFile;
      lastPreview = data.preview || (newAccountKey.slice(0, 8) + '...');

      downloadKeyFile(lastKeyFileBytes);

      if (btnAgain) btnAgain.style.display = 'block';

      setStatus('Account recovered. New .key file downloaded. New key preview: ' + lastPreview + ' Old sessions were logged out.', '#22c55e');

      document.getElementById('recovery-code').value = '';
      document.getElementById('passphrase').value = '';
      document.getElementById('passphrase2').value = '';
    } catch (err) {
      setStatus(err.message || 'Recovery failed.', '#fca5a5');
    } finally {
      btn.disabled = false;
    }
  });
})();
