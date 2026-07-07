const toast = document.getElementById("toast");

function showToast(message) {
  if (!toast) return;

  toast.textContent = message;
  toast.classList.add("show");

  window.clearTimeout(window.__premiumToastTimer);
  window.__premiumToastTimer = window.setTimeout(() => {
    toast.classList.remove("show");
  }, 2400);
}

function addRevealAnimation() {
  const items = document.querySelectorAll(
    ".premium-badge, .premium-hero h1, .premium-hero p, .premium-actions, .premium-card, .premium-panel"
  );

  items.forEach((item) => item.classList.add("premium-reveal"));

  const observer = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
      if (!entry.isIntersecting) return;
      entry.target.classList.add("visible");
      observer.unobserve(entry.target);
    });
  }, { threshold: 0.16 });

  items.forEach((item, index) => {
    item.style.transitionDelay = `${Math.min(index * 55, 260)}ms`;
    observer.observe(item);
  });
}

function addMouseGlow() {
  const cards = document.querySelectorAll(".premium-card, .premium-panel");

  cards.forEach((card) => {
    card.addEventListener("pointermove", (event) => {
      const rect = card.getBoundingClientRect();
      const x = ((event.clientX - rect.left) / rect.width) * 100;
      const y = ((event.clientY - rect.top) / rect.height) * 100;

      card.style.setProperty("--mx", `${x}%`);
      card.style.setProperty("--my", `${y}%`);
    });
  });
}

function addButtonRipples() {
  const buttons = document.querySelectorAll(".premium-primary, .premium-secondary");

  buttons.forEach((button) => {
    button.addEventListener("click", (event) => {
      const rect = button.getBoundingClientRect();
      const ripple = document.createElement("span");

      ripple.className = "premium-ripple";
      ripple.style.left = `${event.clientX - rect.left}px`;
      ripple.style.top = `${event.clientY - rect.top}px`;

      button.appendChild(ripple);
      window.setTimeout(() => ripple.remove(), 620);
    });
  });
}

document.getElementById("notifyBtn")?.addEventListener("click", () => {
  showToast("Premium notifications are not connected yet.");
});

document.getElementById("premiumBtn")?.addEventListener("click", () => {
  showToast("RunSpace Premium is coming soon.");
});

addRevealAnimation();
addMouseGlow();
addButtonRipples();


function addPricingAnimation() {
  const cards = document.querySelectorAll(".premium-price-card");

  const observer = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
      if (!entry.isIntersecting) return;

      const index = [...cards].indexOf(entry.target);
      entry.target.style.transitionDelay = `${Math.min(index * 120, 360)}ms`;
      entry.target.classList.add("visible");

      observer.unobserve(entry.target);
    });
  }, {
    threshold: 0.2,
    rootMargin: "0px 0px -80px 0px"
  });

  cards.forEach((card) => {
    observer.observe(card);

    card.addEventListener("pointermove", (event) => {
      const rect = card.getBoundingClientRect();
      const x = ((event.clientX - rect.left) / rect.width) * 100;
      const y = ((event.clientY - rect.top) / rect.height) * 100;

      card.style.setProperty("--mx", `${x}%`);
      card.style.setProperty("--my", `${y}%`);
    });
  });
}

document.querySelectorAll(".price-btn").forEach((button) => {
  button.addEventListener("click", () => {
    showToast("RunSpace Premium is not available yet.");
  });
});

addPricingAnimation();

