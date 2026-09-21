const products = [
  { id: 1, name: 'Tomates grappes', category: 'Légumes', price: 2.49, unit: 'la barquette', image: 'https://images.unsplash.com/photo-1546094096-0df4bcaaa337?auto=format&fit=crop&w=600&q=85' },
  { id: 2, name: 'Avocats mûrs à point', category: 'Légumes', price: 3.2, unit: 'les 2 pièces', image: 'https://images.unsplash.com/photo-1523049673857-eb18f1d7b578?auto=format&fit=crop&w=600&q=85' },
  { id: 3, name: 'Carottes nouvelles', category: 'Légumes', price: 1.79, unit: 'le kilo', image: 'https://images.unsplash.com/photo-1445282768818-728615cc910a?auto=format&fit=crop&w=600&q=85' },
  { id: 4, name: 'Poulet fermier', category: 'Viandes', price: 9.9, unit: 'le kilo', image: 'https://images.unsplash.com/photo-1604503468506-a8da13d82791?auto=format&fit=crop&w=600&q=85' },
  { id: 5, name: 'Bœuf haché', category: 'Viandes', price: 8.45, unit: 'les 500 g', image: 'https://images.unsplash.com/photo-1588347818036-558601350947?auto=format&fit=crop&w=600&q=85' },
  { id: 6, name: 'Œufs plein air', category: 'Produits laitiers', price: 3.6, unit: 'les 6 œufs', image: 'https://images.unsplash.com/photo-1582722872445-44dc5f7e3c8f?auto=format&fit=crop&w=600&q=85' },
  { id: 7, name: 'Yaourt nature', category: 'Produits laitiers', price: 2.15, unit: 'les 4 pots', image: 'https://images.unsplash.com/photo-1571212515416-fef01fc43637?auto=format&fit=crop&w=600&q=85' },
  { id: 8, name: 'Comté affiné', category: 'Produits laitiers', price: 5.9, unit: 'les 250 g', image: 'https://images.unsplash.com/photo-1452195100486-9cc805987862?auto=format&fit=crop&w=600&q=85' },
  { id: 9, name: 'Huile d’olive vierge', category: 'Épicerie', price: 7.8, unit: 'la bouteille', image: 'https://images.unsplash.com/photo-1474979266404-7eaacbcd87c5?auto=format&fit=crop&w=600&q=85' },
  { id: 10, name: 'Pâtes artisanales', category: 'Épicerie', price: 2.4, unit: 'le paquet', image: 'https://images.unsplash.com/photo-1551462147-ff29053bfc14?auto=format&fit=crop&w=600&q=85' },
  { id: 11, name: 'Miel de fleurs', category: 'Épicerie', price: 6.5, unit: 'le pot', image: 'https://images.unsplash.com/photo-1587049352846-4a222e784d38?auto=format&fit=crop&w=600&q=85' },
  { id: 12, name: 'Pain au levain', category: 'Épicerie', price: 3.1, unit: 'la pièce', image: 'https://images.unsplash.com/photo-1509440159596-0249088772ff?auto=format&fit=crop&w=600&q=85' },
];

const cart = new Map();
const euro = (value) => `${value.toFixed(2).replace('.', ',')} €`;
const productGrid = document.querySelector('#product-grid');
const cartContent = document.querySelector('#cart-content');
const toast = document.querySelector('.toast');

function renderProducts(category = 'Tous') {
  const visible = category === 'Tous' ? products : products.filter((product) => product.category === category);
  productGrid.innerHTML = visible.map((product) => `
    <article class="product-card">
      <img class="product-image" src="${product.image}" alt="${product.name}" loading="lazy" />
      <div class="product-info">
        <span class="product-category">${product.category}</span>
        <h3 class="product-name">${product.name}</h3>
        <div class="product-bottom">
          <span class="product-price">${euro(product.price)} <small>${product.unit}</small></span>
          <button class="add-button" data-add="${product.id}" aria-label="Ajouter ${product.name} au panier">+</button>
        </div>
      </div>
    </article>
  `).join('');
}

function cartEntries() {
  return [...cart.entries()].map(([id, quantity]) => ({ ...products.find((product) => product.id === id), quantity }));
}

function renderCart() {
  const entries = cartEntries();
  const totalItems = entries.reduce((sum, item) => sum + item.quantity, 0);
  document.querySelectorAll('.cart-count').forEach((element) => { element.textContent = totalItems; });
  if (!entries.length) {
    cartContent.innerHTML = '<div class="empty-cart"><div aria-hidden="true" style="font-size:36px">◌</div><p>Votre panier est encore vide.<br />Ajoutez quelques essentiels pour commencer.</p><button data-view="catalogue">Découvrir le catalogue</button></div>';
    return;
  }
  const subtotal = entries.reduce((sum, item) => sum + item.price * item.quantity, 0);
  cartContent.innerHTML = `
    <div class="cart-list">${entries.map((item) => `
      <article class="cart-row">
        <img src="${item.image}" alt="" />
        <div><h3>${item.name}</h3><p>${euro(item.price)} <small>/ ${item.unit}</small></p></div>
        <div class="quantity-control">
          <button data-decrease="${item.id}" aria-label="Retirer une unité de ${item.name}">−</button>
          <span>${item.quantity}</span>
          <button data-increase="${item.id}" aria-label="Ajouter une unité de ${item.name}">+</button>
        </div>
      </article>
    `).join('')}</div>
    <div class="cart-summary">
      <div class="summary-line"><span>Sous-total</span><span>${euro(subtotal)}</span></div>
      <div class="summary-line"><span>Livraison</span><span>Gratuite</span></div>
      <div class="summary-total"><span>Total estimé</span><span>${euro(subtotal)}</span></div>
    </div>
  `;
}

function showToast(message) {
  toast.textContent = message;
  toast.classList.add('is-visible');
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => toast.classList.remove('is-visible'), 1800);
}

function setView(view) {
  document.querySelectorAll('[data-page]').forEach((page) => page.classList.toggle('is-hidden', page.dataset.page !== view));
  document.querySelectorAll('.bottom-nav-item').forEach((item) => item.classList.toggle('is-active', item.dataset.view === view));
  if (view === 'cart') renderCart();
  window.scrollTo({ top: 0, behavior: 'smooth' });
}

document.addEventListener('click', (event) => {
  const add = event.target.closest('[data-add]');
  const increase = event.target.closest('[data-increase]');
  const decrease = event.target.closest('[data-decrease]');
  const view = event.target.closest('[data-view]');
  if (add) {
    const id = Number(add.dataset.add);
    cart.set(id, (cart.get(id) || 0) + 1);
    renderCart();
    showToast(`${products.find((product) => product.id === id).name} ajouté au panier`);
  }
  if (increase || decrease) {
    const id = Number((increase || decrease).dataset[increase ? 'increase' : 'decrease']);
    const next = (cart.get(id) || 0) + (increase ? 1 : -1);
    next > 0 ? cart.set(id, next) : cart.delete(id);
    renderCart();
  }
  if (view) setView(view.dataset.view);
  if (event.target.closest('.category-tab')) {
    document.querySelectorAll('.category-tab').forEach((tab) => tab.classList.remove('is-active'));
    event.target.closest('.category-tab').classList.add('is-active');
    renderProducts(event.target.closest('.category-tab').dataset.category);
  }
});

renderProducts();
renderCart();
if ('serviceWorker' in navigator) window.addEventListener('load', () => navigator.serviceWorker.register('sw.js'));
