<script>
const blockedProfileLinkDomains = [
  "grabify.link",
  "iplogger.org",
  "iplogger.com",
  "2no.co",
  "yip.su",
  "blasze.com",

  "bit.ly",
  "tinyurl.com",
  "cutt.ly",
  "shorturl.at",
  "rebrand.ly",

  "pornhub.com",
  "xvideos.com",
  "xnxx.com",
  "onlyfans.com",

  "stake.com",
  "roobet.com",
  "bc.game",

  "pastebin.com",
  "rentry.co",
  "hastebin.com",

  "anonfiles.com",
  "gofile.io",
  "mega.nz",
  "mediafire.com"
];

const blockedProfileLinkKeywords = [
  "discord-token",
  "token-grabber",
  "grabber",
  "stealer",
  "keylogger",
  "malware",
  "phishing",
  "free-nitro",
  "airdrop",
  "wallet-drain",
  "walletdrain",
  "crack",
  "cheat"
];

function normalizeProfileUrl(rawUrl) {
  let value = String(rawUrl || "").trim();

  if (!value) return null;

  if (!/^https?:\/\//i.test(value)) {
    value = "https://" + value;
  }

  try {
    const url = new URL(value);
    url.hostname = url.hostname.toLowerCase();

    if (url.hostname.startsWith("www.")) {
      url.hostname = url.hostname.slice(4);
    }

    return url;
  } catch {
    return null;
  }
}

function isBlockedProfileLink(rawUrl) {
  const url = normalizeProfileUrl(rawUrl);

  if (!url) {
    return {
      blocked: true,
      reason: "Invalid URL"
    };
  }

  if (!["http:", "https:"].includes(url.protocol)) {
    return {
      blocked: true,
      reason: "Only HTTP/HTTPS links are allowed"
    };
  }

  const host = url.hostname;
  const full = url.href.toLowerCase();

  const blockedDomain = blockedProfileLinkDomains.find(domain => {
    return host === domain || host.endsWith("." + domain);
  });

  if (blockedDomain) {
    return {
      blocked: true,
      reason: "This domain is not allowed"
    };
  }

  const blockedKeyword = blockedProfileLinkKeywords.find(keyword => {
    return full.includes(keyword);
  });

  if (blockedKeyword) {
    return {
      blocked: true,
      reason: "This link contains blocked content"
    };
  }

  return {
    blocked: false,
    reason: "",
    normalizedUrl: url.href
  };
}

function validateProfileLinks(links) {
  const cleanLinks = [];
  const blockedLinks = [];

  for (const link of links) {
    const result = isBlockedProfileLink(link);

    if (result.blocked) {
      blockedLinks.push({
        url: link,
        reason: result.reason
      });
    } else {
      cleanLinks.push(result.normalizedUrl);
    }
  }

  return {
    ok: blockedLinks.length === 0,
    cleanLinks,
    blockedLinks
  };
}
</script>