function addBillingToggle() {
  const billingButtons = document.querySelectorAll(".premium-toggle button");
  const currencyButtons = document.querySelectorAll(".premium-currency-toggle button");
  const priceValues = document.querySelectorAll(".price-value span");
  const priceLabels = document.querySelectorAll(".price-value small");

  let currentBilling = "monthly";
  let currentCurrency = "eur";

  function animatePrice(price, nextPrice) {
    price.animate(
      [
        { opacity: 1, transform: "translateY(0)" },
        { opacity: 0, transform: "translateY(-8px)" }
      ],
      { duration: 120, easing: "ease-out" }
    ).onfinish = () => {
      price.textContent = nextPrice;

      price.animate(
        [
          { opacity: 0, transform: "translateY(8px)" },
          { opacity: 1, transform: "translateY(0)" }
        ],
        { duration: 180, easing: "ease-out" }
      );
    };
  }

  function updatePrices() {
    priceValues.forEach((price) => {
      const key = `${currentCurrency}${currentBilling[0].toUpperCase()}${currentBilling.slice(1)}`;
      const dataKey = currentCurrency + currentBilling.charAt(0).toUpperCase() + currentBilling.slice(1);
      const nextPrice = price.dataset[dataKey];

      if (nextPrice) {
        animatePrice(price, nextPrice);
      }
    });

    priceLabels.forEach((label) => {
      label.textContent = currentBilling === "yearly" ? "/ year" : "/ month";
    });
  }

  billingButtons.forEach((button) => {
    button.addEventListener("click", () => {
      currentBilling = button.dataset.billing || "monthly";

      billingButtons.forEach((item) => item.classList.remove("active"));
      button.classList.add("active");

      updatePrices();
    });
  });

  currencyButtons.forEach((button) => {
    button.addEventListener("click", () => {
      currentCurrency = button.dataset.currency || "eur";

      currencyButtons.forEach((item) => item.classList.remove("active"));
      button.classList.add("active");

      updatePrices();
    });
  });
}

addBillingToggle();

/* ===== RunSpace Premium Stripe Checkout ===== */

document.addEventListener("click", async (event) => {
  const button = event.target.closest("button, a");
  if (!button) return;

  const text = (button.textContent || "").toLowerCase().trim();
  const card = button.closest("[data-plan], .premium-price-card, .price-card, .plan-card, .tier-card");
  const cardText = (card?.textContent || "").toLowerCase();

  const looksLikePremiumButton =
    text.includes("premium") ||
    text.includes("upgrade") ||
    text.includes("get started") ||
    text.includes("coming soon") ||
    text.includes("choose");

  if (!looksLikePremiumButton) return;

  let plan = "";

  if (button.dataset.checkoutPlan) plan = button.dataset.checkoutPlan.toLowerCase();
  else if (button.dataset.plan) plan = button.dataset.plan.toLowerCase();
  else if (card?.dataset.plan) plan = card.dataset.plan.toLowerCase();
  else if (text.includes("premium+") || text.includes("plus") || cardText.includes("premium+")) plan = "plus";
  else if (text.includes("premium") || cardText.includes("premium")) plan = "premium";

  if (plan === "free" || cardText.includes("free")) {
    if (typeof showToast === "function") showToast("You are already on the Free plan.");
    return;
  }

  if (plan !== "premium" && plan !== "plus") return;

  event.preventDefault();
  event.stopImmediatePropagation();

  const billing =
    document.querySelector("[data-billing].active")?.dataset.billing ||
    document.querySelector(".premium-toggle button.active")?.dataset.billing ||
    document.querySelector(".billing-toggle button.active")?.dataset.billing ||
    "monthly";

  const oldText = button.textContent;
  button.disabled = true;
  button.textContent = "Opening checkout...";

  try {
    const res = await fetch("/api/billing/checkout", {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ plan, billing })
    });

    const data = await res.json().catch(() => ({}));

    if (res.status === 401) {
      if (typeof showToast === "function") showToast("Please log in before upgrading.");
      else alert("Please log in before upgrading.");
      button.disabled = false;
      button.textContent = oldText;
      return;
    }

    if (!res.ok || !data.url) {
      if (typeof showToast === "function") showToast(data.error || "Checkout is not ready yet.");
      else alert(data.error || "Checkout is not ready yet.");
      button.disabled = false;
      button.textContent = oldText;
      return;
    }

    window.location.href = data.url;
  } catch {
    if (typeof showToast === "function") showToast("Could not open checkout.");
    else alert("Could not open checkout.");
    button.disabled = false;
    button.textContent = oldText;
  }
}, true);

(() => {
  const params = new URLSearchParams(window.location.search);

  if (params.get("success") === "1") {
    if (typeof showToast === "function") showToast("Payment received. Premium status is updating.");
  }

  if (params.get("canceled") === "1") {
    if (typeof showToast === "function") showToast("Checkout was canceled.");
  }
})();
